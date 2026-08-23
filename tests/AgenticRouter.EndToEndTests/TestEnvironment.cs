using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed class TestEnvironment : IAsyncDisposable
{
  private Process _apiProcess;
  private readonly FakeOllamaServer _fakeOllama;
  private readonly FakeCloudProviderServer _fakeCloud;
  private readonly string _temporaryRoot;
  private readonly StringBuilder _apiOutput;

  private TestEnvironment(
    string repositoryRoot,
    string temporaryRoot,
    string dataDirectory,
    string workspaceDirectory,
    string settingsPath,
    Uri baseUri,
    TestApplicationSettings baselineSettings,
    FakeOllamaServer fakeOllama,
    FakeCloudProviderServer fakeCloud,
    string fakeCodexExecutablePath,
    string fakeClaudeCodeExecutablePath,
    string fakeOpenCodeExecutablePath,
    string fakeQwenCodeExecutablePath,
    Process apiProcess,
    StringBuilder apiOutput
  )
  {
    RepositoryRoot = repositoryRoot;
    _temporaryRoot = temporaryRoot;
    DataDirectory = dataDirectory;
    WorkspaceDirectory = workspaceDirectory;
    SettingsPath = settingsPath;
    BaseUri = baseUri;
    BaselineSettings = baselineSettings;
    _fakeOllama = fakeOllama;
    _fakeCloud = fakeCloud;
    FakeCodexExecutablePath = fakeCodexExecutablePath;
    FakeClaudeCodeExecutablePath = fakeClaudeCodeExecutablePath;
    FakeOpenCodeExecutablePath = fakeOpenCodeExecutablePath;
    FakeQwenCodeExecutablePath = fakeQwenCodeExecutablePath;
    _apiProcess = apiProcess;
    _apiOutput = apiOutput;
    HttpClient = new HttpClient
    {
      BaseAddress = baseUri,
      Timeout = TimeSpan.FromSeconds(
        10
      )
    };
  }

  public string RepositoryRoot { get; }

  public string DataDirectory { get; }

  public string WorkspaceDirectory { get; }

  public string SettingsPath { get; }

  public Uri BaseUri { get; }

  public TestApplicationSettings BaselineSettings { get; }

  public HttpClient HttpClient { get; }

  public FakeOllamaServer FakeOllama => _fakeOllama;

  public FakeCloudProviderServer FakeCloud => _fakeCloud;

  public string FakeCodexExecutablePath { get; }

  public string FakeClaudeCodeExecutablePath { get; }

  public string FakeOpenCodeExecutablePath { get; }

  public string FakeQwenCodeExecutablePath { get; }

  public string ApiOutput => _apiOutput.ToString();

  public static async Task<TestEnvironment> StartAsync()
  {
    var repositoryRoot = FindRepositoryRoot();
    var temporaryRoot = Path.Combine(
      Path.GetTempPath(),
      "agentic-router-e2e",
      Guid.NewGuid().ToString(
        "N"
      )
    );
    var dataDirectory = Path.Combine(
      temporaryRoot,
      "data"
    );
    var settingsPath = Path.Combine(
      dataDirectory,
      "settings.json"
    );
    Directory.CreateDirectory(
      dataDirectory
    );
    var workspaceDirectory = Path.Combine(
      temporaryRoot,
      "workspace"
    );
    Directory.CreateDirectory(
      workspaceDirectory
    );

    var fakeOllama = FakeOllamaServer.Start();
    var fakeCloud = FakeCloudProviderServer.Start();
    var baselineSettings = TestApplicationSettings.Create(
      fakeOllama.BaseUrl,
      workspaceDirectory
    );
    await File.WriteAllTextAsync(
      settingsPath,
      baselineSettings.ToJson()
    );

    var port = GetAvailablePort();
    var baseUri = new Uri(
      $"http://127.0.0.1:{port}",
      UriKind.Absolute
    );
    var configuration = new DirectoryInfo(
      AppContext.BaseDirectory
    ).Parent?.Name ?? "Debug";
    var executablePath = Environment.GetEnvironmentVariable(
      "AGENTIC_ROUTER_E2E_API_PATH"
    );

    if (string.IsNullOrWhiteSpace(
      executablePath
    ))
    {
      executablePath = Path.Combine(
        repositoryRoot,
        "AgenticRouter.Api",
        "bin",
        configuration,
        "net10.0",
        OperatingSystem.IsWindows()
          ? "AgenticRouter.Api.exe"
          : "AgenticRouter.Api"
      );
    }
    else
    {
      executablePath = Path.GetFullPath(
        executablePath
      );
    }
    var apiOutput = new StringBuilder();
    var processStartInfo = new ProcessStartInfo
    {
      FileName = executablePath,
      Arguments = $"--urls {baseUri}",
      WorkingDirectory = Path.Combine(
        repositoryRoot,
        "AgenticRouter.Api"
      ),
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    processStartInfo.Environment["AgenticRouter__DataDirectory"] = dataDirectory;
    processStartInfo.Environment["AgenticRouter__Providers__GroqBaseUrl"] =
      $"{fakeCloud.BaseUrl}/groq/openai/v1/";
    processStartInfo.Environment[
      "AgenticRouter__Providers__GoogleAiStudioBaseUrl"
    ] = $"{fakeCloud.BaseUrl}/gemini/v1beta/";
    processStartInfo.Environment["AgenticRouter__Providers__CerebrasBaseUrl"] =
      $"{fakeCloud.BaseUrl}/cerebras/v1/";
    processStartInfo.Environment[
      "AgenticRouter__Providers__CerebrasPublicBaseUrl"
    ] = $"{fakeCloud.BaseUrl}/cerebras/public/v1/";
    processStartInfo.Environment[
      "AgenticRouter__Providers__OllamaWebSearchBaseUrl"
    ] = $"{fakeCloud.BaseUrl}/ollama/";
    var fakeCodexExecutablePath = Environment.GetEnvironmentVariable(
      "AGENTIC_ROUTER_E2E_FAKE_CODEX_PATH"
    );
    if (string.IsNullOrWhiteSpace(fakeCodexExecutablePath))
    {
      fakeCodexExecutablePath = Path.Combine(
        repositoryRoot,
        "tests",
        "FakeCodexAppServer",
        "bin",
        configuration,
        "net10.0",
        OperatingSystem.IsWindows()
          ? "FakeCodexAppServer.exe"
          : "FakeCodexAppServer"
      );
    }
    else
    {
      fakeCodexExecutablePath = Path.GetFullPath(fakeCodexExecutablePath);
    }
    processStartInfo.Environment[
      "AgenticRouter__Codex__ExecutablePath"
    ] = fakeCodexExecutablePath;
    var fakeClaudeCodeExecutablePath = Environment.GetEnvironmentVariable(
      "AGENTIC_ROUTER_E2E_FAKE_CLAUDE_CODE_PATH"
    );
    if (string.IsNullOrWhiteSpace(fakeClaudeCodeExecutablePath))
    {
      fakeClaudeCodeExecutablePath = Path.Combine(
        repositoryRoot,
        "tests",
        "FakeClaudeCodeCli",
        "bin",
        configuration,
        "net10.0",
        OperatingSystem.IsWindows()
          ? "FakeClaudeCodeCli.exe"
          : "FakeClaudeCodeCli"
      );
    }
    else
    {
      fakeClaudeCodeExecutablePath = Path.GetFullPath(fakeClaudeCodeExecutablePath);
    }
    processStartInfo.Environment[
      "AgenticRouter__ClaudeCode__ExecutablePath"
    ] = fakeClaudeCodeExecutablePath;
    processStartInfo.Environment["CLAUDE_CODE_USE_BEDROCK"] = "1";
    processStartInfo.Environment["CLAUDE_CODE_USE_VERTEX"] = "1";
    processStartInfo.Environment["CLAUDE_CODE_USE_FOUNDRY"] = "1";
    var fakeOpenCodeExecutablePath = Environment.GetEnvironmentVariable(
      "AGENTIC_ROUTER_E2E_FAKE_OPENCODE_PATH"
    );
    if (string.IsNullOrWhiteSpace(fakeOpenCodeExecutablePath))
    {
      fakeOpenCodeExecutablePath = Path.Combine(
        repositoryRoot,
        "tests",
        "FakeOpenCodeServer",
        "bin",
        configuration,
        "net10.0",
        OperatingSystem.IsWindows()
          ? "FakeOpenCodeServer.exe"
          : "FakeOpenCodeServer"
      );
    }
    else
    {
      fakeOpenCodeExecutablePath = Path.GetFullPath(fakeOpenCodeExecutablePath);
    }
    processStartInfo.Environment[
      "AgenticRouter__OpenCode__ExecutablePath"
    ] = fakeOpenCodeExecutablePath;
    var fakeQwenCodeExecutablePath = Environment.GetEnvironmentVariable(
      "AGENTIC_ROUTER_E2E_FAKE_QWEN_CODE_PATH"
    );
    if (string.IsNullOrWhiteSpace(fakeQwenCodeExecutablePath))
    {
      fakeQwenCodeExecutablePath = Path.Combine(
        repositoryRoot,
        "tests",
        "FakeQwenCodeServer",
        "bin",
        configuration,
        "net10.0",
        OperatingSystem.IsWindows()
          ? "FakeQwenCodeServer.exe"
          : "FakeQwenCodeServer"
      );
    }
    else
    {
      fakeQwenCodeExecutablePath = Path.GetFullPath(fakeQwenCodeExecutablePath);
    }
    processStartInfo.Environment[
      "AgenticRouter__QwenCode__ExecutablePath"
    ] = fakeQwenCodeExecutablePath;
    var apiProcess = new Process
    {
      StartInfo = processStartInfo,
      EnableRaisingEvents = true
    };
    apiProcess.OutputDataReceived += (
      _,
      eventArgs
    ) =>
    {
      if (eventArgs.Data is not null)
      {
        apiOutput.AppendLine(
          eventArgs.Data
        );
      }
    };
    apiProcess.ErrorDataReceived += (
      _,
      eventArgs
    ) =>
    {
      if (eventArgs.Data is not null)
      {
        apiOutput.AppendLine(
          eventArgs.Data
        );
      }
    };

    if (!apiProcess.Start())
    {
      await fakeOllama.DisposeAsync();
      await fakeCloud.DisposeAsync();
      throw new InvalidOperationException(
        "The E2E API process could not be started."
      );
    }

    apiProcess.BeginOutputReadLine();
    apiProcess.BeginErrorReadLine();

    var environment = new TestEnvironment(
      repositoryRoot,
      temporaryRoot,
      dataDirectory,
      workspaceDirectory,
      settingsPath,
      baseUri,
      baselineSettings,
      fakeOllama,
      fakeCloud,
      fakeCodexExecutablePath,
      fakeClaudeCodeExecutablePath,
      fakeOpenCodeExecutablePath,
      fakeQwenCodeExecutablePath,
      apiProcess,
      apiOutput
    );

    try
    {
      await environment.WaitUntilReadyAsync();
      return environment;
    }
    catch
    {
      await environment.DisposeAsync();
      throw;
    }
  }

  public async Task ResetSettingsAsync()
  {
    var modelOrganizationPath = Path.Combine(
      DataDirectory,
      "model-organization.json"
    );

    if (File.Exists(
      modelOrganizationPath
    ))
    {
      File.Delete(
        modelOrganizationPath
      );
    }

    foreach (var entry in new DirectoryInfo(
      WorkspaceDirectory
    ).EnumerateFileSystemInfos())
    {
      if (entry is DirectoryInfo directory)
      {
        NormalizeDeletionAttributes(
          directory.FullName
        );
        directory.Delete(
          true
        );
      }
      else
      {
        entry.Attributes = FileAttributes.Normal;
        entry.Delete();
      }
    }

    using var response = await HttpClient.PutAsJsonAsync(
          "api/settings",
          BaselineSettings,
          TestJson.Options
    );
    if (!response.IsSuccessStatusCode)
    {
      throw new HttpRequestException(
        $"Baseline settings reset failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}"
      );
    }
    using var profilesResponse = await HttpClient.GetAsync(
      "api/workspaces"
    );
    profilesResponse.EnsureSuccessStatusCode();
    using var profilesDocument = JsonDocument.Parse(
      await profilesResponse.Content.ReadAsStringAsync()
    );
    var activeWorkspaceId = profilesDocument.RootElement.GetProperty(
      "activeWorkspaceId"
    ).GetString();

    foreach (var profile in profilesDocument.RootElement
      .GetProperty(
        "profiles"
      )
      .EnumerateArray()
      .ToArray())
    {
      var id = profile.GetProperty(
        "id"
      ).GetString()!;
      var path = profile.GetProperty(
        "path"
      ).GetString()!;

      if (!string.Equals(
        Path.GetFullPath(
          path
        ),
        Path.GetFullPath(
          WorkspaceDirectory
        ),
        StringComparison.OrdinalIgnoreCase
      ))
      {
        using var removed = await HttpClient.DeleteAsync(
          $"api/workspaces/{id}?confirmed=true"
        );
        removed.EnsureSuccessStatusCode();
        continue;
      }

      if (!string.Equals(activeWorkspaceId, id, StringComparison.Ordinal))
      {
        using var activated = await HttpClient.PostAsync(
          $"api/workspaces/{id}/activate",
          null
        );
        activated.EnsureSuccessStatusCode();
        activeWorkspaceId = id;
      }
      using var history = await HttpClient.PutAsJsonAsync(
        $"api/workspaces/{id}/history",
        new
        {
          enabled = false
        }
      );
      history.EnsureSuccessStatusCode();
      using var modelProfile = await HttpClient.PutAsJsonAsync(
        $"api/model-organization/workspaces/{id}/preferred-profile",
        new
        {
          profileId = (string?)null
        }
      );
      modelProfile.EnsureSuccessStatusCode();
    }

    using var deletedSessions = await HttpClient.DeleteAsync(
      "api/sessions?confirmed=true"
    );
    deletedSessions.EnsureSuccessStatusCode();
    using var purgedUsage = await HttpClient.DeleteAsync(
      "api/usage?confirmed=true"
    );
    purgedUsage.EnsureSuccessStatusCode();
    _fakeOllama.Reset();
    _fakeCloud.Reset();
  }

  public string CreateWorkspaceDirectory(
    string name
  )
  {
    var path = Path.Combine(
      _temporaryRoot,
      name
    );
    Directory.CreateDirectory(
      path
    );
    return path;
  }

  public async Task RestartApplicationAsync()
  {
    var startInfo = _apiProcess.StartInfo;

    if (!string.Equals(
      _apiProcess.ProcessName,
      "AgenticRouter.Api",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw new InvalidOperationException(
        $"Refusing to restart unexpected process {_apiProcess.ProcessName}."
      );
    }

    if (!_apiProcess.HasExited)
    {
      _apiProcess.Kill(true);
      await _apiProcess.WaitForExitAsync();
    }

    _apiProcess.Dispose();
    _apiOutput.Clear();
    _apiProcess = new Process
    {
      StartInfo = startInfo,
      EnableRaisingEvents = true
    };
    _apiProcess.OutputDataReceived += (
      _,
      eventArgs
    ) =>
    {
      if (eventArgs.Data is not null)
      {
        _apiOutput.AppendLine(
          eventArgs.Data
        );
      }
    };
    _apiProcess.ErrorDataReceived += (
      _,
      eventArgs
    ) =>
    {
      if (eventArgs.Data is not null)
      {
        _apiOutput.AppendLine(
          eventArgs.Data
        );
      }
    };

    if (!_apiProcess.Start())
    {
      throw new InvalidOperationException(
        "The E2E API process could not be restarted."
      );
    }

    _apiProcess.BeginOutputReadLine();
    _apiProcess.BeginErrorReadLine();
    await WaitUntilReadyAsync();
  }

  public async Task SetCodexExecutableAndRestartAsync(
    string executablePath
  )
  {
    _apiProcess.StartInfo.Environment[
      "AgenticRouter__Codex__ExecutablePath"
    ] = executablePath;
    await RestartApplicationAsync();
  }

  public async Task SetClaudeCodeExecutableAndRestartAsync(
    string executablePath
  )
  {
    _apiProcess.StartInfo.Environment[
      "AgenticRouter__ClaudeCode__ExecutablePath"
    ] = executablePath;
    await RestartApplicationAsync();
  }

  public async Task SetOpenCodeExecutableAndRestartAsync(
    string executablePath
  )
  {
    _apiProcess.StartInfo.Environment[
      "AgenticRouter__OpenCode__ExecutablePath"
    ] = executablePath;
    await RestartApplicationAsync();
  }

  public async Task SetQwenCodeExecutableAndRestartAsync(
    string executablePath
  )
  {
    _apiProcess.StartInfo.Environment[
      "AgenticRouter__QwenCode__ExecutablePath"
    ] = executablePath;
    await RestartApplicationAsync();
  }

  public async Task UseManagedCodexInstallAndRestartAsync()
  {
    var managedRoot = Path.Combine(
      _temporaryRoot,
      "managed-codex"
    );
    var managedVersion = Path.Combine(
      managedRoot,
      "current-test-version"
    );
    Directory.CreateDirectory(managedVersion);
    var sourceDirectory = Path.GetDirectoryName(FakeCodexExecutablePath)
      ?? throw new InvalidOperationException(
        "The fake Codex executable has no parent directory."
      );

    foreach (var source in Directory.EnumerateFiles(sourceDirectory))
    {
      File.Copy(
        source,
        Path.Combine(managedVersion, Path.GetFileName(source)),
        true
      );
    }

    File.Copy(
      FakeCodexExecutablePath,
      Path.Combine(
        managedVersion,
        OperatingSystem.IsWindows() ? "codex.exe" : "codex"
      ),
      true
    );
    _apiProcess.StartInfo.Environment.Remove(
      "AgenticRouter__Codex__ExecutablePath"
    );
    _apiProcess.StartInfo.Environment[
      "AgenticRouter__Codex__ManagedInstallRoot"
    ] = managedRoot;
    await RestartApplicationAsync();
  }

  public async Task<HttpResponseMessage> PutSettingsAsync(
    TestApplicationSettings settings
  )
  {
    return await HttpClient.PutAsJsonAsync(
      "api/settings",
      settings,
      TestJson.Options
    );
  }

  public async ValueTask DisposeAsync()
  {
    HttpClient.Dispose();

    if (!_apiProcess.HasExited)
    {
      _apiProcess.Kill(
        true
      );
      await _apiProcess.WaitForExitAsync();
    }

    _apiProcess.Dispose();
    await _fakeOllama.DisposeAsync();
    await _fakeCloud.DisposeAsync();

    if (Directory.Exists(
      _temporaryRoot
    ))
    {
      NormalizeDeletionAttributes(
        _temporaryRoot
      );
      Directory.Delete(
        _temporaryRoot,
        true
      );
    }
  }

  private async Task WaitUntilReadyAsync()
  {
    using var startupClient = new HttpClient
    {
      BaseAddress = BaseUri,
      Timeout = TimeSpan.FromSeconds(
        1
      )
    };
    using var timeout = new CancellationTokenSource(
      TimeSpan.FromSeconds(
        20
      )
    );

    while (!timeout.IsCancellationRequested)
    {
      if (_apiProcess.HasExited)
      {
        throw new InvalidOperationException(
          $"The E2E API process exited during startup.{Environment.NewLine}{_apiOutput}"
        );
      }

      try
      {
        using var response = await startupClient.GetAsync(
          "api/settings",
          timeout.Token
        );

        if (response.StatusCode == HttpStatusCode.OK)
        {
          return;
        }
      }
      catch (HttpRequestException)
      {
      }
      catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
      {
      }

      await Task.Delay(
        100,
        timeout.Token
      );
    }

    throw new TimeoutException(
      $"The E2E API did not become ready.{Environment.NewLine}{_apiOutput}"
    );
  }

  private static string FindRepositoryRoot()
  {
    var configuredRoot = Environment.GetEnvironmentVariable(
      "AGENTIC_ROUTER_REPOSITORY_ROOT"
    );
    IReadOnlyList<string> startPaths = string.IsNullOrWhiteSpace(
      configuredRoot
    )
      ?
      [
        Environment.CurrentDirectory,
        AppContext.BaseDirectory
      ]
      :
      [
        configuredRoot,
        Environment.CurrentDirectory,
        AppContext.BaseDirectory
      ];

    foreach (var startPath in startPaths)
    {
      var directory = new DirectoryInfo(
        startPath
      );

      while (directory is not null)
      {
        if (File.Exists(
          Path.Combine(
            directory.FullName,
            "AgenticRouter.slnx"
          )
        ))
        {
          return directory.FullName;
        }

        directory = directory.Parent;
      }
    }

    throw new DirectoryNotFoundException(
      "Could not locate the Agentic Router repository root."
    );
  }

  private static int GetAvailablePort()
  {
    var listener = new TcpListener(
      IPAddress.Loopback,
      0
    );
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
  }

  private static void NormalizeDeletionAttributes(
    string root
  )
  {
    foreach (var path in Directory.EnumerateFileSystemEntries(
      root,
      "*",
      SearchOption.AllDirectories
    ))
    {
      try
      {
        File.SetAttributes(
          path,
          FileAttributes.Normal
        );
      }
      catch (
        IOException
      )
      {
      }
      catch (
        UnauthorizedAccessException
      )
      {
      }
    }

    File.SetAttributes(
      root,
      FileAttributes.Normal
    );
  }
}
