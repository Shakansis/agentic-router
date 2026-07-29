using System.Diagnostics;
using System.Text;

namespace AgenticRouter.Api.Execution;

public interface IProcessExecutionService
{
  Task<ProcessExecutionResult> ExecuteAsync(
    ProcessExecutionRequest request,
    CancellationToken cancellationToken
  );
}

public sealed record ProcessExecutionRequest(
  string Executable,
  IReadOnlyList<string> Arguments,
  string WorkingDirectory,
  TimeSpan Timeout
);

public sealed record ProcessExecutionResult(
  int ExitCode,
  string StandardOutput,
  string StandardError,
  bool TimedOut,
  bool Cancelled,
  long DurationMilliseconds,
  bool StandardOutputTruncated,
  bool StandardErrorTruncated
);

public sealed class ProcessExecutionService : IProcessExecutionService
{
  private const int OutputLimit = 64 * 1_024;

  public async Task<ProcessExecutionResult> ExecuteAsync(
    ProcessExecutionRequest request,
    CancellationToken cancellationToken
  )
  {
    var stopwatch = Stopwatch.StartNew();
    using var timeout = new CancellationTokenSource(
      request.Timeout
    );
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken,
      timeout.Token
    );
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = request.Executable,
        WorkingDirectory = request.WorkingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };

    foreach (var argument in request.Arguments)
    {
      process.StartInfo.ArgumentList.Add(
        argument
      );
    }

    try
    {
      if (!process.Start())
      {
        throw new LocalActionException(
          "process-execution",
          "The process could not be started."
        );
      }
    }
    catch (Exception exception) when (
      exception is not LocalActionException
    )
    {
      throw new LocalActionException(
        "process-execution",
        $"The process '{request.Executable}' could not be started.",
        exception
      );
    }

    var standardOutput = ReadBoundedAsync(
      process.StandardOutput,
      linked.Token
    );
    var standardError = ReadBoundedAsync(
      process.StandardError,
      linked.Token
    );

    try
    {
      await process.WaitForExitAsync(
        linked.Token
      );
    }
    catch (OperationCanceledException)
    {
      TryKill(
        process
      );

      var cancelledOutput = await CompleteReadAsync(
        standardOutput
      );
      var cancelledError = await CompleteReadAsync(
        standardError
      );
      return new ProcessExecutionResult(
        -1,
        cancelledOutput.Text,
        cancelledError.Text,
        !cancellationToken.IsCancellationRequested,
        cancellationToken.IsCancellationRequested,
        stopwatch.ElapsedMilliseconds,
        cancelledOutput.Truncated,
        cancelledError.Truncated
      );
    }

    var output = await standardOutput;
    var error = await standardError;
    return new ProcessExecutionResult(
      process.ExitCode,
      output.Text,
      error.Text,
      false,
      false,
      stopwatch.ElapsedMilliseconds,
      output.Truncated,
      error.Truncated
    );
  }

  private static async Task<BoundedOutput> ReadBoundedAsync(
    StreamReader reader,
    CancellationToken cancellationToken
  )
  {
    var buffer = new char[4_096];
    var result = new StringBuilder();

    var truncated = false;

    while (true)
    {
      var read = await reader.ReadAsync(
        buffer.AsMemory(
          0,
          buffer.Length
        ),
        cancellationToken
      );

      if (read == 0)
      {
        break;
      }

      var accepted = Math.Min(
        read,
        OutputLimit - result.Length
      );

      if (accepted > 0)
      {
        result.Append(
          buffer,
          0,
          accepted
        );
      }

      truncated = truncated || accepted < read;
    }

    if (truncated)
    {
      result.Append(
        Environment.NewLine
      );
      result.Append(
        "[output truncated]"
      );
    }

    return new BoundedOutput(
      result.ToString(),
      truncated
    );
  }

  private static async Task<BoundedOutput> CompleteReadAsync(
    Task<BoundedOutput> readTask
  )
  {
    try
    {
      return await readTask;
    }
    catch (OperationCanceledException)
    {
      return new BoundedOutput(
        string.Empty,
        false
      );
    }
  }

  private static void TryKill(
    Process process
  )
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(
          true
        );
      }
    }
    catch (InvalidOperationException)
    {
    }
  }

  private sealed record BoundedOutput(
    string Text,
    bool Truncated
  );
}
