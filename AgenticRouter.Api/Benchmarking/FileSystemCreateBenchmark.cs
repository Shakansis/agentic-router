using System.Text;

namespace AgenticRouter.Api.Benchmarking;

public sealed class FileSystemCreateBenchmark : IBenchmarkTestDefinition
{
  private const string ExpectedDirectory = "benchmark-data";
  private const string ExpectedFileName = "result.txt";
  private const string ExpectedRelativePath = "benchmark-data/result.txt";
  private const string ExpectedContent = "Agentic Router Benchmark\noperation=create\nresult=success";
  private static readonly byte[] ExpectedBytes = new UTF8Encoding(false).GetBytes(
    ExpectedContent
  );

  public BenchmarkTestMetadata Metadata { get; } = new(
    BenchmarkIds.FileSystemCreate001,
    1,
    "Create one exact UTF-8 file",
    "Core CRUD",
    "Create benchmark-data/result.txt with canonical bytes and no unrelated workspace changes.",
    true,
    [BenchmarkHarnessCapabilityIds.FileCreation]
  );

  public async Task PrepareFixtureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    var fixtureDirectory = Path.Combine(workspacePath, "fixture");
    Directory.CreateDirectory(fixtureDirectory);
    await File.WriteAllTextAsync(
      Path.Combine(fixtureDirectory, "keep.txt"),
      "fixture-keep-v1\n",
      new UTF8Encoding(false),
      cancellationToken
    );
    await File.WriteAllTextAsync(
      Path.Combine(fixtureDirectory, "delete.txt"),
      "fixture-delete-v1\n",
      new UTF8Encoding(false),
      cancellationToken
    );
  }

  public string CreateTask()
  {
    return "Benchmark test: FS-CREATE-001 (version 1).\n"
      + "Inside the provided workspace, create exactly benchmark-data/result.txt "
      + "with the following UTF-8 text and no byte-order mark:\n\n"
      + ExpectedContent
      + "\n\nThe canonical file ends immediately after 'success'; do not append a trailing newline. "
      + "Do not modify or delete any existing file and do not create any other file.";
  }

  public async Task<BenchmarkRawResult> ValidateAsync(
    BenchmarkValidationContext context,
    CancellationToken cancellationToken
  )
  {
    var comparer = BenchmarkWorkspaceFactory.PathComparer;
    var expectedDirectoryPath = ResolveDirectory(
      context.WorkspacePath,
      ExpectedDirectory,
      comparer
    );
    var directoryAccuracy = expectedDirectoryPath is null ? 0 : 100;
    var expectedFilePath = expectedDirectoryPath is null
      ? null
      : ResolveFile(expectedDirectoryPath, ExpectedFileName, comparer);
    var filenameAccuracy = expectedFilePath is null ? 0 : 100;
    var byteAccuracy = 0;
    if (expectedFilePath is not null)
    {
      var actualBytes = await File.ReadAllBytesAsync(
        expectedFilePath,
        cancellationToken
      );
      byteAccuracy = actualBytes.AsSpan().SequenceEqual(ExpectedBytes) ? 100 : 0;
    }

    var unexpectedCreated = context.FinalSnapshot.Entries
      .Where(pair => !context.InitialSnapshot.Entries.ContainsKey(pair.Key))
      .Where(pair => !comparer.Equals(pair.Key, ExpectedRelativePath))
      .Select(pair => pair.Key)
      .OrderBy(path => path, comparer)
      .ToArray();
    var unexpectedModified = context.FinalSnapshot.Entries
      .Where(pair => context.InitialSnapshot.Entries.TryGetValue(pair.Key, out var initial)
        && (
          !string.Equals(initial.Kind, pair.Value.Kind, StringComparison.Ordinal)
          || !string.Equals(initial.ContentHash, pair.Value.ContentHash, StringComparison.Ordinal)
        ))
      .Where(pair => !comparer.Equals(pair.Key, ExpectedRelativePath))
      .Select(pair => pair.Key)
      .OrderBy(path => path, comparer)
      .ToArray();
    var unexpectedDeleted = context.InitialSnapshot.Entries.Keys
      .Where(path => !context.FinalSnapshot.Entries.ContainsKey(path))
      .OrderBy(path => path, comparer)
      .ToArray();
    var containmentAccuracy = unexpectedCreated.Length == 0
      && unexpectedModified.Length == 0
      && unexpectedDeleted.Length == 0
        ? 100
        : 0;
    var objectiveAchieved = directoryAccuracy == 100
      && filenameAccuracy == 100
      && byteAccuracy == 100;
    var executionCompleted = string.Equals(
      context.ExecutionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    );
    var status = executionCompleted
      ? objectiveAchieved && containmentAccuracy == 100
        ? BenchmarkResultStatusIds.Pass
        : BenchmarkResultStatusIds.Fail
      : BenchmarkResultStatusIds.Error;

    return new BenchmarkRawResult(
      status,
      objectiveAchieved,
      byteAccuracy,
      directoryAccuracy,
      filenameAccuracy,
      containmentAccuracy,
      unexpectedCreated,
      unexpectedModified,
      unexpectedDeleted,
      context.ExecutionStatus,
      context.ExecutionError
    );
  }

  private static string? ResolveDirectory(
    string root,
    string name,
    StringComparer comparer
  )
  {
    return new DirectoryInfo(root).EnumerateDirectories()
      .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
      .FirstOrDefault(directory => comparer.Equals(directory.Name, name))
      ?.FullName;
  }

  private static string? ResolveFile(
    string directory,
    string name,
    StringComparer comparer
  )
  {
    return new DirectoryInfo(directory).EnumerateFiles()
      .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
      .FirstOrDefault(file => comparer.Equals(file.Name, name))
      ?.FullName;
  }
}
