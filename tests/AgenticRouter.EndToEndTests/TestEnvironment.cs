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
    foreach (var entry in new DirectoryInfo(
      WorkspaceDirectory
    ).EnumerateFileSystemInfos())
    {
      if (entry is DirectoryInfo directory)
      {
        directory.Delete(
          true
        );
      }
      else
      {
        entry.Delete();
      }
    }

    using var response = await HttpClient.PutAsJsonAsync(
          "api/settings",
          BaselineSettings,
          TestJson.Options
    );
    response.EnsureSuccessStatusCode();
    using var profilesResponse = await HttpClient.GetAsync(
      "api/workspaces"
    );
    profilesResponse.EnsureSuccessStatusCode();
    using var profilesDocument = JsonDocument.Parse(
      await profilesResponse.Content.ReadAsStringAsync()
    );

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

      using var activated = await HttpClient.PostAsync(
        $"api/workspaces/{id}/activate",
        null
      );
      activated.EnsureSuccessStatusCode();
      using var history = await HttpClient.PutAsJsonAsync(
        $"api/workspaces/{id}/history",
        new
        {
          enabled = false
        }
      );
      history.EnsureSuccessStatusCode();
    }

    using var deletedSessions = await HttpClient.DeleteAsync(
      "api/sessions?confirmed=true"
    );
    deletedSessions.EnsureSuccessStatusCode();
    _fakeOllama.Reset();
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
      _apiProcess.Kill();
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

    if (Directory.Exists(
      _temporaryRoot
    ))
    {
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
    var directory = new DirectoryInfo(
      AppContext.BaseDirectory
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
}
