using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkNativeExecutor
{
  IAsyncEnumerable<BenchmarkHarnessEvidence> ExecuteAsync(
    IBenchmarkTestDefinition test,
    InstalledModel model,
    Uri providerEndpoint,
    BenchmarkWorkspace workspace,
    int? contextTokens,
    BenchmarkProgressContext? progress,
    CancellationToken cancellationToken
  );

  Task<string> ExecuteToolAsync(
    string workspacePath,
    string tool,
    JsonElement arguments,
    CancellationToken cancellationToken
  );
}

public sealed class BenchmarkNativeExecutor : IBenchmarkNativeExecutor
{
  public const string PromptMarker = "BENCHMARK_NATIVE_CRUD_V1";
  private const int MaximumTurns = 8;
  private const int MaximumReadBytes = 1024 * 1024;
  private static readonly UTF8Encoding Utf8 = new(false);
  private static readonly IReadOnlyList<OllamaToolDefinition> Tools =
    LocalActionPlanner.GetToolDefinitions(
      [
        "read_file",
        "create_file",
        "create_files",
        "write_file",
        "replace_text",
        "delete_paths"
      ]
    ).Select(definition => new OllamaToolDefinition(
      definition.Name,
      definition.Description,
      definition.Parameters
    )).ToArray();

  private readonly IOllamaClient _ollamaClient;

  public BenchmarkNativeExecutor(IOllamaClient ollamaClient)
  {
    _ollamaClient = ollamaClient;
  }

  public async IAsyncEnumerable<BenchmarkHarnessEvidence> ExecuteAsync(
    IBenchmarkTestDefinition test,
    InstalledModel model,
    Uri providerEndpoint,
    BenchmarkWorkspace workspace,
    int? contextTokens,
    BenchmarkProgressContext? progress,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var messages = new List<OllamaToolMessage>
    {
      new(
        "system",
        PromptMarker + "\n"
          + "You are executing one deterministic filesystem benchmark in the supplied trusted workspace. "
          + "Use only the provided structured tools. Never use or request a shell. Paths are relative to the workspace. "
          + "After the required effect is complete, return a concise final report with no tool call."
      ),
      new("user", test.CreateTask())
    };
    var toolCalls = 0;
    var surfacedErrors = 0;
    long? inputTokens = null;
    long? outputTokens = null;

    for (var attempt = 1; attempt <= MaximumTurns; attempt++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var response = await _ollamaClient.GenerateToolCallAsync(
        providerEndpoint,
        model.Name,
        messages,
        Tools,
        "benchmark-native",
        new ProviderCallContext(
          workspace.Id,
          null,
          $"{workspace.Id}-{attempt}",
          workspace.Id,
          UsageModelRoles.Benchmark,
          test.Metadata.Id,
          model.Digest,
          contextTokens
        ),
        cancellationToken
      );
      if (response.Usage is not null)
      {
        inputTokens = (inputTokens ?? 0) + response.Usage.InputTokens;
        outputTokens = (outputTokens ?? 0) + response.Usage.OutputTokens;
      }

      if (response.ToolCalls.Count == 0)
      {
        var report = response.Content?.Trim() ?? string.Empty;
        if (surfacedErrors > 0)
        {
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.Running,
            $"Harness recovered after {surfacedErrors} surfaced tool error(s).",
            BenchmarkActivityKindIds.RecoveredError
          );
        }
        progress?.Publish(
          BenchmarkProgressTypeIds.Activity,
          BenchmarkLiveStateIds.HarnessCompleted,
          "Native harness returned a terminal result.",
          BenchmarkActivityKindIds.HarnessTerminal
        );
        yield return new BenchmarkHarnessEvidence(
          BenchmarkExecutionStatusIds.Completed,
          null,
          report,
          toolCalls,
          surfacedErrors,
          surfacedErrors,
          inputTokens,
          outputTokens
        );
        yield break;
      }

      if (response.ToolCalls.Count != 1)
      {
        yield return Failure(
          "benchmark-native-multiple-tools",
          "Native returned more than one structured action in a benchmark turn.",
          toolCalls,
          surfacedErrors + 1,
          inputTokens,
          outputTokens
        );
        yield break;
      }

      var call = response.ToolCalls[0];
      toolCalls++;
      progress?.Publish(
        BenchmarkProgressTypeIds.Activity,
        BenchmarkLiveStateIds.Running,
        $"Executing {call.Name}.",
        ActivityKind(call.Name)
      );
      messages.Add(
        new OllamaToolMessage(
          "assistant",
          response.Content,
          response.Thinking,
          response.ToolCalls
        )
      );
      string toolResult;
      try
      {
        toolResult = await ExecuteToolAsync(
          workspace.WorkspacePath,
          call.Name,
          call.Arguments,
          cancellationToken
        );
      }
      catch (BenchmarkNativeToolException exception)
      {
        surfacedErrors++;
        toolResult = $"ERROR {exception.Code}: {exception.Message}";
        progress?.Publish(
          BenchmarkProgressTypeIds.Activity,
          BenchmarkLiveStateIds.Running,
          $"{exception.Code}: {exception.Message}",
          BenchmarkActivityKindIds.Tool
        );
      }
      messages.Add(
        new OllamaToolMessage(
          "tool",
          toolResult,
          ToolName: call.Name,
          ToolCallId: call.Id
        )
      );
    }

    yield return Failure(
      "benchmark-native-turn-budget",
      $"Native exhausted the bounded {MaximumTurns}-turn benchmark tool loop.",
      toolCalls,
      surfacedErrors,
      inputTokens,
      outputTokens
    );
  }

  internal static string ActivityKind(string tool)
  {
    return tool switch
    {
      "read_file" => BenchmarkActivityKindIds.FileRead,
      "create_file" or "create_files" => BenchmarkActivityKindIds.FileCreate,
      "write_file" or "replace_text" => BenchmarkActivityKindIds.FileEdit,
      "delete_paths" => BenchmarkActivityKindIds.FileDelete,
      "run_process" => BenchmarkActivityKindIds.Process,
      _ => BenchmarkActivityKindIds.Tool
    };
  }

  private static BenchmarkHarnessEvidence Failure(
    string code,
    string message,
    int toolCalls,
    int surfacedErrors,
    long? inputTokens,
    long? outputTokens
  )
  {
    return new BenchmarkHarnessEvidence(
      BenchmarkExecutionStatusIds.Failed,
      new BenchmarkError(code, message, "native-harness-execution", true),
      string.Empty,
      toolCalls,
      surfacedErrors,
      0,
      inputTokens,
      outputTokens
    );
  }

  public async Task<string> ExecuteToolAsync(
    string workspacePath,
    string tool,
    JsonElement arguments,
    CancellationToken cancellationToken
  )
  {
    return tool switch
    {
      "read_file" => await ReadFileAsync(workspacePath, arguments, cancellationToken),
      "create_file" => await CreateFileAsync(workspacePath, arguments, cancellationToken),
      "create_files" => await CreateFilesAsync(workspacePath, arguments, cancellationToken),
      "write_file" => await WriteFileAsync(workspacePath, arguments, cancellationToken),
      "replace_text" => await ReplaceTextAsync(workspacePath, arguments, cancellationToken),
      "delete_paths" => DeletePaths(workspacePath, arguments),
      _ => throw new BenchmarkNativeToolException(
        "benchmark-native-tool-unknown",
        $"Tool '{tool}' is unavailable in the benchmark runtime."
      )
    };
  }

  private static async Task<string> ReadFileAsync(
    string workspacePath,
    JsonElement arguments,
    CancellationToken cancellationToken
  )
  {
    var relative = RequiredString(arguments, "path");
    var path = ResolveOwnedPath(workspacePath, relative);
    if (!File.Exists(path))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-file-missing",
        $"File '{relative}' does not exist."
      );
    }
    EnsureNotReparsePoint(path);
    var length = new FileInfo(path).Length;
    if (length > MaximumReadBytes)
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-read-limit",
        $"File '{relative}' exceeds the benchmark read limit."
      );
    }
    return await File.ReadAllTextAsync(path, cancellationToken);
  }

  private static async Task<string> CreateFileAsync(
    string workspacePath,
    JsonElement arguments,
    CancellationToken cancellationToken
  )
  {
    var relative = RequiredString(arguments, "path");
    var content = RequiredString(arguments, "content", allowEmpty: true);
    await CreateOneAsync(workspacePath, relative, content, cancellationToken);
    return $"Created {Normalize(relative)} with {Utf8.GetByteCount(content)} bytes.";
  }

  private static async Task<string> CreateFilesAsync(
    string workspacePath,
    JsonElement arguments,
    CancellationToken cancellationToken
  )
  {
    if (
      !arguments.TryGetProperty("files", out var files)
      || files.ValueKind != JsonValueKind.Array
      || files.GetArrayLength() is < 1 or > 50
    )
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-files-invalid",
        "create_files requires between 1 and 50 file entries."
      );
    }
    var entries = files.EnumerateArray().Select(file => (
      Path: RequiredString(file, "path"),
      Content: RequiredString(file, "content", allowEmpty: true)
    )).ToArray();
    foreach (var entry in entries)
    {
      var path = ResolveOwnedPath(workspacePath, entry.Path);
      if (File.Exists(path) || Directory.Exists(path))
      {
        throw new BenchmarkNativeToolException(
          "benchmark-native-create-existing",
          $"Path '{entry.Path}' already exists."
        );
      }
    }
    foreach (var entry in entries)
    {
      await CreateOneAsync(
        workspacePath,
        entry.Path,
        entry.Content,
        cancellationToken
      );
    }
    return $"Created {entries.Length} file(s).";
  }

  private static async Task CreateOneAsync(
    string workspacePath,
    string relative,
    string content,
    CancellationToken cancellationToken
  )
  {
    var path = ResolveOwnedPath(workspacePath, relative);
    if (File.Exists(path) || Directory.Exists(path))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-create-existing",
        $"Path '{relative}' already exists."
      );
    }
    var parent = Path.GetDirectoryName(path)
      ?? throw new BenchmarkNativeToolException(
        "benchmark-native-parent-invalid",
        $"Path '{relative}' has no valid parent."
      );
    Directory.CreateDirectory(parent);
    EnsureNoReparsePoints(workspacePath, parent);
    await File.WriteAllTextAsync(path, content, Utf8, cancellationToken);
  }

  private static async Task<string> WriteFileAsync(
    string workspacePath,
    JsonElement arguments,
    CancellationToken cancellationToken
  )
  {
    var relative = RequiredString(arguments, "path");
    var content = RequiredString(arguments, "content", allowEmpty: true);
    var path = ResolveOwnedPath(workspacePath, relative);
    if (!File.Exists(path))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-write-missing",
        $"File '{relative}' does not exist."
      );
    }
    EnsureNotReparsePoint(path);
    await File.WriteAllTextAsync(path, content, Utf8, cancellationToken);
    return $"Updated {Normalize(relative)} with {Utf8.GetByteCount(content)} bytes.";
  }

  private static async Task<string> ReplaceTextAsync(
    string workspacePath,
    JsonElement arguments,
    CancellationToken cancellationToken
  )
  {
    var relative = RequiredString(arguments, "path");
    var oldText = RequiredString(arguments, "oldText", allowEmpty: false);
    var newText = RequiredString(arguments, "newText", allowEmpty: true);
    var replaceAll = arguments.TryGetProperty("replaceAll", out var replaceAllElement)
      && replaceAllElement.ValueKind == JsonValueKind.True;
    var path = ResolveOwnedPath(workspacePath, relative);
    if (!File.Exists(path))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-replace-missing",
        $"File '{relative}' does not exist."
      );
    }
    EnsureNotReparsePoint(path);
    var content = await File.ReadAllTextAsync(path, cancellationToken);
    var occurrences = CountOccurrences(content, oldText);
    if (occurrences == 0 || (!replaceAll && occurrences != 1))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-replace-ambiguous",
        $"replace_text expected {(replaceAll ? "at least one" : "exactly one")} match in '{relative}', observed {occurrences}."
      );
    }
    var updated = replaceAll
      ? content.Replace(oldText, newText, StringComparison.Ordinal)
      : ReplaceFirst(content, oldText, newText);
    await File.WriteAllTextAsync(path, updated, Utf8, cancellationToken);
    return $"Replaced {occurrences} occurrence(s) in {Normalize(relative)}.";
  }

  private static string DeletePaths(
    string workspacePath,
    JsonElement arguments
  )
  {
    if (
      !arguments.TryGetProperty("paths", out var paths)
      || paths.ValueKind != JsonValueKind.Array
      || paths.GetArrayLength() is < 1 or > 50
    )
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-delete-invalid",
        "delete_paths requires between 1 and 50 explicit paths."
      );
    }
    var recursive = arguments.TryGetProperty("recursive", out var recursiveElement)
      && recursiveElement.ValueKind == JsonValueKind.True;
    var targets = paths.EnumerateArray().Select(path => path.GetString()).ToArray();
    if (targets.Any(string.IsNullOrWhiteSpace))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-delete-invalid",
        "delete_paths contains an empty path."
      );
    }
    var files = targets.Select(relative =>
    {
      var path = ResolveOwnedPath(workspacePath, relative!);
      if (!File.Exists(path))
      {
        throw new BenchmarkNativeToolException(
          "benchmark-native-delete-missing",
          $"Path '{relative}' does not exist."
        );
      }
      EnsureNotReparsePoint(path);
      return path;
    }).ToArray();
    _ = recursive;
    foreach (var path in files)
    {
      File.Delete(path);
    }
    return $"Deleted {targets.Length} path(s).";
  }

  private static string ResolveOwnedPath(
    string workspacePath,
    string relative
  )
  {
    if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-path-invalid",
        "Benchmark tool paths must be non-empty and relative."
      );
    }
    var root = Path.GetFullPath(workspacePath);
    var path = Path.GetFullPath(Path.Combine(
      root,
      relative.Replace('/', Path.DirectorySeparatorChar)
    ));
    var containment = Path.GetRelativePath(root, path);
    if (
      Path.IsPathRooted(containment)
      || string.Equals(containment, "..", StringComparison.Ordinal)
      || containment.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      || containment.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
      || string.Equals(containment, ".", StringComparison.Ordinal)
    )
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-path-escape",
        $"Path '{relative}' is outside the disposable benchmark workspace."
      );
    }
    EnsureNoReparsePoints(root, Path.GetDirectoryName(path) ?? root);
    return path;
  }

  private static void EnsureNoReparsePoints(
    string workspacePath,
    string candidateDirectory
  )
  {
    var root = Path.GetFullPath(workspacePath);
    var current = new DirectoryInfo(Path.GetFullPath(candidateDirectory));
    while (true)
    {
      if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        throw new BenchmarkNativeToolException(
          "benchmark-native-reparse-point",
          "Benchmark tool paths must not traverse reparse points."
        );
      }
      if (string.Equals(current.FullName, root, PathComparison))
      {
        break;
      }
      current = current.Parent
        ?? throw new BenchmarkNativeToolException(
          "benchmark-native-path-escape",
          "Benchmark tool path escaped the disposable workspace."
        );
    }
  }

  private static void EnsureNotReparsePoint(string path)
  {
    if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-reparse-point",
        "Benchmark tools refuse reparse-point targets."
      );
    }
  }

  private static string RequiredString(
    JsonElement arguments,
    string property,
    bool allowEmpty = false
  )
  {
    if (
      !arguments.TryGetProperty(property, out var value)
      || value.ValueKind != JsonValueKind.String
      || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString()))
    )
    {
      throw new BenchmarkNativeToolException(
        "benchmark-native-arguments-invalid",
        $"Tool argument '{property}' must be a{(allowEmpty ? string.Empty : " non-empty")} string."
      );
    }
    return value.GetString() ?? string.Empty;
  }

  private static int CountOccurrences(string content, string value)
  {
    var count = 0;
    var index = 0;
    while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
    {
      count++;
      index += value.Length;
    }
    return count;
  }

  private static string ReplaceFirst(string content, string oldText, string newText)
  {
    var index = content.IndexOf(oldText, StringComparison.Ordinal);
    return index < 0
      ? content
      : string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
  }

  private static string Normalize(string relative)
  {
    return relative.Replace('\\', '/');
  }

  private static StringComparison PathComparison => OperatingSystem.IsWindows()
    ? StringComparison.OrdinalIgnoreCase
    : StringComparison.Ordinal;
}

public sealed class BenchmarkNativeToolException : Exception
{
  public BenchmarkNativeToolException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}
