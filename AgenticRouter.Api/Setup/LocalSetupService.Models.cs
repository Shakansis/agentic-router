using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Runtime;

namespace AgenticRouter.Api.Setup;

public sealed partial class LocalSetupService
{
  private readonly IConfiguration _configuration;
  private readonly IModelRequestTracker _requests;

  public SetupJobState? GetModelJob(string model) => GetJob(ModelJobKey(ValidateModelName(model)));

  private static string ValidateModelName(string model)
  {
    model = model.Trim();
    if (model.Length is < 1 or > 200 || !Regex.IsMatch(model,
      @"\A[a-zA-Z0-9][a-zA-Z0-9._-]*(?:/[a-zA-Z0-9][a-zA-Z0-9._-]*)*(?::[a-zA-Z0-9][a-zA-Z0-9._-]*)?\z",
      RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
      throw new SetupException("invalid-model-name", "Enter an Ollama model name, for example gemma4:12b or namespace/model:tag.");
    return model.Contains(':', StringComparison.Ordinal) ? model : model + ":latest";
  }

  public async Task<SetupActionResult> RemoveModelAsync(string model, CancellationToken cancellationToken)
  {
    ThrowIfMutationUnavailable();
    model = ValidateModelName(model);
    var key = ModelJobKey(model);
    var initial = new SetupJobState("removing", $"Removing {model}.", DateTimeOffset.UtcNow, null, null);
    if (!_jobs.TryAdd(key, initial))
    {
      var previous = _jobs[key];
      if (previous.State is "downloading" or "removing" || !_jobs.TryUpdate(key, initial, previous))
        throw new SetupException("model-operation-active", "A model operation is already in progress. Wait for it to finish.");
    }
    try
    {
      // Serialize against AR inference so a removal never interrupts an active AR turn.
      using var lease = await _requests.BeginRequestAsync(true, cancellationToken);
      var settings = await _settingsStore.GetAsync(cancellationToken);
      var endpoint = new Uri(settings.OllamaUrl);
      var installed = await _ollama.GetModelsAsync(endpoint, cancellationToken);
      if (!installed.Any(item => item.Name == model))
        throw new SetupException("model-not-installed", "This exact model is no longer installed. Refresh the model library.");
      var start = new ProcessStartInfo
      {
        FileName = ResolveModelCli(),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      start.ArgumentList.Add("rm");
      start.ArgumentList.Add(model);
      start.Environment["OLLAMA_HOST"] = endpoint.GetLeftPart(UriPartial.Authority);
      using var process = Process.Start(start) ?? throw new SetupException("model-remove-start-failed", "The Ollama CLI could not start.");
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _applicationLifetime.ApplicationStopping);
      timeout.CancelAfter(TimeSpan.FromMinutes(2));
      try
      {
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
          throw new SetupException("model-remove-failed", $"Ollama could not remove {model} (exit {process.ExitCode}). Refresh the library and check that Ollama is available.");
      }
      catch (OperationCanceledException)
      {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
        throw new SetupException("model-remove-interrupted", "Removal was interrupted. Refresh the library to check whether the model was removed.");
      }
      installed = await _ollama.GetModelsAsync(endpoint, cancellationToken);
      if (installed.Any(item => item.Name == model))
        throw new SetupException("model-remove-unverified", "Ollama exited successfully but the model is still installed. Refresh before retrying.");
      _jobs[key] = initial with { State = "removed", Message = $"{model} was removed.", UpdatedAt = DateTimeOffset.UtcNow };
      return new SetupActionResult(model, true, "removed", $"{model} was removed. Routing and saved preferences were preserved.");
    }
    catch (Exception exception)
    {
      _jobs[key] = initial with { State = "failed", Message = "Model removal did not complete.", UpdatedAt = DateTimeOffset.UtcNow };
      _logger.LogWarning(exception, "Model removal failed for {Model}.", model);
      if (exception is Win32Exception or IOException)
        throw new SetupException("model-remove-start-failed", "The Ollama CLI could not run. Check the Ollama installation and try again.", exception);
      throw;
    }
  }

  private string ResolveModelCli()
  {
    var configured = _configuration["AgenticRouter:ManagedOllama:ExecutablePath"];
    if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
    var name = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
    var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(path => Path.Combine(path, name));
    if (OperatingSystem.IsWindows())
      candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", name) }.Concat(candidates);
    return candidates.FirstOrDefault(File.Exists)
      ?? throw new SetupException("ollama-cli-unavailable", "Ollama CLI was not found. Install Ollama or add it to PATH.");
  }
}
