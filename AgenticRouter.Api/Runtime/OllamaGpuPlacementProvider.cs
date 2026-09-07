using System.ComponentModel;
using System.Diagnostics;

namespace AgenticRouter.Api.Runtime;

public interface IOllamaGpuPlacementProvider
{
  OllamaGpuPlacementSnapshot GetStatus(
    Uri ollamaEndpoint
  );
}

public sealed class NoOpOllamaGpuPlacementProvider : IOllamaGpuPlacementProvider
{
  public OllamaGpuPlacementSnapshot GetStatus(
    Uri ollamaEndpoint
  )
  {
    return new OllamaGpuPlacementSnapshot(
      null,
      "unavailable",
      "Loaded-model GPU placement is not observable on this platform."
    );
  }
}

public sealed class WindowsOllamaGpuPlacementProvider : IOllamaGpuPlacementProvider
{
  private static readonly string[] RunnerProcessNames =
  [
    "llama-server",
    "ollama_llama_server"
  ];

  public OllamaGpuPlacementSnapshot GetStatus(
    Uri ollamaEndpoint
  )
  {
    if (!OperatingSystem.IsWindows())
    {
      return new OllamaGpuPlacementSnapshot(
        null,
        "unavailable",
        "Windows Ollama runner inspection is unavailable on this operating system."
      );
    }
    if (!ollamaEndpoint.IsLoopback)
    {
      return new OllamaGpuPlacementSnapshot(
        null,
        "unavailable",
        "The configured Ollama endpoint is remote, so its runner backend cannot be inspected locally."
      );
    }
    if (ollamaEndpoint.Port != 11_434)
    {
      return new OllamaGpuPlacementSnapshot(
        null,
        "unavailable",
        "The configured Ollama endpoint uses a custom local port, so local runner processes cannot be correlated with it safely."
      );
    }

    var runners = RunnerProcessNames.SelectMany(
      Process.GetProcessesByName
    ).ToArray();
    if (runners.Length == 0)
    {
      return new OllamaGpuPlacementSnapshot(
        null,
        "not-observed",
        "No local Ollama runner process was observed."
      );
    }

    var backends = new HashSet<string>(
      StringComparer.Ordinal
    );
    var inaccessible = 0;
    try
    {
      foreach (var runner in runners)
      {
        try
        {
          foreach (ProcessModule module in runner.Modules)
          {
            var name = module.ModuleName;
            if (
              name.Contains(
                "ggml-hip",
                StringComparison.OrdinalIgnoreCase
              )
              || name.Contains(
                "amdhip",
                StringComparison.OrdinalIgnoreCase
              )
            )
            {
              backends.Add(
                "rocm"
              );
            }
            else if (
              name.Contains(
                "ggml-cuda",
                StringComparison.OrdinalIgnoreCase
              )
              || name.Contains(
                "cublas",
                StringComparison.OrdinalIgnoreCase
              )
            )
            {
              backends.Add(
                "cuda"
              );
            }
            else if (
              name.Contains(
                "ggml-vulkan",
                StringComparison.OrdinalIgnoreCase
              )
            )
            {
              backends.Add(
                "vulkan"
              );
            }
          }
        }
        catch (Exception exception) when (
          exception is Win32Exception
            or InvalidOperationException
            or NotSupportedException
        )
        {
          inaccessible++;
        }
      }
    }
    finally
    {
      foreach (var runner in runners)
      {
        runner.Dispose();
      }
    }

    if (backends.Count == 1)
    {
      var backend = backends.Single();
      return new OllamaGpuPlacementSnapshot(
        backend,
        inaccessible == 0
          ? "observed"
          : "partial",
        $"Local Ollama runner modules identify the {BackendLabel(backend)} backend."
          + (inaccessible == 0
            ? string.Empty
            : $" {inaccessible} runner process(es) could not be inspected.")
      );
    }
    if (backends.Count > 1)
    {
      return new OllamaGpuPlacementSnapshot(
        null,
        "ambiguous",
        $"Local Ollama runners use multiple GPU backends: {string.Join(
          ", ",
          backends.Order(
            StringComparer.Ordinal
          ).Select(
            BackendLabel
          )
        )}. The running-model API does not identify which runner owns each model."
      );
    }

    return new OllamaGpuPlacementSnapshot(
      null,
      inaccessible == runners.Length
        ? "unavailable"
        : "not-observed",
      inaccessible == runners.Length
        ? "Local Ollama runner modules could not be inspected."
        : "No CUDA, ROCm, or Vulkan module was observed in the local Ollama runners."
    );
  }

  private static string BackendLabel(
    string backend
  )
  {
    return backend switch
    {
      "cuda" => "CUDA",
      "rocm" => "ROCm",
      "vulkan" => "Vulkan",
      _ => backend
    };
  }
}

public sealed record OllamaGpuPlacementSnapshot(
  string? Backend,
  string Status,
  string? Diagnostic
);
