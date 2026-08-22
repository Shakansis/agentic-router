using System.Security.Cryptography;
using System.Text;

namespace AgenticRouter.Api.Benchmarking;

internal static class BasicCrudFixture
{
  public const string ReadPrimaryContent = "project=Agentic Router\ncodename=ORBIT-41\n";
  public const string ReadSecondaryContent = "verification-word=marigold\nrelease-channel=local\n";
  public const string UpdateInitialContent = "mode=preview\nretries=2\nowner=router\n";
  public const string UpdateExpectedContent = "mode=preview\nretries=3\nowner=router\n";
  public const string DeleteContent = "remove-this-file-only\n";
  public const string KeepContent = "canonical-fixture-keep-v1\n";

  public static async Task PrepareAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    var fixture = Path.Combine(workspacePath, "fixture");
    Directory.CreateDirectory(fixture);
    await WriteAsync(fixture, "keep.txt", KeepContent, cancellationToken);
    await WriteAsync(fixture, "read-primary.txt", ReadPrimaryContent, cancellationToken);
    await WriteAsync(fixture, "read-secondary.txt", ReadSecondaryContent, cancellationToken);
    await WriteAsync(fixture, "update.txt", UpdateInitialContent, cancellationToken);
    await WriteAsync(fixture, "delete.txt", DeleteContent, cancellationToken);
  }

  private static Task WriteAsync(
    string directory,
    string fileName,
    string content,
    CancellationToken cancellationToken
  )
  {
    return File.WriteAllTextAsync(
      Path.Combine(directory, fileName),
      content,
      new UTF8Encoding(false),
      cancellationToken
    );
  }
}

public sealed record BenchmarkChangeSet(
  IReadOnlyList<string> Created,
  IReadOnlyList<string> Modified,
  IReadOnlyList<string> Deleted
)
{
  public IReadOnlyList<string> All => Created
    .Concat(Modified)
    .Concat(Deleted)
    .Distinct(BenchmarkWorkspaceFactory.PathComparer)
    .OrderBy(path => path, BenchmarkWorkspaceFactory.PathComparer)
    .ToArray();
}

public abstract class BasicCrudBenchmark : IBenchmarkTestDefinition
{
  public abstract BenchmarkTestMetadata Metadata { get; }

  protected abstract IReadOnlySet<string> ExpectedCreated { get; }

  protected abstract IReadOnlySet<string> ExpectedModified { get; }

  protected abstract IReadOnlySet<string> ExpectedDeleted { get; }

  public Task PrepareFixtureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    return BasicCrudFixture.PrepareAsync(workspacePath, cancellationToken);
  }

  public abstract string CreateTask();

  public async Task<BenchmarkRawResult> ValidateAsync(
    BenchmarkValidationContext context,
    CancellationToken cancellationToken
  )
  {
    var changes = CalculateChanges(context.InitialSnapshot, context.FinalSnapshot);
    var unexpectedCreated = Except(changes.Created, ExpectedCreated);
    var unexpectedModified = Except(changes.Modified, ExpectedModified);
    var unexpectedDeleted = Except(changes.Deleted, ExpectedDeleted);
    var missingExpectedChange = ExpectedCreated.Except(
      changes.Created,
      BenchmarkWorkspaceFactory.PathComparer
    ).Concat(ExpectedModified.Except(
      changes.Modified,
      BenchmarkWorkspaceFactory.PathComparer
    )).Concat(ExpectedDeleted.Except(
      changes.Deleted,
      BenchmarkWorkspaceFactory.PathComparer
    )).ToArray();
    var workspaceAccuracy = unexpectedCreated.Count == 0
      && unexpectedModified.Count == 0
      && unexpectedDeleted.Count == 0
        ? 100
        : 0;
    var validation = await ValidateObjectiveAsync(
      context,
      changes,
      cancellationToken
    );
    var executionCompleted = string.Equals(
      context.ExecutionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    );
    var hostPassed = validation.ObjectiveAchieved && workspaceAccuracy == 100;
    var status = executionCompleted
      ? hostPassed ? BenchmarkResultStatusIds.Pass : BenchmarkResultStatusIds.Fail
      : BenchmarkResultStatusIds.Error;
    var evidence = context.HarnessEvidence;
    var unexpected = unexpectedCreated
      .Concat(unexpectedModified)
      .Concat(unexpectedDeleted)
      .Concat(missingExpectedChange.Select(path => $"missing:{path}"))
      .Distinct(BenchmarkWorkspaceFactory.PathComparer)
      .OrderBy(path => path, BenchmarkWorkspaceFactory.PathComparer)
      .ToArray();

    return new BenchmarkRawResult(
      status,
      validation.ObjectiveAchieved,
      validation.ByteAccuracy,
      validation.DirectoryAccuracy,
      validation.FilenameAccuracy,
      workspaceAccuracy,
      unexpectedCreated,
      unexpectedModified,
      unexpectedDeleted,
      context.ExecutionStatus,
      context.ExecutionError,
      evidence?.InputTokens,
      evidence?.OutputTokens,
      validation.Exactness,
      validation.UsefulPartialOutcome,
      evidence?.ToolCallCount,
      evidence?.SurfacedErrorCount,
      evidence?.RecoveredErrorCount,
      changes.All,
      unexpected,
      hostPassed ? "pass" : "fail",
      evidence?.FinalReport ?? string.Empty,
      validation.Facts
    );
  }

  protected abstract Task<BenchmarkObjectiveValidation> ValidateObjectiveAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  );

  protected static async Task<byte[]?> ReadBytesAsync(
    string workspacePath,
    string relativePath,
    CancellationToken cancellationToken
  )
  {
    var path = Path.Combine(
      workspacePath,
      relativePath.Replace('/', Path.DirectorySeparatorChar)
    );
    return File.Exists(path)
      ? await File.ReadAllBytesAsync(path, cancellationToken)
      : null;
  }

  protected static string Sha256(byte[] bytes)
  {
    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
  }

  protected static bool PathExists(
    string workspacePath,
    string relativePath
  )
  {
    var path = Path.Combine(
      workspacePath,
      relativePath.Replace('/', Path.DirectorySeparatorChar)
    );
    return File.Exists(path) || Directory.Exists(path);
  }

  private static BenchmarkChangeSet CalculateChanges(
    BenchmarkWorkspaceSnapshot initial,
    BenchmarkWorkspaceSnapshot final
  )
  {
    var comparer = BenchmarkWorkspaceFactory.PathComparer;
    var created = final.Entries.Keys
      .Where(path => !initial.Entries.ContainsKey(path))
      .OrderBy(path => path, comparer)
      .ToArray();
    var modified = final.Entries
      .Where(pair => initial.Entries.TryGetValue(pair.Key, out var before)
        && (
          !string.Equals(before.Kind, pair.Value.Kind, StringComparison.Ordinal)
          || !string.Equals(before.ContentHash, pair.Value.ContentHash, StringComparison.Ordinal)
        ))
      .Select(pair => pair.Key)
      .OrderBy(path => path, comparer)
      .ToArray();
    var deleted = initial.Entries.Keys
      .Where(path => !final.Entries.ContainsKey(path))
      .OrderBy(path => path, comparer)
      .ToArray();
    return new BenchmarkChangeSet(created, modified, deleted);
  }

  private static IReadOnlyList<string> Except(
    IReadOnlyList<string> actual,
    IReadOnlySet<string> expected
  )
  {
    return actual.Except(expected, BenchmarkWorkspaceFactory.PathComparer)
      .OrderBy(path => path, BenchmarkWorkspaceFactory.PathComparer)
      .ToArray();
  }
}

public sealed record BenchmarkObjectiveValidation(
  bool ObjectiveAchieved,
  int Exactness,
  bool UsefulPartialOutcome,
  int ByteAccuracy = 0,
  int DirectoryAccuracy = 0,
  int FilenameAccuracy = 0,
  IReadOnlyDictionary<string, string>? Facts = null
);

public sealed class FileSystemCreateBenchmark : BasicCrudBenchmark
{
  private const string ExpectedPath = "benchmark-data/result.txt";
  private const string ExpectedContent = "Agentic Router Benchmark\noperation=create\nresult=success";
  private static readonly IReadOnlySet<string> Created = Set(ExpectedPath);
  private static readonly IReadOnlySet<string> None = Set();

  public override BenchmarkTestMetadata Metadata { get; } = new(
    BenchmarkIds.FileSystemCreate001,
    1,
    "Create one exact UTF-8 file",
    BenchmarkSuiteIds.BasicCrud,
    "Create benchmark-data/result.txt with canonical bytes and no unrelated workspace changes.",
    true,
    [BenchmarkHarnessCapabilityIds.FileCreation],
    AcceptanceVersion: 1,
    Order: 1
  );

  protected override IReadOnlySet<string> ExpectedCreated => Created;

  protected override IReadOnlySet<string> ExpectedModified => None;

  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override string CreateTask()
  {
    return "Benchmark test: FS-CREATE-001 (version 1; acceptance 1).\n"
      + "Inside the provided workspace, create exactly benchmark-data/result.txt "
      + "with the following UTF-8 text and no byte-order mark:\n\n"
      + ExpectedContent
      + "\n\nThe canonical file ends immediately after 'success'; do not append a trailing newline. "
      + "Do not modify or delete any existing file and do not create any other file.";
  }

  protected override async Task<BenchmarkObjectiveValidation> ValidateObjectiveAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    var directory = Directory.Exists(Path.Combine(context.WorkspacePath, "benchmark-data"));
    var filename = File.Exists(Path.Combine(context.WorkspacePath, "benchmark-data", "result.txt"));
    var actualBytes = await ReadBytesAsync(
      context.WorkspacePath,
      ExpectedPath,
      cancellationToken
    );
    var expectedBytes = new UTF8Encoding(false).GetBytes(ExpectedContent);
    var length = actualBytes?.Length == expectedBytes.Length;
    var bytes = actualBytes is not null
      && actualBytes.AsSpan().SequenceEqual(expectedBytes);
    var exactness = (directory ? 20 : 0)
      + (filename ? 20 : 0)
      + (length ? 20 : 0)
      + (bytes ? 40 : 0);
    return new BenchmarkObjectiveValidation(
      directory && filename && bytes,
      exactness,
      exactness > 0,
      bytes ? 100 : 0,
      directory ? 100 : 0,
      filename ? 100 : 0,
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["expectedPath"] = ExpectedPath,
        ["expectedByteLength"] = expectedBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["actualByteLength"] = actualBytes?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing",
        ["expectedSha256"] = Sha256(expectedBytes),
        ["actualSha256"] = actualBytes is null ? "missing" : Sha256(actualBytes)
      }
    );
  }

  private static IReadOnlySet<string> Set(params string[] paths)
  {
    return new HashSet<string>(paths, BenchmarkWorkspaceFactory.PathComparer);
  }
}

public sealed class FileSystemReadBenchmark : BasicCrudBenchmark
{
  private static readonly IReadOnlySet<string> None = new HashSet<string>(
    BenchmarkWorkspaceFactory.PathComparer
  );

  public override BenchmarkTestMetadata Metadata { get; } = new(
    BenchmarkIds.FileSystemRead001,
    1,
    "Read and report two known facts",
    BenchmarkSuiteIds.BasicCrud,
    "Read two canonical fixture files and report their deterministic facts without mutation.",
    true,
    [BenchmarkHarnessCapabilityIds.FileReading],
    AcceptanceVersion: 1,
    Order: 2
  );

  protected override IReadOnlySet<string> ExpectedCreated => None;

  protected override IReadOnlySet<string> ExpectedModified => None;

  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override string CreateTask()
  {
    return "Benchmark test: FS-READ-001 (version 1; acceptance 1).\n"
      + "Read fixture/read-primary.txt and fixture/read-secondary.txt. In the final answer, "
      + "report the exact codename and exact verification-word as two separate key=value facts. "
      + "Do not create, modify, or delete any file.";
  }

  protected override Task<BenchmarkObjectiveValidation> ValidateObjectiveAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    _ = cancellationToken;
    var report = context.HarnessEvidence?.FinalReport ?? string.Empty;
    var codename = report.Contains("codename=ORBIT-41", StringComparison.Ordinal);
    var word = report.Contains("verification-word=marigold", StringComparison.Ordinal);
    var exactness = (codename ? 50 : 0) + (word ? 50 : 0);
    return Task.FromResult(
      new BenchmarkObjectiveValidation(
        codename && word,
        exactness,
        exactness > 0,
        Facts: new Dictionary<string, string>(StringComparer.Ordinal)
        {
          ["codenameExpected"] = "ORBIT-41",
          ["codenameMatched"] = codename.ToString(),
          ["verificationWordExpected"] = "marigold",
          ["verificationWordMatched"] = word.ToString()
        }
      )
    );
  }
}

public sealed class FileSystemUpdateBenchmark : BasicCrudBenchmark
{
  private const string ExpectedPath = "fixture/update.txt";
  private static readonly IReadOnlySet<string> Modified = new HashSet<string>(
    [ExpectedPath],
    BenchmarkWorkspaceFactory.PathComparer
  );
  private static readonly IReadOnlySet<string> None = new HashSet<string>(
    BenchmarkWorkspaceFactory.PathComparer
  );

  public override BenchmarkTestMetadata Metadata { get; } = new(
    BenchmarkIds.FileSystemUpdate001,
    1,
    "Update one exact value",
    BenchmarkSuiteIds.BasicCrud,
    "Change retries=2 to retries=3 in one existing file while preserving all other bytes.",
    true,
    [BenchmarkHarnessCapabilityIds.FileUpdate],
    AcceptanceVersion: 1,
    Order: 3
  );

  protected override IReadOnlySet<string> ExpectedCreated => None;

  protected override IReadOnlySet<string> ExpectedModified => Modified;

  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override string CreateTask()
  {
    return "Benchmark test: FS-UPDATE-001 (version 1; acceptance 1).\n"
      + "In fixture/update.txt, replace exactly the single line retries=2 with retries=3. "
      + "Preserve every other byte, do not recreate or duplicate the file, and do not change any other file.";
  }

  protected override async Task<BenchmarkObjectiveValidation> ValidateObjectiveAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    var actualBytes = await ReadBytesAsync(
      context.WorkspacePath,
      ExpectedPath,
      cancellationToken
    );
    var expectedBytes = new UTF8Encoding(false).GetBytes(
      BasicCrudFixture.UpdateExpectedContent
    );
    var bytes = actualBytes is not null
      && actualBytes.AsSpan().SequenceEqual(expectedBytes);
    var singleMutation = changes.Modified.Count == 1
      && BenchmarkWorkspaceFactory.PathComparer.Equals(changes.Modified[0], ExpectedPath)
      && changes.Created.Count == 0
      && changes.Deleted.Count == 0;
    var exactness = (bytes ? 75 : 0) + (singleMutation ? 25 : 0);
    return new BenchmarkObjectiveValidation(
      bytes && singleMutation,
      exactness,
      exactness > 0,
      bytes ? 100 : 0,
      100,
      PathExists(context.WorkspacePath, ExpectedPath) ? 100 : 0,
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["expectedPath"] = ExpectedPath,
        ["expectedSha256"] = Sha256(expectedBytes),
        ["actualSha256"] = actualBytes is null ? "missing" : Sha256(actualBytes),
        ["expectedChangedFiles"] = "1",
        ["actualChangedFiles"] = changes.All.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
      }
    );
  }
}

public sealed class FileSystemDeleteBenchmark : BasicCrudBenchmark
{
  private const string ExpectedPath = "fixture/delete.txt";
  private static readonly IReadOnlySet<string> Deleted = new HashSet<string>(
    [ExpectedPath],
    BenchmarkWorkspaceFactory.PathComparer
  );
  private static readonly IReadOnlySet<string> None = new HashSet<string>(
    BenchmarkWorkspaceFactory.PathComparer
  );

  public override BenchmarkTestMetadata Metadata { get; } = new(
    BenchmarkIds.FileSystemDelete001,
    1,
    "Delete one exact file",
    BenchmarkSuiteIds.BasicCrud,
    "Delete fixture/delete.txt through a structured capability and preserve every unrelated file.",
    true,
    [BenchmarkHarnessCapabilityIds.FileDeletion],
    AcceptanceVersion: 1,
    Order: 4
  );

  protected override IReadOnlySet<string> ExpectedCreated => None;

  protected override IReadOnlySet<string> ExpectedModified => None;

  protected override IReadOnlySet<string> ExpectedDeleted => Deleted;

  public override string CreateTask()
  {
    return "Benchmark test: FS-DELETE-001 (version 1; acceptance 1).\n"
      + "Delete exactly fixture/delete.txt using an available structured filesystem capability. "
      + "Do not use a shell. Preserve every unrelated file and do not create a placeholder or replacement.";
  }

  protected override Task<BenchmarkObjectiveValidation> ValidateObjectiveAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = cancellationToken;
    var absent = !PathExists(context.WorkspacePath, ExpectedPath);
    var singleDeletion = changes.Deleted.Count == 1
      && BenchmarkWorkspaceFactory.PathComparer.Equals(changes.Deleted[0], ExpectedPath)
      && changes.Created.Count == 0
      && changes.Modified.Count == 0;
    var exactness = (absent ? 75 : 0) + (singleDeletion ? 25 : 0);
    return Task.FromResult(
      new BenchmarkObjectiveValidation(
        absent && singleDeletion,
        exactness,
        exactness > 0,
        absent ? 100 : 0,
        100,
        absent ? 100 : 0,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
          ["expectedAbsentPath"] = ExpectedPath,
          ["targetAbsent"] = absent.ToString(),
          ["actualDeletedFiles"] = changes.Deleted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }
      )
    );
  }
}
