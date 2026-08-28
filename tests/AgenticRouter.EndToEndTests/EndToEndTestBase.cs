using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Execution;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

public abstract class ChatEndToEndTestBase<TBatch> : PageTest
{
  private protected static TestEnvironment _environment = null!;

  [ClassInitialize(InheritanceBehavior.BeforeEachDerivedClass)]
  public static async Task InitializeAsync(
    TestContext _
  )
  {
    _environment = await TestEnvironment.StartAsync();
  }

  [ClassCleanup(InheritanceBehavior.BeforeEachDerivedClass)]
  public static async Task CleanupAsync()
  {
    var environment = _environment;
    if (environment is null)
    {
      return;
    }

    _environment = null!;
    await environment.DisposeAsync();
  }

  [TestInitialize]
  public async Task ResetAsync()
  {
    await _environment.ResetSettingsAsync();
  }

  public override BrowserNewContextOptions ContextOptions()
  {
    return new BrowserNewContextOptions
    {
      BaseURL = _environment.BaseUri.ToString(),
      ColorScheme = ColorScheme.Dark,
      ViewportSize = new ViewportSize
      {
        Width = 1280,
        Height = 720
      }
    };
  }

  private protected static async Task<JsonObject[]> ExecuteCodexStreamAsync(
    string message,
    string browserSessionId
  )
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message,
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "codex",
        approvalPolicy = "auto",
        browserSessionId
      }
    );
    response.EnsureSuccessStatusCode();
    return ParseSseEvents(await response.Content.ReadAsStringAsync());
  }

  private protected static async Task<JsonObject[]> ExecuteHarnessStreamAsync(
    string harness,
    string message,
    string browserSessionId,
    string model = "alpha:latest",
    IReadOnlyList<object>? history = null
  )
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message,
        model,
        history = history ?? Array.Empty<object>(),
        interactionMode = "execute",
        harness,
        approvalPolicy = "auto",
        browserSessionId
      }
    );
    response.EnsureSuccessStatusCode();
    return ParseSseEvents(await response.Content.ReadAsStringAsync());
  }

  private protected static bool IsTerminalStreamEvent(JsonObject item)
  {
    return item["type"]!.GetValue<string>() is
      "error" or "response.completed" or "request.cancelled";
  }

  private protected static JsonObject[] ParseSseEvents(string stream)
  {
    return stream.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
      .Select(line => JsonNode.Parse(line[6..])!.AsObject())
      .ToArray();
  }

  private protected static bool TryParseJsonObject(string line, out JsonObject? value)
  {
    try
    {
      value = JsonNode.Parse(line)?.AsObject();
      return value is not null;
    }
    catch (JsonException)
    {
      value = null;
      return false;
    }
  }

  private protected static async Task<string> ActiveWorkspaceIdAsync()
  {
    using var response = await _environment.HttpClient.GetAsync(
      "api/workspaces"
    );
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await response.Content.ReadAsStringAsync()
    );
    return document.RootElement.GetProperty(
      "activeWorkspaceId"
    ).GetString()!;
  }

  private protected static async Task RunGitAsync(
    params string[] arguments
  )
  {
    _ = await RunGitTextAsync(
      _environment.WorkspaceDirectory,
      arguments
    );
  }

  private protected static async Task<string> RunGitTextAsync(
    string workingDirectory,
    params string[] arguments
  )
  {
    var result = await RunGitAllowFailureAsync(
      workingDirectory,
      arguments
    );
    Assert.AreEqual(
      0,
      result.ExitCode,
      result.Error
    );
    return result.Output;
  }

  private protected static async Task<(
    int ExitCode,
    string Output,
    string Error
  )> RunGitAllowFailureAsync(
    string workingDirectory,
    params string[] arguments
  )
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };

    foreach (var argument in arguments)
    {
      process.StartInfo.ArgumentList.Add(
        argument
      );
    }

    Assert.IsTrue(
      process.Start()
    );
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (
      process.ExitCode,
      (
        await outputTask
      ).Trim(),
      (
        await errorTask
      ).Trim()
    );
  }

  private protected static async Task<(
    int ExitCode,
    string Output,
    string Error
  )> RunPowerShellAsync(
    string scriptPath,
    params string[] arguments
  )
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "powershell",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };
    process.StartInfo.ArgumentList.Add("-NoProfile");
    process.StartInfo.ArgumentList.Add("-File");
    process.StartInfo.ArgumentList.Add(scriptPath);
    foreach (var argument in arguments)
    {
      process.StartInfo.ArgumentList.Add(argument);
    }
    Assert.IsTrue(process.Start());
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
  }

  private protected static async Task<string> InitializeDeliveryRepositoryAsync()
  {
    await RunGitAsync(
      "init",
      "-b",
      "main"
    );
    await RunGitAsync(
      "config",
      "user.name",
      "Agentic Router E2E"
    );
    await RunGitAsync(
      "config",
      "user.email",
      "agentic-router-e2e@example.invalid"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "baseline.txt"
      ),
      "baseline"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "preexisting.txt"
      ),
      "user baseline"
    );
    await RunGitAsync(
      "add",
      "--",
      "baseline.txt",
      "preexisting.txt"
    );
    await RunGitAsync(
      "commit",
      "-m",
      "test baseline"
    );
    var remote = _environment.CreateWorkspaceDirectory(
      $"delivery-remote-{Guid.NewGuid():N}.git"
    );
    _ = await RunGitTextAsync(
      _environment.WorkspaceDirectory,
      "init",
      "--bare",
      remote
    );
    await RunGitAsync(
      "remote",
      "add",
      "origin",
      remote
    );
    await RunGitAsync(
      "push",
      "-u",
      "origin",
      "main"
    );
    _ = await RunGitTextAsync(
      remote,
      "symbolic-ref",
      "HEAD",
      "refs/heads/main"
    );
    return remote;
  }

  private protected async Task StageDeliveryAsync(
    ILocator panel
  )
  {
    await ApproveDeliveryOperationAsync(
      panel,
      "Stage selected"
    );
  }

  private protected async Task ApproveDeliveryOperationAsync(
    ILocator panel,
    string operation,
    bool expectSuccess = true
  )
  {
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = operation,
        Exact = true
      }
    ).ClickAsync();
    var approvalRequired = string.Equals(
      await Page.Locator("#approval-policy").InputValueAsync(),
      "ask",
      StringComparison.Ordinal
    );
    var approval = panel.Locator(".delivery-approval");
    if (approvalRequired)
    {
      await Expect(approval).ToBeVisibleAsync();
      await panel.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Approve exact action",
          Exact = true
        }
      ).ClickAsync();
    }
    else
    {
      await Expect(approval).ToBeHiddenAsync();
    }

    if (expectSuccess)
    {
      var operationCode = operation switch
      {
        "Stage selected" => "stage",
        "Unstage selected" => "unstage",
        "Create commit" => "commit",
        "Create annotated tag" => "tag",
        "Push current branch" => "push-branch",
        "Push exact tag" => "push-tag",
        _ => operation
      };
      await Expect(
        Page.Locator(
          "#undo-status"
        )
      ).ToContainTextAsync(
        $"Git {operationCode} completed"
      );
    }
  }

  private protected async Task SetExecuteModeAsync(
    string approvalPolicy
  )
  {
    await Page.Locator(
      "[data-mode=\"execute\"]"
    ).ClickAsync();
    await Page.Locator(
      "#approval-policy"
    ).SelectOptionAsync(
      approvalPolicy
    );
  }

  private protected async Task AssertQwenToolingScenarioAsync(
    string prompt,
    string relativePath,
    string expectedContent,
    bool expectProcessOffered,
    bool expectProcessExecuted
  )
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");

    if (expectProcessExecuted)
    {
      await StartMessageAsync(prompt);
      await Expect(Page.Locator(".action-approval")).ToBeVisibleAsync();
      await Page.Locator(".action-approval").Last.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Approve",
          Exact = true
        }
      ).ClickAsync();
      await Expect(
        Page.Locator("[data-event-type=\"action.process-output\"]")
      ).ToContainTextAsync("hello");
      await Expect(
        Page.Locator(".message.assistant .activity").Last
      ).ToHaveAttributeAsync(
        "data-terminal",
        "true",
        new()
        {
          Timeout = 20_000
        }
      );
    }
    else
    {
      await SendMessageAsync(prompt);
    }

    Assert.AreEqual(
      expectedContent,
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          relativePath
        )
      )
    );

    var toolingRequests = _environment.FakeOllama.Requests.Where(
      request => request.Model == "qwen3-coder:30b"
        && request.HasTools
        && request.Messages.Any(
          message => message.Content.Contains(
            "SPECIALIST_TOOL_LOOP_V2",
            StringComparison.Ordinal
          )
        )
    ).ToArray();
    Assert.IsNotEmpty(toolingRequests);
    var firstRequest = toolingRequests[0];
    CollectionAssert.AreEqual(
      new[]
      {
        LocalActionPlanner.RequestToolsetTool
      },
      firstRequest.AvailableTools.ToArray()
    );
    Assert.AreEqual(
      expectProcessOffered,
      firstRequest.Messages.Any(
        message => message.Content.Contains(
          "\nrun_process(",
          StringComparison.Ordinal
        )
      )
    );
    Assert.IsTrue(
      firstRequest.Messages.Any(
        message => message.Content.Contains(
          "SPECIALIST_TOOLING_PROFILE qwen-code-ollama-v1",
          StringComparison.Ordinal
        )
      )
    );

    var proposedTools = toolingRequests.SelectMany(
      request => request.Messages.SelectMany(
        message => message.ToolCalls.Select(
          call => call.Name
        )
      )
    ).ToArray();
    CollectionAssert.Contains(
      proposedTools,
      "create_file"
    );
    Assert.IsTrue(
      proposedTools.Contains(
        "read_file",
        StringComparer.Ordinal
      ),
      "The specialist must independently read the created file even when it also executes it."
    );
    Assert.AreEqual(
      expectProcessExecuted,
      proposedTools.Contains(
        "run_process",
        StringComparer.Ordinal
      )
    );
    await Expect(
      Page.Locator("[data-event-type=\"agent.tooling-profile-resolved\"]")
    ).ToContainTextAsync("qwen-code-ollama@1");
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && request.Messages.Any(
            message => message.Content.Contains(
              "SPECIALIST_TOOL_LOOP_V2",
              StringComparison.Ordinal
            )
          )
      )
    );
    Assert.IsEmpty(_environment.FakeCloud.Requests);
  }

  private protected async Task OpenSettingsAsync()
  {
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Settings",
        Exact = true
      }
    ).ClickAsync();
  }

  private protected async Task ConfirmAppModalAsync()
  {
    await Expect(
      Page.Locator(
        "#app-modal"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      "#app-modal-confirm"
    ).ClickAsync();
  }

  private protected static async Task ConnectFakeCloudAsync(
    string providerId,
    string apiKey
  )
  {
    using (
      var saved = await _environment.HttpClient.PutAsJsonAsync(
        $"api/cloud-providers/{providerId}/key",
        new
        {
          apiKey
        }
      )
    )
    {
      Assert.AreEqual(
        HttpStatusCode.OK,
        saved.StatusCode,
        await saved.Content.ReadAsStringAsync()
      );
    }

    using var refreshed = await _environment.HttpClient.PostAsync(
      $"api/cloud-providers/{providerId}/models/refresh",
      null
    );
    Assert.AreEqual(
      HttpStatusCode.OK,
      refreshed.StatusCode,
      await refreshed.Content.ReadAsStringAsync()
    );
  }

  private protected static IReadOnlyDictionary<string, string> ExpectedToolAliases()
  {
    return new Dictionary<string, string>(
      StringComparer.OrdinalIgnoreCase
    )
    {
      ["create-execution-plan"] = "create_execution_plan",
      ["createexecutionplan"] = "create_execution_plan",
      ["make_execution_plan"] = "create_execution_plan",
      ["build_execution_plan"] = "create_execution_plan",
      ["revise-execution-plan"] = "revise_execution_plan",
      ["reviseexecutionplan"] = "revise_execution_plan",
      ["update_execution_plan"] = "revise_execution_plan",
      ["list-files"] = "list_files",
      ["listfiles"] = "list_files",
      ["list_directory"] = "list_files",
      ["list-directory"] = "list_files",
      ["list_dir"] = "list_files",
      ["read-file"] = "read_file",
      ["readfile"] = "read_file",
      ["read_doc"] = "read_file",
      ["read-doc"] = "read_file",
      ["readdoc"] = "read_file",
      ["read_code"] = "read_file",
      ["read-code"] = "read_file",
      ["readcode"] = "read_file",
      ["get-file-info"] = "get_file_info",
      ["getfileinfo"] = "get_file_info",
      ["file_info"] = "get_file_info",
      ["file-info"] = "get_file_info",
      ["stat_file"] = "get_file_info",
      ["file_stat"] = "get_file_info",
      ["search-text"] = "search_text",
      ["searchtext"] = "search_text",
      ["grep_text"] = "search_text",
      ["grep-text"] = "search_text",
      ["find_text"] = "search_text",
      ["find-text"] = "search_text",
      ["search_in_files"] = "search_text",
      ["search-in-files"] = "search_text",
      ["create-file"] = "create_file",
      ["createfile"] = "create_file",
      ["create-files"] = "create_files",
      ["createfiles"] = "create_files",
      ["write-file"] = "write_file",
      ["writefile"] = "write_file",
      ["replace-text"] = "replace_text",
      ["replacetext"] = "replace_text",
      ["apply-patch"] = "apply_patch",
      ["applypatch"] = "apply_patch",
      ["delete_files"] = "delete_paths",
      ["delete-files"] = "delete_paths",
      ["deletefiles"] = "delete_paths",
      ["delete-paths"] = "delete_paths",
      ["deletepaths"] = "delete_paths",
      ["remove_files"] = "delete_paths",
      ["remove-files"] = "delete_paths",
      ["remove_paths"] = "delete_paths",
      ["remove-paths"] = "delete_paths",
      ["create-directory"] = "create_directory",
      ["createdirectory"] = "create_directory",
      ["run-process"] = "run_process",
      ["runprocess"] = "run_process",
      ["run-validation-profile"] = "run_validation_profile",
      ["runvalidationprofile"] = "run_validation_profile",
      ["git-status"] = "git_status",
      ["gitstatus"] = "git_status",
      ["repo_status"] = "git_status",
      ["repository_status"] = "git_status",
      ["git-diff"] = "git_diff",
      ["gitdiff"] = "git_diff",
      ["repo_diff"] = "git_diff",
      ["repository_diff"] = "git_diff",
      ["git-log"] = "git_log",
      ["gitlog"] = "git_log",
      ["commit_log"] = "git_log",
      ["git_history"] = "git_log",
      ["git-show-commit"] = "git_show_commit",
      ["gitshowcommit"] = "git_show_commit",
      ["show_commit"] = "git_show_commit",
      ["inspect_commit"] = "git_show_commit",
      ["git-stage-files"] = "git_stage_files",
      ["gitstagefiles"] = "git_stage_files",
      ["git-unstage-files"] = "git_unstage_files",
      ["gitunstagefiles"] = "git_unstage_files",
      ["git-create-commit"] = "git_create_commit",
      ["gitcreatecommit"] = "git_create_commit",
      ["git-create-annotated-tag"] = "git_create_annotated_tag",
      ["gitcreateannotatedtag"] = "git_create_annotated_tag",
      ["git-push-current-branch"] = "git_push_current_branch",
      ["gitpushcurrentbranch"] = "git_push_current_branch",
      ["git-push-tag"] = "git_push_tag",
      ["gitpushtag"] = "git_push_tag"
    };
  }

  private protected static string[] ExpectedCanonicalTools()
  {
    return
    [
      "request_toolset",
      "create_execution_plan",
      "revise_execution_plan",
      "list_files",
      "read_file",
      "get_file_info",
      "search_text",
      "web_search",
      "create_file",
      "create_files",
      "write_file",
      "replace_text",
      "apply_patch",
      "delete_paths",
      "create_directory",
      "run_process",
      "run_validation_profile",
      "git_status",
      "git_diff",
      "git_log",
      "git_show_commit",
      "git_stage_files",
      "git_unstage_files",
      "git_create_commit",
      "git_create_annotated_tag",
      "git_push_current_branch",
      "git_push_tag"
    ];
  }

  private protected static string[] DeliberatelyRejectedToolAliases()
  {
    return
    [
      "get_file",
      "open_file",
      "search_files",
      "find_file",
      "edit_file",
      "modify_file",
      "save_file",
      "update_file",
      "patch_file",
      "new_file",
      "mkdir",
      "execute_command",
      "run_command",
      "shell",
      "run_tests",
      "validate",
      "stage_files",
      "commit",
      "push",
      "tag",
      "inspect_repo",
      "analyze_project"
    ];
  }

  private protected static async Task BenchmarkStructuredConformanceAsync(
    string model,
    bool expectedPassed
  )
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/models/conformance",
      new
      {
        model,
        profile = "structured-action",
        restoreResidentModel = false
      }
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<JsonElement>(
      TestJson.Options
    );
    Assert.AreEqual(
      expectedPassed,
      result.GetProperty(
        "passed"
      ).GetBoolean()
    );
  }

  private protected static async Task<JsonObject> GetSettingsJsonAsync()
  {
    using var response = await _environment.HttpClient.GetAsync(
      "api/settings"
    );
    response.EnsureSuccessStatusCode();
    return JsonNode.Parse(
      await response.Content.ReadAsStringAsync()
    )!.AsObject();
  }

  private protected static Task<HttpResponseMessage> PutSettingsJsonAsync(
    JsonObject settings
  )
  {
    return _environment.HttpClient.PutAsync(
      "api/settings",
      new StringContent(
        settings.ToJsonString(
          TestJson.Options
        ),
        Encoding.UTF8,
        "application/json"
      )
    );
  }

  private protected static async Task<string> PostChatStreamAsync(
    string message,
    string model,
    string browserSessionId = "browser-v095",
    bool webSearchEnabled = false,
    IReadOnlyList<object>? images = null,
    string interactionMode = "chat",
    string harness = "native",
    string approvalPolicy = "ask"
  )
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message,
        model,
        history = Array.Empty<object>(),
        interactionMode,
        harness,
        approvalPolicy,
        browserSessionId,
        conversationSessionId = (string?)null,
        webSearchEnabled,
        images
      }
    );
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
  }

  private protected async Task DispatchImageTransferAsync(
    string type,
    string fileName,
    string base64
  )
  {
    await Page.EvaluateAsync(
      @"({ type, fileName, base64 }) => {
        const bytes = Uint8Array.from(atob(base64), value => value.charCodeAt(0));
        const file = new File([bytes], fileName, { type: 'image/png' });
        const transfer = new DataTransfer();
        transfer.items.add(file);
        const event = new Event(type, { bubbles: true, cancelable: true });
        const property = type === 'paste' ? 'clipboardData' : 'dataTransfer';
        Object.defineProperty(event, property, { value: transfer });
        const target = type === 'paste'
          ? document.querySelector('#message-input')
          : document.querySelector('#composer');
        target.dispatchEvent(event);
      }",
      new
      {
        type,
        fileName,
        base64
      }
    );
  }

  private protected async Task<double> RemainingScrollAsync()
  {
    return await Page.Locator(
      "#messages"
    ).EvaluateAsync<double>(
      "messages => messages.scrollHeight - messages.scrollTop - messages.clientHeight"
    );
  }

  private protected static bool IsPathInside(
    string root,
    string candidate
  )
  {
    var relative = Path.GetRelativePath(
      Path.GetFullPath(root),
      Path.GetFullPath(candidate)
    );
    return !Path.IsPathRooted(relative)
      && !string.Equals(relative, "..", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  private protected static async Task WaitUntilAsync(
    Func<bool> condition,
    TimeSpan timeout
  )
  {
    using var cancellation = new CancellationTokenSource(
      timeout
    );

    while (!condition())
    {
      await Task.Delay(
        50,
        cancellation.Token
      );
    }
  }

  private protected static bool ProcessIsAlive(int id)
  {
    try
    {
      using var process = Process.GetProcessById(id);
      return !process.HasExited;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }

  private protected static string ReadAllTextShared(string path)
  {
    using var stream = new FileStream(
      path,
      FileMode.Open,
      FileAccess.Read,
      FileShare.ReadWrite | FileShare.Delete
    );
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  private protected async Task StartMessageAsync(
    string message
  )
  {
    await Expect(
      Page.Locator("#send-button-label")
    ).ToHaveTextAsync("Send");
    var input = Page.Locator(
      "#message-input"
    );
    await Expect(input).ToBeEnabledAsync();
    await input.FillAsync(message);
    var send = Page.Locator("#send-button");
    await Expect(send).ToBeEnabledAsync();
    await send.ClickAsync();
  }

  private protected async Task SendMessageAsync(
    string message
  )
  {
    var assistantMessages = Page.Locator(
      ".message.assistant"
    );
    var previousCount = await assistantMessages.CountAsync();
    await StartMessageAsync(
      message
    );
    await Expect(
      assistantMessages
    ).ToHaveCountAsync(
      previousCount + 1
    );
    await Expect(
      Page.Locator(
        ".message.assistant .activity"
      ).Last
    ).ToHaveAttributeAsync(
      "data-terminal",
      "true",
      new()
      {
        Timeout = 20_000
      }
    );
    await Expect(
      Page.Locator(
        ".message.assistant .activity"
      ).Last
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
  }

}
