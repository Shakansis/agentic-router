using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Recovery;

public sealed class SafeModeSettingsStore : ISettingsStore
{
  private readonly JsonSettingsStore _inner;
  private readonly SafeModeState _safeMode;

  public SafeModeSettingsStore(
    JsonSettingsStore inner,
    SafeModeState safeMode
  )
  {
    _inner = inner;
    _safeMode = safeMode;
  }

  public async Task<ApplicationSettings> GetAsync(
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await _inner.GetAsync(
        cancellationToken
      );
    }
    catch (Exception exception) when (
      _safeMode.Enabled
      && exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or JsonException
    )
    {
      return SettingsDefaults.Create();
    }
  }

  public Task<SettingsSaveResult> SaveAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    if (_safeMode.Enabled)
    {
      return Task.FromResult(
        new SettingsSaveResult(
          false,
          null,
          new Dictionary<string, string[]>
          {
            ["safeMode"] =
            [
              "Settings are read-only while safe mode is active."
            ]
          }
        )
      );
    }

    return _inner.SaveAsync(
      settings,
      cancellationToken
    );
  }
}

public sealed class SafeModeState
{
  private readonly object _gate = new();
  private bool _enabled;
  private string? _reason;

  public SafeModeState(
    bool enabled,
    string? reason
  )
  {
    _enabled = enabled;
    _reason = reason;
  }

  public bool Enabled
  {
    get
    {
      lock (_gate)
      {
        return _enabled;
      }
    }
  }

  public string? Reason
  {
    get
    {
      lock (_gate)
      {
        return _reason;
      }
    }
  }

  public void Activate(
    string reason
  )
  {
    lock (_gate)
    {
      _enabled = true;
      _reason = reason;
    }
  }
}

public sealed record RecoveryStatus(
  bool SafeMode,
  string? Reason,
  bool SettingsReadOnly,
  bool ExecuteDisabled,
  bool CloudDisabled,
  bool HistoryAutoLoadDisabled
);

public sealed record LocalBackupOptions(
  bool IncludeConversations = false,
  bool IncludeSessionSummaries = false,
  bool IncludeUsageHistory = false,
  bool IncludeReviewData = false
);

public sealed record BackupManifestEntry(
  string Path,
  string Category,
  long Bytes,
  string Sha256
);

public sealed record BackupManifest(
  int SchemaVersion,
  string ApplicationVersion,
  DateTimeOffset CreatedAt,
  LocalBackupOptions Options,
  IReadOnlyList<string> Categories,
  IReadOnlyList<BackupManifestEntry> Entries
);

public sealed record InspectBackupRequest(
  string ArchiveBase64
);

public sealed record RestoreBackupRequest(
  string ArchiveBase64,
  IReadOnlyList<string> Categories,
  bool Confirmed
);

public sealed record BackupInspection(
  BackupManifest Manifest,
  IReadOnlyList<string> Conflicts,
  bool HashesValid
);

public sealed record RestoreBackupResult(
  IReadOnlyList<string> RestoredCategories,
  string CurrentDataBackup,
  bool RolledBack
);

public interface ILocalBackupService
{
  Task<byte[]> CreateAsync(
    LocalBackupOptions options,
    CancellationToken cancellationToken
  );

  Task<BackupInspection> InspectAsync(
    byte[] archive,
    CancellationToken cancellationToken
  );

  Task<RestoreBackupResult> RestoreAsync(
    byte[] archive,
    IReadOnlyList<string> categories,
    CancellationToken cancellationToken
  );
}

public sealed class LocalBackupService : ILocalBackupService
{
  private const int MaximumArchiveBytes = 100 * 1024 * 1024;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };
  private static readonly HashSet<string> BaseFiles = new(
    StringComparer.OrdinalIgnoreCase
  )
  {
    "settings.json",
    "workspaces.json",
    "model-organization.json"
  };
  private readonly string _dataDirectory;
  private readonly IPricingCatalog _pricing;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public LocalBackupService(
    string dataDirectory,
    IPricingCatalog pricing
  )
  {
    _dataDirectory = dataDirectory;
    _pricing = pricing;
  }

  public async Task<byte[]> CreateAsync(
    LocalBackupOptions options,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var files = await CollectAsync(
        options,
        cancellationToken
      );
      var entries = files.Select(
        file => new BackupManifestEntry(
          file.Path,
          file.Category,
          file.Content.LongLength,
          Convert.ToHexString(
            SHA256.HashData(
              file.Content
            )
          ).ToLowerInvariant()
        )
      ).ToArray();
      var manifest = new BackupManifest(
        1,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(
          3
        ) ?? "unknown",
        DateTimeOffset.UtcNow,
        options,
        entries.Select(
          entry => entry.Category
        ).Distinct(
          StringComparer.Ordinal
        ).Order(
          StringComparer.Ordinal
        ).ToArray(),
        entries
      );
      using var output = new MemoryStream();

      using (
        var archive = new ZipArchive(
          output,
          ZipArchiveMode.Create,
          true
        )
      )
      {
        WriteEntry(
          archive,
          "manifest.json",
          Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
              manifest,
              JsonOptions
            ) + "\n"
          )
        );

        foreach (var file in files)
        {
          cancellationToken.ThrowIfCancellationRequested();
          WriteEntry(
            archive,
            $"data/{file.Path}",
            file.Content
          );
        }
      }

      return output.ToArray();
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<BackupInspection> InspectAsync(
    byte[] archive,
    CancellationToken cancellationToken
  )
  {
    var validated = await ValidateArchiveAsync(
      archive,
      cancellationToken
    );
    var conflicts = validated.Manifest.Entries.Where(
      entry => File.Exists(
        ResolveDataPath(
          entry.Path
        )
      )
    ).Select(
      entry => entry.Path
    ).ToArray();
    return new BackupInspection(
      validated.Manifest,
      conflicts,
      true
    );
  }

  public async Task<RestoreBackupResult> RestoreAsync(
    byte[] archive,
    IReadOnlyList<string> categories,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var validated = await ValidateArchiveAsync(
        archive,
        cancellationToken
      );
      var allowed = categories.ToHashSet(
        StringComparer.Ordinal
      );
      var selected = validated.Files.Where(
        file => allowed.Contains(
          file.Category
        )
      ).ToArray();

      if (
        selected.Length == 0
        || allowed.Any(
          category => !validated.Manifest.Categories.Contains(
            category,
            StringComparer.Ordinal
          )
        )
      )
      {
        throw new InvalidDataException(
          "The selected restore categories are invalid."
        );
      }

      var backupDirectory = Path.Combine(
        _dataDirectory,
        "restore-backups"
      );
      Directory.CreateDirectory(
        backupDirectory
      );
      var backupPath = Path.Combine(
        backupDirectory,
        $"before-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.zip"
      );
      var current = new Dictionary<string, byte[]?>(
        StringComparer.Ordinal
      );

      foreach (var file in selected)
      {
        var target = ResolveDataPath(
          file.Path
        );
        current[file.Path] = File.Exists(
          target
        )
          ? await File.ReadAllBytesAsync(
            target,
            cancellationToken
          )
          : null;
      }

      await WriteCurrentBackupAsync(
        backupPath,
        current,
        cancellationToken
      );

      try
      {
        foreach (var file in selected)
        {
          cancellationToken.ThrowIfCancellationRequested();
          await WriteAtomicAsync(
            ResolveDataPath(
              file.Path
            ),
            file.Content,
            cancellationToken
          );
        }
      }
      catch
      {
        foreach (var item in current)
        {
          var target = ResolveDataPath(
            item.Key
          );

          if (item.Value is null)
          {
            if (File.Exists(
              target
            ))
            {
              File.Delete(
                target
              );
            }
          }
          else
          {
            await WriteAtomicAsync(
              target,
              item.Value,
              CancellationToken.None
            );
          }
        }

        throw;
      }

      return new RestoreBackupResult(
        selected.Select(
          file => file.Category
        ).Distinct(
          StringComparer.Ordinal
        ).Order(
          StringComparer.Ordinal
        ).ToArray(),
        Path.GetRelativePath(
          _dataDirectory,
          backupPath
        ).Replace(
          '\\',
          '/'
        ),
        false
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task<IReadOnlyList<BackupFile>> CollectAsync(
    LocalBackupOptions options,
    CancellationToken cancellationToken
  )
  {
    var files = new List<BackupFile>();
    files.Add(
      new BackupFile(
        "catalog/pricing-catalog.json",
        "pricing-catalog",
        Encoding.UTF8.GetBytes(
          JsonSerializer.Serialize(
            _pricing.Get(),
            JsonOptions
          ) + "\n"
        )
      )
    );

    foreach (var name in BaseFiles.Order(
      StringComparer.Ordinal
    ))
    {
      var path = Path.Combine(
        _dataDirectory,
        name
      );

      if (File.Exists(
        path
      ))
      {
        var category = name switch
        {
          "settings.json" => "settings",
          "workspaces.json" => "workspaces",
          _ => "model-organization"
        };
        files.Add(
          new BackupFile(
            name,
            category,
            SanitizeJson(
              await File.ReadAllBytesAsync(
                path,
                cancellationToken
              ),
              false,
              false
            )
          )
        );
      }
    }

    if (options.IncludeConversations)
    {
      foreach (var path in Directory.Exists(
        Path.Combine(
          _dataDirectory,
          "workspaces"
        )
      )
        ? Directory.EnumerateFiles(
          Path.Combine(
            _dataDirectory,
            "workspaces"
          ),
          "*.json",
          SearchOption.AllDirectories
        )
        : [])
      {
        cancellationToken.ThrowIfCancellationRequested();
        files.Add(
          new BackupFile(
            Path.GetRelativePath(
              _dataDirectory,
              path
            ).Replace(
              '\\',
              '/'
            ),
            "conversations",
            SanitizeJson(
              await File.ReadAllBytesAsync(
                path,
                cancellationToken
              ),
              options.IncludeSessionSummaries,
              options.IncludeReviewData
            )
          )
        );
      }
    }

    if (options.IncludeUsageHistory)
    {
      var usage = Path.Combine(
        _dataDirectory,
        "usage"
      );

      if (Directory.Exists(
        usage
      ))
      {
        foreach (var path in Directory.EnumerateFiles(
          usage,
          "*.jsonl",
          SearchOption.TopDirectoryOnly
        ))
        {
          files.Add(
            new BackupFile(
              Path.GetRelativePath(
                _dataDirectory,
                path
              ).Replace(
                '\\',
                '/'
              ),
              "usage",
              await File.ReadAllBytesAsync(
                path,
                cancellationToken
              )
            )
          );
        }
      }
    }

    return files;
  }

  private static byte[] SanitizeJson(
    byte[] content,
    bool includeSummary,
    bool includeReview
  )
  {
    var node = JsonNode.Parse(
      content
    ) ?? throw new InvalidDataException(
      "A backup source JSON file is invalid."
    );
    SanitizeNode(
      node,
      includeSummary,
      includeReview
    );
    return Encoding.UTF8.GetBytes(
      node.ToJsonString(
        JsonOptions
      ) + "\n"
    );
  }

  private static void SanitizeNode(
    JsonNode node,
    bool includeSummary,
    bool includeReview
  )
  {
    if (node is JsonObject value)
    {
      foreach (var property in value.ToArray())
      {
        var name = property.Key;

        if (
          name.Contains(
            "secret",
            StringComparison.OrdinalIgnoreCase
          )
          || name.Contains(
            "apiKey",
            StringComparison.OrdinalIgnoreCase
          )
          || name.Contains(
            "approval",
            StringComparison.OrdinalIgnoreCase
          )
          || name.Contains(
            "processId",
            StringComparison.OrdinalIgnoreCase
          )
          || name.Contains(
            "executionRollbacks",
            StringComparison.OrdinalIgnoreCase
          )
          || !includeSummary
          && name.Equals(
            "sessionSummary",
            StringComparison.OrdinalIgnoreCase
          )
          || !includeReview
          && name.Equals(
            "executionReviews",
            StringComparison.OrdinalIgnoreCase
          )
        )
        {
          value.Remove(
            name
          );
          continue;
        }

        if (property.Value is not null)
        {
          SanitizeNode(
            property.Value,
            includeSummary,
            includeReview
          );
        }
      }
    }
    else if (node is JsonArray array)
    {
      foreach (var item in array)
      {
        if (item is not null)
        {
          SanitizeNode(
            item,
            includeSummary,
            includeReview
          );
        }
      }
    }
  }

  private async Task<ValidatedBackup> ValidateArchiveAsync(
    byte[] content,
    CancellationToken cancellationToken
  )
  {
    if (content.Length is < 1 or > MaximumArchiveBytes)
    {
      throw new InvalidDataException(
        "The backup archive size is invalid."
      );
    }

    using var stream = new MemoryStream(
      content,
      false
    );
    using var archive = new ZipArchive(
      stream,
      ZipArchiveMode.Read
    );
    var manifestEntry = archive.GetEntry(
      "manifest.json"
    ) ?? throw new InvalidDataException(
      "The backup manifest is missing."
    );
    BackupManifest manifest;

    await using (
      var manifestStream = manifestEntry.Open()
    )
    {
      manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
        manifestStream,
        JsonOptions,
        cancellationToken
      ) ?? throw new InvalidDataException(
        "The backup manifest is empty."
      );
    }

    if (
      manifest.SchemaVersion != 1
      || manifest.Entries.Count > 10_000
      || manifest.Entries.Any(
        entry => entry.Bytes is < 0 or > 52_428_800
      )
      || manifest.Entries.Sum(
        entry => entry.Bytes
      ) > 262_144_000
      || manifest.Entries.Select(
        entry => entry.Path
      ).Distinct(
        StringComparer.Ordinal
      ).Count() != manifest.Entries.Count
    )
    {
      throw new InvalidDataException(
        "The backup manifest is invalid or unsupported."
      );
    }

    var files = new List<BackupFile>();

    foreach (var expected in manifest.Entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _ = ResolveDataPath(
        expected.Path
      );
      var entry = archive.GetEntry(
        $"data/{expected.Path}"
      ) ?? throw new InvalidDataException(
        $"Backup entry '{expected.Path}' is missing."
      );

      if (entry.Length != expected.Bytes)
      {
        throw new InvalidDataException(
          $"Backup entry '{expected.Path}' has an invalid declared size."
        );
      }
      await using var input = entry.Open();
      using var buffer = new MemoryStream();
      await input.CopyToAsync(
        buffer,
        cancellationToken
      );
      var bytes = buffer.ToArray();
      var hash = Convert.ToHexString(
        SHA256.HashData(
          bytes
        )
      ).ToLowerInvariant();

      if (
        bytes.LongLength != expected.Bytes
        || !string.Equals(
          hash,
          expected.Sha256,
          StringComparison.Ordinal
        )
      )
      {
        throw new InvalidDataException(
          $"Backup entry '{expected.Path}' failed hash validation."
        );
      }

      files.Add(
        new BackupFile(
          expected.Path,
          expected.Category,
          bytes
        )
      );
    }

    return new ValidatedBackup(
      manifest,
      files
    );
  }

  private string ResolveDataPath(
    string relative
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        relative
      )
      || Path.IsPathRooted(
        relative
      )
    )
    {
      throw new InvalidDataException(
        "A backup entry path is invalid."
      );
    }

    var root = Path.GetFullPath(
      _dataDirectory
    );
    var path = Path.GetFullPath(
      Path.Combine(
        root,
        relative.Replace(
          '/',
          Path.DirectorySeparatorChar
        )
      )
    );

    if (!path.StartsWith(
      root + Path.DirectorySeparatorChar,
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw new InvalidDataException(
        "A backup entry escapes the application data directory."
      );
    }

    return path;
  }

  private static async Task WriteCurrentBackupAsync(
    string path,
    IReadOnlyDictionary<string, byte[]?> current,
    CancellationToken cancellationToken
  )
  {
    await using var output = File.Create(
      path
    );
    using var archive = new ZipArchive(
      output,
      ZipArchiveMode.Create
    );

    foreach (var item in current.Where(
      item => item.Value is not null
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      WriteEntry(
        archive,
        item.Key,
        item.Value!
      );
    }
  }

  private static async Task WriteAtomicAsync(
    string path,
    byte[] content,
    CancellationToken cancellationToken
  )
  {
    var directory = Path.GetDirectoryName(
      path
    )!;
    Directory.CreateDirectory(
      directory
    );
    var temporary = Path.Combine(
      directory,
      $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      await File.WriteAllBytesAsync(
        temporary,
        content,
        cancellationToken
      );
      File.Move(
        temporary,
        path,
        true
      );
    }
    finally
    {
      if (File.Exists(
        temporary
      ))
      {
        File.Delete(
          temporary
        );
      }
    }
  }

  private static void WriteEntry(
    ZipArchive archive,
    string path,
    byte[] content
  )
  {
    var entry = archive.CreateEntry(
      path,
      CompressionLevel.SmallestSize
    );
    entry.LastWriteTime = new DateTimeOffset(
      1980,
      1,
      1,
      0,
      0,
      0,
      TimeSpan.Zero
    );
    using var stream = entry.Open();
    stream.Write(
      content
    );
  }

  private sealed record BackupFile(
    string Path,
    string Category,
    byte[] Content
  );

  private sealed record ValidatedBackup(
    BackupManifest Manifest,
    IReadOnlyList<BackupFile> Files
  );
}
