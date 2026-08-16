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
      "plan=pending; latest-action=read_file",
      100_000,
      false,
      1,
      false
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
      "latest-action=read_file",
      100_000,
      false,
      1,
      false
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
  public void UnboundInspectionsStayOnNextPlanTargetAndDoNotRepeat()
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
      var firstRead = Action("read-style", "read_file") with
      {
        TargetPath = stylePath,
        Summary = "read_file: style.css"
      };

      Assert.IsNull(session.ValidateUnboundPlanSupport(firstRead));
      session.RecordAction(firstRead, "completed", "observed");

      var repeatedRead = firstRead with
      {
        ActionId = "read-style-again"
      };
      StringAssert.Contains(
        session.ValidateUnboundPlanSupport(repeatedRead),
        "already inspected"
      );

      var unrelatedRead = Action("read-other", "read_file") with
      {
        TargetPath = Path.Combine(workspace, "index.html"),
        Summary = "read_file: index.html"
      };
      StringAssert.Contains(
        session.ValidateUnboundPlanSupport(unrelatedRead),
        "next pending plan step"
      );
    }
    finally
    {
      Directory.Delete(workspace, true);
    }
  }

  [TestMethod]
  public void PlanPreservesModelChosenArtifactNamesAndRebasesTraversal()
  {
    using var document = JsonDocument.Parse(
      """
      {
        "objective": "Create several artifacts",
        "steps": [
          { "title": "Create worker source", "target": "../../src/worker.py" },
          { "title": "Create binary data", "target": "assets/content.dat" },
          { "title": "Create workbook", "target": "reports/results.xlsx" }
        ]
      }
      """
    );
    var service = new ExecutionPlanService();

    var plan = service.ValidateCreate(document.RootElement, 8);

    Assert.AreEqual(
      "src/worker.py",
      ToolEffectRegistry.TryGetHostTarget(plan.Steps[0].Title)
    );
    Assert.AreEqual(
      "assets/content.dat",
      ToolEffectRegistry.TryGetHostTarget(plan.Steps[1].Title)
    );
    Assert.AreEqual(
      "reports/results.xlsx",
      ToolEffectRegistry.TryGetHostTarget(plan.Steps[2].Title)
    );
  }

  [TestMethod]
  public void ExistingReferencedDependencyIsInspectedUnlessMutationIsExplicit()
  {
    var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var assets = Path.Combine(workspace, "assets");
    Directory.CreateDirectory(assets);
    File.WriteAllText(
      Path.Combine(assets, "library.wathever"),
      "existing dependency"
    );
    try
    {
      using var document = JsonDocument.Parse(
        """
        {
          "objective": "Use an existing dependency",
          "steps": [
            {
              "title": "Create referenced dependency",
              "target": "assets/library.wathever"
            }
          ]
        }
        """
      );
      var service = new ExecutionPlanService();
      var proposed = service.ValidateCreate(document.RootElement, 8);

      var reusePlan = service.NormalizeExistingReferencedDependencies(
        proposed,
        "Use the library.wathever inside assets to generate output",
        workspace
      );
      var mutationPlan = service.NormalizeExistingReferencedDependencies(
        proposed,
        "Update library.wathever inside assets",
        workspace
      );

      Assert.AreEqual(
        ToolEffects.Inspected,
        ToolEffectRegistry.InferExpectedEffect(reusePlan.Steps[0].Title)
      );
      Assert.AreEqual(
        "assets/library.wathever",
        ToolEffectRegistry.TryGetHostTarget(reusePlan.Steps[0].Title)
      );
      Assert.AreEqual(
        ToolEffects.FileCreated,
        ToolEffectRegistry.InferExpectedEffect(mutationPlan.Steps[0].Title)
      );
    }
    finally
    {
      Directory.Delete(workspace, true);
    }
  }

  [TestMethod]
  public void HostNormalizesAmbiguousAndGenericTitlesWithoutArbitraryFallback()
  {
    Assert.AreEqual(
      ToolEffects.Inspected,
      ToolEffectRegistry.InferExpectedEffect("Inspect files selected for deletion")
    );

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

    Assert.IsTrue(session.RecordPlanActionStarted(creation.ActionId, creation.Tool));
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
  public void IncompatibleToolDoesNotAdvanceAnotherPlanStep()
  {
    var session = CreateSession("Delete obsolete.txt");
    session.CreatePlan(
      new ExecutionPlanView(
        "Delete obsolete.txt",
        [
          new ExecutionPlanStep("inspect", "Inspect selected file", "pending"),
          new ExecutionPlanStep("delete", "Delete inspected file", "pending")
        ],
        null,
        0,
        0
      )
    );

    var firstInspection = Action("inspect-1", "read_file");
    Assert.IsTrue(session.RecordPlanActionStarted(firstInspection.ActionId, firstInspection.Tool));
    session.RecordAction(firstInspection, "completed", "observed");
    Assert.IsTrue(session.RecordPlanActionResult(firstInspection.ActionId, firstInspection.Tool, "completed"));

    var unrelatedInspection = Action("inspect-2", "list_files");
    Assert.IsFalse(session.RecordPlanActionStarted(unrelatedInspection.ActionId, unrelatedInspection.Tool));
    session.RecordAction(unrelatedInspection, "completed", "listed");
    Assert.IsFalse(session.RecordPlanActionResult(unrelatedInspection.ActionId, unrelatedInspection.Tool, "completed"));

    var plan = session.CreateReview().Summary.Plan;
    Assert.IsNotNull(plan);
    Assert.AreEqual("completed", plan.Steps.Single(step => step.Id == "inspect").Status);
    Assert.AreEqual("pending", plan.Steps.Single(step => step.Id == "delete").Status);
  }

  [TestMethod]
  public void StaticReviewRequiresEarlierMutationsAndTheExactHostTarget()
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
            "pending"
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
    Assert.IsFalse(
      session.RecordPlanActionStarted(
        prematureReview.ActionId,
        prematureReview.Tool,
        prematureReview.TargetPath
      )
    );

    var creation = Action("create", "create_file") with
    {
      TargetPath = Path.Combine(workspace, "index.html")
    };
    Assert.IsTrue(
      session.RecordPlanActionStarted(
        creation.ActionId,
        creation.Tool,
        creation.TargetPath
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

    var wrongReview = Action("read-wrong", "read_file") with
    {
      TargetPath = Path.Combine(workspace, "words.js")
    };
    Assert.IsFalse(
      session.RecordPlanActionStarted(
        wrongReview.ActionId,
        wrongReview.Tool,
        wrongReview.TargetPath
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
        correctReview.TargetPath
      )
    );
  }

  [TestMethod]
  public void StaticCollectionReviewRejectsWrongTopLevelItemCount()
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
        review.TargetPath
      )
    );

    var diagnostic = session.ValidatePlanActionEvidence(
      review.ActionId,
      review.Tool,
      "const items = [{ id: 1 }, { id: 2 }, { id: 3 }, { id: 4 }];"
    );
    Assert.IsNotNull(diagnostic);
    StringAssert.Contains(diagnostic, "4 top-level array items");
    Assert.IsTrue(session.RejectPlanActionEvidence(review.ActionId));
    Assert.AreEqual(
      "pending",
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
    var deletion = Action("delete-1", "delete_files");

    Assert.IsTrue(session.RecordPlanActionStarted(deletion.ActionId, deletion.Tool));
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

    Assert.IsTrue(session.RecordPlanActionStarted(action.ActionId, action.Tool));
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
}
