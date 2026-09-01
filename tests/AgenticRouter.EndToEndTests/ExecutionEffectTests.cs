using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class ExecutionEffectTests
{
  [TestMethod]
  public void UnboundPlanActionIsARecoverablePlanningFailure()
  {
    var classifier = new PlanningFailureClassifier();

    Assert.AreEqual(
      CoordinatorFailureCategory.CorrectablePlanning,
      classifier.Classify(
        new LocalActionException(
          "plan-action-binding",
          "The proposed read does not advance the next step."
        )
      )
    );
  }

  [TestMethod]
  public void CoordinatorCompactsCompletedHistoryAndPreservesLatestToolPair()
  {
    var planner = new LocalActionPlanner(
      null!,
      new ToolNameResolver(),
      new ConservativeTokenEstimator(),
      new SpecialistToolingProtocol()
    );
    var toolingProfile = new SpecialistToolingProfile(
      SpecialistToolingProfileIds.GenericNative,
      "1",
      "provider-native-tools",
      "test tooling profile",
      false,
      true,
      "test"
    );
    var obsoleteOutput = new string('o', 12_000);
    var latestOutput = "Status: completed\nOutput:\ncurrent file contents";
    var messages = new List<OllamaToolMessage>
    {
      new(
        "system",
        "APPLICATION_OWNED_PROJECT_CONTEXT\nstatic project context"
      ),
      new("user", "Create the requested artifacts"),
      new("assistant", new string('a', 12_000)),
      new("tool", $"Status: completed\nOutput:\n{obsoleteOutput}", ToolName: "read_file")
    };
    for (var index = 1; index <= 3; index++)
    {
      messages.Add(new OllamaToolMessage("assistant", $"recent proposal {index}"));
      messages.Add(
        new OllamaToolMessage(
          "tool",
          $"Status: completed\nOutput:\nrecent result {index}",
          ToolName: "read_file"
        )
      );
    }
    messages.Add(new OllamaToolMessage("assistant", "latest assistant proposal"));
    messages.Add(new OllamaToolMessage("tool", latestOutput, ToolName: "read_file"));

    var fit = planner.FitToBudget(
      messages,
      toolingProfile,
      ["read_file"],
      "plan=pending; latest-action=read_file",
      100_000,
      false,
      1,
      false,
      true
    );

    Assert.IsTrue(fit.Compacted);
    Assert.AreEqual("compacted", fit.Outcome);
    Assert.IsLessThan(fit.BeforeTokens, fit.AfterTokens);
    Assert.IsTrue(
      fit.Messages.Any(message => message.Content == latestOutput)
    );
    Assert.IsFalse(
      fit.Messages.Any(message => message.Content?.Contains(obsoleteOutput) == true)
    );
  }

  [TestMethod]
  public void CoordinatorCompactionPreservesOriginalToolScopeAcrossRecoveryControl()
  {
    var planner = new LocalActionPlanner(
      null!,
      new ToolNameResolver(),
      new ConservativeTokenEstimator(),
      new SpecialistToolingProtocol()
    );
    var toolingProfile = new SpecialistToolingProfile(
      SpecialistToolingProfileIds.QwenCodeOllama,
      "1",
      "ollama-native-tools",
      "test tooling profile",
      false,
      true,
      "test"
    );
    const string objective =
      "Create the static browser game. Do not run it; I will test it manually.";
    var messages = new List<OllamaToolMessage>
    {
      new(
        "system",
        "APPLICATION_OWNED_PROJECT_CONTEXT\nValidation profile: none"
      ),
      new("user", objective)
    };
    for (var index = 0; index < 5; index++)
    {
      messages.Add(new OllamaToolMessage("assistant", $"proposal {index}"));
      messages.Add(
        new OllamaToolMessage(
          "tool",
          $"{{\"status\":\"completed\",\"output\":\"result {index}\"}}",
          ToolName: "read_file"
        )
      );
    }
    messages.Add(
      new OllamaToolMessage(
        "user",
        "RECOVERY_STRATEGY_REVISION\nReturn a materially revised strategy and validation steps."
      )
    );

    var fit = planner.FitToBudget(
      messages,
      toolingProfile,
      ["read_file"],
      "latest-action=read_file",
      100_000,
      false,
      1,
      false,
      true
    );

    Assert.IsTrue(fit.Compacted);
    Assert.IsTrue(
      fit.Messages.Any(message => message.Role == "user" && message.Content == objective)
    );
    Assert.IsTrue(
      fit.Messages.Any(message => message.Content?.StartsWith("RECOVERY_", StringComparison.Ordinal) == true)
    );
    var scope = ExecutionTurnToolPolicy.Resolve(
      fit.Messages.Select(message => (message.Role, message.Content))
    );
    Assert.IsFalse(scope.ProcessExecutionAllowed);
    Assert.IsFalse(scope.Allows("run_process"));
  }

  [TestMethod]
  public void QwenCodeProfileForbidsUnavailableProcessesAndGuidesMissingOutputCreation()
  {
    var profile = new SpecialistToolingProfileResolver().Resolve(
      new SpecialistToolingIdentity(
        "ollama-local",
        "qwen3-coder:30b",
        "digest",
        true,
        true,
        false
      )
    );
    StringAssert.Contains(
      profile.PromptInstructions,
      "never call run_process when the Host says process execution is unavailable"
    );
    StringAssert.Contains(
      profile.PromptInstructions,
      "do not repeat read_file for the same absent output path"
    );

    var schema = JsonSerializer.SerializeToElement(
      new
      {
        type = "object",
        properties = new { },
        required = Array.Empty<string>()
      }
    );
    var definitions = new SpecialistToolingProtocol().ToOllamaDefinitions(
      profile,
      [
        new CanonicalToolDefinition("read_file", "read", schema),
        new CanonicalToolDefinition("create_file", "create", schema)
      ]
    );
    StringAssert.Contains(
      definitions.Single(tool => tool.Name == "read_file").Description,
      "do not repeat this read"
    );
    StringAssert.Contains(
      definitions.Single(tool => tool.Name == "create_file").Description,
      "required output path is absent"
    );
  }

  [TestMethod]
  public void OrdinaryPlanBindingsAreAdvisoryWithoutSemanticInference()
  {
    var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    var stylePath = Path.Combine(workspace, "style.css");
    File.WriteAllText(stylePath, "body { color: black; }");
    try
    {
      var session = CreateSession("Create browser artifacts", workspace);
      session.CreatePlan(
        new ExecutionPlanView(
          "Create browser artifacts",
          [
            new ExecutionPlanStep(
              "style",
              "Create stylesheet [target: style.css]",
              "pending"
            ),
            new ExecutionPlanStep(
              "script",
              "Create behavior [target: app.js]",
              "pending"
            )
          ],
          null,
          0,
          0
        )
      );
      var missingBinding = session.ResolvePlanActionBinding(null);
      Assert.AreEqual(HostPlanBindingStates.Unbound, missingBinding.State);
      Assert.IsNull(missingBinding.EffectiveStepId);
      var explicitBinding = session.ResolvePlanActionBinding("style");
      Assert.AreEqual(HostPlanBindingStates.Explicit, explicitBinding.State);
      Assert.AreEqual("style", explicitBinding.EffectiveStepId);
      var invalidBinding = session.ResolvePlanActionBinding("missing");
      Assert.AreEqual(HostPlanBindingStates.Unbound, invalidBinding.State);
      Assert.IsNull(invalidBinding.EffectiveStepId);

      var completed = CreateSession("Completed browser artifacts", workspace);
      completed.CreatePlan(
        new ExecutionPlanView(
          "Completed browser artifacts",
          [new ExecutionPlanStep("done", "Completed step", "completed")],
          null,
          1,
          0
        )
      );
      var completedBinding = completed.ResolvePlanActionBinding(null);
      Assert.AreEqual(HostPlanBindingStates.Unbound, completedBinding.State);
      Assert.IsNull(completedBinding.EffectiveStepId);

      var single = CreateSession("Single actionable step", workspace);
      single.CreatePlan(
        new ExecutionPlanView(
          "Single actionable step",
          [
            new ExecutionPlanStep("done", "Completed", "completed"),
            new ExecutionPlanStep("next", "Continue", "pending")
          ],
          null,
          1,
          0
        )
      );
      var automatic = single.ResolvePlanActionBinding(null);
      Assert.AreEqual(HostPlanBindingStates.Auto, automatic.State);
      Assert.AreEqual("next", automatic.EffectiveStepId);
      var corrected = single.ResolvePlanActionBinding("done");
      Assert.AreEqual(HostPlanBindingStates.Corrected, corrected.State);
      Assert.AreEqual("next", corrected.EffectiveStepId);
    }
    finally
    {
      Directory.Delete(workspace, true);
    }
  }

  [TestMethod]
  public void OrdinaryHostResultUsesCompactTypedEnvelope()
  {
    var session = CreateSession("Inspect workspace");
    session.CreatePlan(
      new ExecutionPlanView(
        "Inspect workspace",
        [new ExecutionPlanStep("inspect", "Inspect", "pending")],
        null,
        0,
        0
      )
    );
    using var arguments = JsonDocument.Parse("{}");
    var action = new ValidatedLocalAction(
      "action-1",
      "search_text",
      arguments.RootElement.Clone(),
      null,
      null,
      "search",
      null,
      true,
      false,
      PlanStepId: "inspect",
      PlanBindingState: HostPlanBindingStates.Auto
    );

    var result = HostActionResultAdapter.FromLegacy(
      "No matches.",
      true,
      "SEARCH_COMPLETED",
      session,
      action
    );
    var serialized = result.Serialize();
    using var parsed = JsonDocument.Parse(serialized);

    Assert.AreEqual(1, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
    Assert.AreEqual("succeeded", parsed.RootElement.GetProperty("outcome").GetString());
    Assert.AreEqual("SEARCH_COMPLETED", parsed.RootElement.GetProperty("code").GetString());
    Assert.AreEqual(
      HostPlanBindingStates.Auto,
      parsed.RootElement.GetProperty("plan").GetProperty("bindingCorrection").GetProperty("state").GetString()
    );
    Assert.IsFalse(parsed.RootElement.GetProperty("plan").TryGetProperty("full", out _));
    Assert.IsFalse(serialized.Contains("HOST_OWNED_PLAN_STATE", StringComparison.Ordinal));
  }

  [TestMethod]
  public void TimeoutAndApprovalOutcomeCodesRemainDistinct()
  {
    var codes = new[]
    {
      HostActionCodes.ApprovalPending,
      HostActionCodes.ApprovalRejected,
      HostActionCodes.ApprovalExpired,
      HostActionCodes.ProcessTimeout,
      HostActionCodes.HarnessStall,
      HostActionCodes.TurnTimeout,
      HostActionCodes.UserCancelled
    };
    Assert.HasCount(codes.Length, codes.Distinct(StringComparer.Ordinal));
  }

  [TestMethod]
  public void CanonicalRegistryCoversSchemasAliasesAndEffectiveHarnessCapabilities()
  {
    var resolver = new ToolNameResolver();
    Assert.HasCount(resolver.CanonicalTools.Count, resolver.Registrations);
    Assert.HasCount(
      resolver.CanonicalTools.Count,
      resolver.Registrations.Select(item => item.CanonicalName).Distinct(StringComparer.OrdinalIgnoreCase)
    );
    var definitions = LocalActionPlanner.GetToolDefinitions(resolver.CanonicalTools);
    var definitionNames = definitions.Select(item => item.Name)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var canonical in resolver.CanonicalTools.Where(
      tool => tool != LocalActionPlanner.RequestToolsetTool
    ))
    {
      Assert.Contains(canonical, definitionNames);
    }
    Assert.IsEmpty(
      resolver.Aliases.Where(alias => definitionNames.Contains(alias.Alias))
    );
    foreach (var alias in resolver.Aliases)
    {
      var resolution = resolver.Resolve(alias.Alias, resolver.CanonicalTools);
      Assert.AreEqual(alias.CanonicalTool, resolution.CanonicalName);
      Assert.AreEqual(ToolNameResolver.CuratedAliasSource, resolution.Source);
    }

    var profile = HostCapabilityProfile.Create(
      ExecutionTurnToolPolicy.Resolve(
        string.Empty,
        validationProfileAvailable: true,
        webSearchAvailable: true,
        diagnosticTraceAvailable: true
      ),
      "auto"
    );
    var fullDefinitions = LocalActionPlanner.GetToolDefinitions(profile.ToolScope.AvailableTools);
    var fullCharacters = SchemaCharacters(fullDefinitions);
    foreach (var harness in new[]
    {
      HarnessIds.Native,
      HarnessIds.Codex,
      HarnessIds.OpenCode,
      HarnessIds.QwenCode,
      HarnessIds.ClaudeCode
    })
    {
      CollectionAssert.AreEquivalent(
        profile.ToolScope.AvailableTools.ToArray(),
        HarnessCapabilityProjection.EffectiveCanonicalCapabilities(harness, profile).ToArray()
      );
      var projected = LocalActionPlanner.GetToolDefinitions(
        HarnessCapabilityProjection.HostBridgeTools(harness, profile)
      );
      var projectedCharacters = SchemaCharacters(projected);
      Assert.IsLessThanOrEqualTo(fullCharacters, projectedCharacters);
      Console.WriteLine(
        $"projection harness={harness} authoritative={fullDefinitions.Count} projected={projected.Count} fullChars={fullCharacters} projectedChars={projectedCharacters}"
      );
    }
  }

  [TestMethod]
  public void UnknownToolHandlingSuggestsOnlyClosedUnambiguousCapabilities()
  {
    var resolver = new ToolNameResolver();
    var unambiguous = Assert.ThrowsExactly<LocalActionException>(
      () => resolver.Resolve("file-read", resolver.ExecutableTools)
    );
    Assert.AreEqual("read_file", unambiguous.ProposedCanonicalTool);
    StringAssert.Contains(unambiguous.Message, "Use canonical tool 'read_file'");

    var ambiguous = Assert.ThrowsExactly<LocalActionException>(
      () => resolver.Resolve("file-mutation", resolver.ExecutableTools)
    );
    Assert.IsNull(ambiguous.ProposedCanonicalTool);
    StringAssert.Contains(ambiguous.Message, "capability family 'file-mutation'");

    var unknown = Assert.ThrowsExactly<LocalActionException>(
      () => resolver.Resolve("read_flie", resolver.ExecutableTools)
    );
    Assert.IsNull(unknown.ProposedCanonicalTool);
    Assert.IsFalse(unknown.Message.Contains("read_file", StringComparison.Ordinal));
  }

  private static int SchemaCharacters(IReadOnlyList<CanonicalToolDefinition> definitions)
  {
    return definitions.Sum(
      definition => definition.Name.Length
        + definition.Description.Length
        + definition.Parameters.GetRawText().Length
    );
  }

  [TestMethod]
  public void ExactDeterministicRepeatRequiresUnchangedArgumentsAndRelevantState()
  {
    var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    try
    {
      var session = CreateSession("Read missing file", workspace);
      using var firstArguments = JsonDocument.Parse("{\"path\":\"missing.txt\",\"query\":\"needle\"}");
      using var reorderedArguments = JsonDocument.Parse("{\"query\":\"needle\",\"path\":\"missing.txt\"}");
      var failure = HostActionResultAdapter.FromLegacy(
        "The file does not exist.",
        false,
        "ACTION_VALIDATION",
        session,
        retryUnchanged: false,
        evidenceId: "call-1"
      );
      session.RecordDeterministicFailure(
        "search_text",
        firstArguments.RootElement,
        failure.Code,
        "call-1",
        "Create the file or change the path.",
        failure
      );

      Assert.IsTrue(
        session.TryGetDeterministicRepeat(
          "search_text",
          reorderedArguments.RootElement,
          out var repeat
        )
      );
      Assert.AreEqual("call-1", repeat?.EvidenceId);

      File.WriteAllText(Path.Combine(workspace, "missing.txt"), "needle");
      Assert.IsFalse(
        session.TryGetDeterministicRepeat(
          "search_text",
          reorderedArguments.RootElement,
          out _
        )
      );
    }
    finally
    {
      Directory.Delete(workspace, true);
    }
  }

  [TestMethod]
  public async Task FileCreationAndReplacementConvergeOnlyAfterVerifiedPostconditions()
  {
    var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var session = CreateSession("Create and update item.txt", root);
      var service = new LocalActionService(
        new TestWorkspaceService(root),
        null!,
        null!,
        null!,
        null!,
        null!,
        new ToolNameResolver(),
        null!
      );
      using var createArguments = JsonDocument.Parse(
        "{\"path\":\"item.txt\",\"content\":\"alpha beta\"}"
      );
      var createProposal = new LocalActionProposal(
        "create_file",
        createArguments.RootElement.Clone(),
        null
      );
      var firstCreate = await service.ExecuteAsync(
        await service.ValidateAsync(createProposal, session, CancellationToken.None),
        session,
        CancellationToken.None
      );
      var secondCreate = await service.ExecuteAsync(
        await service.ValidateAsync(createProposal, session, CancellationToken.None),
        session,
        CancellationToken.None
      );
      Assert.AreEqual(HostActionOutcomes.Succeeded, firstCreate.Outcome);
      Assert.AreEqual(HostActionOutcomes.NoOp, secondCreate.Outcome);
      Assert.AreEqual("already_present", secondCreate.Code);
      Assert.IsFalse(secondCreate.Changed);
      Assert.IsTrue(secondCreate.PostconditionSatisfied);

      using var readArguments = JsonDocument.Parse("{\"path\":\"item.txt\"}");
      var readProposal = new LocalActionProposal(
        "read_file",
        readArguments.RootElement.Clone(),
        null
      );
      _ = await service.ExecuteAsync(
        await service.ValidateAsync(readProposal, session, CancellationToken.None),
        session,
        CancellationToken.None
      );
      using var replaceArguments = JsonDocument.Parse(
        "{\"path\":\"item.txt\",\"oldText\":\"beta\",\"newText\":\"gamma\",\"replaceAll\":false}"
      );
      var replaceProposal = new LocalActionProposal(
        "replace_text",
        replaceArguments.RootElement.Clone(),
        null
      );
      var firstReplace = await service.ExecuteAsync(
        await service.ValidateAsync(replaceProposal, session, CancellationToken.None),
        session,
        CancellationToken.None
      );
      var secondReplace = await service.ExecuteAsync(
        await service.ValidateAsync(replaceProposal, session, CancellationToken.None),
        session,
        CancellationToken.None
      );
      Assert.AreEqual("replace_text_completed", firstReplace.Code);
      Assert.AreEqual(HostActionOutcomes.NoOp, secondReplace.Outcome);
      Assert.AreEqual("already_applied", secondReplace.Code);
      Assert.AreEqual("alpha gamma", await File.ReadAllTextAsync(Path.Combine(root, "item.txt")));
    }
    finally
    {
      Directory.Delete(root, true);
    }
  }

  [TestMethod]
  public void PlanPreservesSpecialistTitlesLiterally()
  {
    using var document = JsonDocument.Parse(
      """
      {
        "objective": "Create several artifacts",
        "steps": [
          { "title": "Create worker source at src/worker.py" },
          { "title": "Create binary data at assets/content.dat" },
          { "title": "Create workbook at reports/results.xlsx" }
        ]
      }
      """
    );
    var service = new ExecutionPlanService();

    var plan = service.ValidateCreate(document.RootElement, 8);

    CollectionAssert.AreEqual(
      new[]
      {
        "Create worker source at src/worker.py",
        "Create binary data at assets/content.dat",
        "Create workbook at reports/results.xlsx"
      },
      plan.Steps.Select(step => step.Title).ToArray()
    );
  }

  [TestMethod]
  public void SpecialistDependenciesMapToHostIdsWithoutSemanticStepRewrite()
  {
    using var document = JsonDocument.Parse(
      """
      {
        "objective": "Use the existing dependency, then create output",
        "steps": [
          { "title": "Inspect existing dependency" },
          { "title": "Create requested output", "dependsOn": [1] }
        ]
      }
      """
    );
    var service = new ExecutionPlanService();

    var plan = service.ValidateCreate(document.RootElement, 8);

    Assert.AreEqual("Inspect existing dependency", plan.Steps[0].Title);
    Assert.AreEqual("Create requested output", plan.Steps[1].Title);
    CollectionAssert.AreEqual(
      new[]
      {
        "step-1"
      },
      plan.Steps[1].Dependencies!.ToArray()
    );
  }

  [TestMethod]
  public void GenericSpecialistTitleDoesNotControlActionBinding()
  {
    var session = CreateSession("Create requested file");
    session.CreatePlan(
      new ExecutionPlanView(
        "Create requested file",
        [new ExecutionPlanStep("generic", "Implement requested file changes", "pending")],
        null,
        0,
        0
      )
    );
    var creation = Action("create-1", "create_file");

    Assert.IsTrue(
      session.RecordPlanActionStarted(
        creation.ActionId,
        creation.Tool,
        stepId: "generic"
      )
    );
    session.RecordFileChange(
      new ExecutionFileChange(
        "created.txt",
        "created",
        false,
        "empty",
        "created",
        null,
        "created",
        7,
        DateTimeOffset.UtcNow,
        true,
        true,
        null,
        0
      )
    );
    session.RecordAction(creation, "completed", "created");

    Assert.IsTrue(session.RecordPlanActionResult(creation.ActionId, creation.Tool, "completed"));
  }

  [TestMethod]
  public void ChangedHtmlCannotCompleteWithMissingLocalAssetReference()
  {
    var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    try
    {
      File.WriteAllText(Path.Combine(workspace, "styles.css"), "body {}");
      var html = "<link href=\"styles.css\"><script src=\"game.js\"></script>";
      File.WriteAllText(Path.Combine(workspace, "index.html"), html);
      var session = CreateSession("Create browser game", workspace);
      session.RecordFileChange(
        new ExecutionFileChange(
          "index.html",
          "created",
          false,
          "empty",
          "created",
          null,
          html,
          html.Length,
          DateTimeOffset.UtcNow,
          true,
          true,
          null,
          0
        )
      );

      CollectionAssert.AreEqual(
        new[] { "game.js" },
        session.UnresolvedChangedFileReferences.ToArray()
      );

      File.WriteAllText(Path.Combine(workspace, "game.js"), "(() => {})();");
      Assert.IsEmpty(session.UnresolvedChangedFileReferences);
    }
    finally
    {
      Directory.Delete(workspace, true);
    }
  }

  [TestMethod]
  public void StaticCompletionReviewRejectsWrongWordCountAndBrokenGameBindings()
  {
    var session = CreateSession(
      "Create a game with a collection of 3 fixed substantive words"
    );
    session.RecordFileChange(
      new ExecutionFileChange(
        "index.html",
        "created",
        false,
        "empty",
        "html",
        null,
        "<button id=\"start-btn\">Start</button><span id=\"words-found\"></span><script src=\"game.js\"></script>",
        100,
        DateTimeOffset.UtcNow,
        true,
        true,
        null,
        0
      )
    );
    session.RecordFileChange(
      new ExecutionFileChange(
        "game.js",
        "created",
        false,
        "empty",
        "js",
        null,
        "const WORDS = ['apple', 'apple']; document.getElementById('found-count');",
        72,
        DateTimeOffset.UtcNow,
        true,
        true,
        null,
        0
      )
    );

    var issues = string.Join(" ", session.StaticCompletionIssues);
    StringAssert.Contains(issues, "exactly 3");
    StringAssert.Contains(issues, "found-count");
    StringAssert.Contains(issues, "start-btn");
  }

  [TestMethod]
  public void SpecialistBindingAndDependenciesSelectTheStepWithoutTitleInference()
  {
    var session = CreateSession("Delete obsolete.txt");
    session.CreatePlan(
      new ExecutionPlanView(
        "Delete obsolete.txt",
        [
          new ExecutionPlanStep("inspect", "Inspect selected file", "pending"),
          new ExecutionPlanStep(
            "delete",
            "Delete inspected file",
            "pending",
            ["inspect"]
          )
        ],
        null,
        0,
        0
      )
    );

    var firstInspection = Action("inspect-1", "read_file");
    var dependencyCorrection = session.ResolvePlanActionBinding("delete");
    Assert.AreEqual(HostPlanBindingStates.Corrected, dependencyCorrection.State);
    Assert.AreEqual("inspect", dependencyCorrection.EffectiveStepId);
    Assert.IsTrue(
      session.RecordPlanActionStarted(
        firstInspection.ActionId,
        firstInspection.Tool,
        stepId: "inspect"
      )
    );
    session.RecordAction(firstInspection, "completed", "observed");
    Assert.IsTrue(session.RecordPlanActionResult(firstInspection.ActionId, firstInspection.Tool, "completed"));

    var unrelatedInspection = Action("inspect-2", "list_files");
    Assert.IsTrue(
      session.RecordPlanActionStarted(
        unrelatedInspection.ActionId,
        unrelatedInspection.Tool,
        stepId: "delete"
      )
    );
    session.RecordAction(unrelatedInspection, "completed", "listed");
    Assert.IsTrue(session.RecordPlanActionResult(unrelatedInspection.ActionId, unrelatedInspection.Tool, "completed"));

    var plan = session.CreateReview().Summary.Plan;
    Assert.IsNotNull(plan);
    Assert.AreEqual("completed", plan.Steps.Single(step => step.Id == "inspect").Status);
    Assert.AreEqual("completed", plan.Steps.Single(step => step.Id == "delete").Status);
  }

  [TestMethod]
  public void PlanDependenciesBlockLaterStepUntilEarlierEffectIsProven()
  {
    var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var session = CreateSession(
      "Create browser artifact",
      workspace
    );
    session.CreatePlan(
      new ExecutionPlanView(
        "Create browser artifact",
        [
          new ExecutionPlanStep(
            "create",
            "Create HTML entrypoint [target: index.html]",
            "pending"
          ),
          new ExecutionPlanStep(
            "review",
            "Review HTML entrypoint [target: index.html]",
            "pending",
            ["create"]
          )
        ],
        null,
        0,
        0
      )
    );

    var prematureReview = Action("read-early", "read_file") with
    {
      TargetPath = Path.Combine(workspace, "index.html")
    };
    var dependencyCorrection = session.ResolvePlanActionBinding("review");
    Assert.AreEqual(HostPlanBindingStates.Corrected, dependencyCorrection.State);
    Assert.AreEqual("create", dependencyCorrection.EffectiveStepId);

    var creation = Action("create", "create_file") with
    {
      TargetPath = Path.Combine(workspace, "index.html")
    };
    Assert.IsTrue(
      session.RecordPlanActionStarted(
        creation.ActionId,
        creation.Tool,
        creation.TargetPath,
        "create"
      )
    );
    session.RecordFileChange(
      new ExecutionFileChange(
        "index.html",
        "created",
        false,
        "empty",
        "created",
        null,
        "created",
        7,
        DateTimeOffset.UtcNow,
        true,
        true,
        null,
        0
      )
    );
    session.RecordAction(creation, "completed", "created");
    Assert.IsTrue(
      session.RecordPlanActionResult(
        creation.ActionId,
        creation.Tool,
        "completed"
      )
    );

    var correctReview = Action("read-correct", "read_file") with
    {
      TargetPath = Path.Combine(workspace, "index.html")
    };
    Assert.IsTrue(
      session.RecordPlanActionStarted(
        correctReview.ActionId,
        correctReview.Tool,
        correctReview.TargetPath,
        "review"
      )
    );
  }

  [TestMethod]
  public void HostDoesNotParseSpecialistPlanTitleAsAValidationRule()
  {
    var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var session = CreateSession(
      "Create a collection of 3 fixed items",
      workspace
    );
    session.CreatePlan(
      new ExecutionPlanView(
        "Create a collection of 3 fixed items",
        [
          new ExecutionPlanStep(
            "review",
            "Review constraint-heavy data file; verify exact 3 [target: data.js]",
            "pending"
          )
        ],
        null,
        0,
        0
      )
    );
    var review = Action("review", "read_file") with
    {
      TargetPath = Path.Combine(workspace, "data.js")
    };
    Assert.IsTrue(
      session.RecordPlanActionStarted(
        review.ActionId,
        review.Tool,
        review.TargetPath,
        "review"
      )
    );
    session.RecordAction(
      review,
      "completed",
      "const items = [{ id: 1 }, { id: 2 }, { id: 3 }, { id: 4 }];"
    );
    Assert.IsTrue(
      session.RecordPlanActionResult(
        review.ActionId,
        review.Tool,
        "completed"
      )
    );
    Assert.AreEqual(
      "completed",
      session.CreateReview().Summary.Plan?.Steps.Single().Status
    );
  }

  [TestMethod]
  public void DeleteStepRequiresVerifiedDeletedFileEffect()
  {
    var session = CreateSession("Delete obsolete.txt");
    session.CreatePlan(
      new ExecutionPlanView(
        "Delete obsolete.txt",
        [new ExecutionPlanStep("delete", "Delete obsolete.txt", "pending")],
        null,
        0,
        0
      )
    );
    var deletion = Action("delete-1", "delete_paths");

    Assert.IsTrue(
      session.RecordPlanActionStarted(
        deletion.ActionId,
        deletion.Tool,
        stepId: "delete"
      )
    );
    session.RecordAction(deletion, "completed", "claimed deletion");
    Assert.IsFalse(session.RecordPlanActionResult(deletion.ActionId, deletion.Tool, "completed"));

    session.RecordFileChange(
      new ExecutionFileChange(
        "obsolete.txt",
        "deleted",
        true,
        "before",
        "after",
        "obsolete",
        string.Empty,
        0,
        DateTimeOffset.UtcNow,
        true,
        true,
        null,
        8
      )
    );

    Assert.IsTrue(session.RecordPlanActionResult(deletion.ActionId, deletion.Tool, "completed"));
    Assert.AreEqual("completed", session.CreateReview().Summary.Plan?.Steps.Single().Status);
  }

  [TestMethod]
  public void MutationObjectiveCannotCompleteWithoutVerifiedMutation()
  {
    var session = CreateSession("Edit app.js");
    session.CreatePlan(
      new ExecutionPlanView(
        "Edit app.js",
        [new ExecutionPlanStep("edit", "Edit app.js", "pending")],
        null,
        0,
        0
      )
    );

    session.Complete("completed");

    var review = session.CreateReview();
    Assert.AreEqual("blocked", review.Summary.State);
    Assert.AreEqual("blocked-mutation-not-performed", review.Summary.CompletionStatus);
  }

  [TestMethod]
  public void VerifiedDirectoryMutationIsNotReportedAsInspectionOnly()
  {
    var session = CreateSession("Create directory generated");
    session.CreatePlan(
      new ExecutionPlanView(
        "Create directory generated",
        [new ExecutionPlanStep("directory", "Create directory generated", "pending")],
        null,
        0,
        0
      )
    );
    var action = Action("directory-1", "create_directory");

    Assert.IsTrue(
      session.RecordPlanActionStarted(
        action.ActionId,
        action.Tool,
        stepId: "directory"
      )
    );
    session.RecordCreatedDirectory("generated");
    session.RecordAction(action, "completed", "created");
    Assert.IsTrue(session.RecordPlanActionResult(action.ActionId, action.Tool, "completed"));
    session.Complete("completed");

    var review = session.CreateReview();
    Assert.AreEqual("completed", review.Summary.State);
    Assert.AreEqual("verified-mutation-no-file-artifacts", review.Summary.CompletionStatus);
  }

  private static ExecutionSession CreateSession(
    string objective,
    string? workspace = null
  ) => new(
    Guid.NewGuid().ToString("N"),
    "browser",
    "request",
    objective,
    "ask",
    workspace ?? Path.GetTempPath(),
    "specialist",
    "functiongemma:270m",
    "resident-action",
    new ExecutionSettings()
  );

  private static ValidatedLocalAction Action(string id, string tool)
  {
    using var document = JsonDocument.Parse("{}");
    return new ValidatedLocalAction(
      id,
      tool,
      document.RootElement.Clone(),
      null,
      null,
      tool,
      null,
      true,
      false
    );
  }

  private sealed class TestWorkspaceService(string root) : ITrustedWorkspaceService
  {
    private readonly string _root = Path.GetFullPath(root);

    public Task<TrustedWorkspaceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
      Task.FromException<TrustedWorkspaceStatus>(new NotSupportedException());

    public Task<TrustedWorkspaceStatus> ConfigureAsync(string path, CancellationToken cancellationToken) =>
      Task.FromException<TrustedWorkspaceStatus>(new NotSupportedException());

    public Task<TrustedWorkspaceStatus> ClearAsync(CancellationToken cancellationToken) =>
      Task.FromException<TrustedWorkspaceStatus>(new NotSupportedException());

    public Task<string> ResolvePathAsync(string? path, CancellationToken cancellationToken)
    {
      var resolved = Resolve(path);
      if (!File.Exists(resolved) && !Directory.Exists(resolved))
      {
        throw new LocalActionException("workspace-path", "The requested path does not exist.");
      }
      return Task.FromResult(resolved);
    }

    public Task<TrustedWorkspacePathResolution> ResolveCreationPathAsync(
      string? path,
      CancellationToken cancellationToken
    )
    {
      var resolved = Resolve(path);
      return Task.FromResult(
        new TrustedWorkspacePathResolution(
          resolved,
          Path.GetRelativePath(_root, resolved),
          path,
          false
        )
      );
    }

    private string Resolve(string? path)
    {
      var resolved = Path.GetFullPath(Path.Combine(_root, path ?? "."));
      var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
      if (
        !string.Equals(resolved, _root, StringComparison.OrdinalIgnoreCase)
        && !resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
      )
      {
        throw new LocalActionException("workspace-boundary", "The path escapes the test workspace.");
      }
      return resolved;
    }
  }
}
