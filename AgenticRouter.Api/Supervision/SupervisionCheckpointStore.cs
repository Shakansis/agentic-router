using System.Text;
using System.Text.Json;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Supervision;

public interface ISupervisionCheckpointStore
{
  Task<SupervisionCheckpointLoadResult> ReadAllAsync(
    CancellationToken cancellationToken
  );

  Task<DurableSupervisionCheckpoint?> ReadAsync(
    string workspaceId,
    string conversationSessionId,
    string runId,
    CancellationToken cancellationToken
  );

  Task<DurableSupervisionCheckpoint> WriteAsync(
    DurableSupervisionCheckpoint checkpoint,
    long? expectedRevision,
    CancellationToken cancellationToken
  );

  Task DeleteAsync(
    string workspaceId,
    string conversationSessionId,
    string runId,
    CancellationToken cancellationToken
  );

  Task DeleteConversationAsync(
    string workspaceId,
    string conversationSessionId,
    CancellationToken cancellationToken
  );
}

public sealed class SupervisionCheckpointStore : ISupervisionCheckpointStore
{
  private const int MaximumCheckpointBytes = 1_048_576;
  private const int MaximumEvents = 256;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };
  private static readonly JsonSerializerOptions IntegrityOptions = new(
    JsonSerializerDefaults.Web
  );

  private readonly string _dataDirectory;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public SupervisionCheckpointStore(
    IWorkspaceProfileStore workspaceProfiles
  )
  {
    _dataDirectory = workspaceProfiles.DataDirectory;
  }

  public async Task<SupervisionCheckpointLoadResult> ReadAllAsync(
    CancellationToken cancellationToken
  )
  {
    var root = Path.Combine(
      _dataDirectory,
      "workspaces"
    );

    if (!Directory.Exists(
      root
    ))
    {
      return new SupervisionCheckpointLoadResult(
        [],
        []
      );
    }

    var checkpoints = new List<DurableSupervisionCheckpoint>();
    var issues = new List<SupervisionCheckpointLoadIssue>();

    foreach (var path in EnumerateCheckpointPaths(
      root
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();

      try
      {
        var checkpoint = await ReadPathAsync(
          path,
          cancellationToken
        );
        checkpoints.Add(
          checkpoint
        );
      }
      catch (SupervisionException exception)
      {
        issues.Add(
          new SupervisionCheckpointLoadIssue(
            Path.GetRelativePath(
              _dataDirectory,
              path
            ).Replace(
              '\\',
              '/'
            ),
            exception.Code,
            exception.Message
          )
        );
      }
    }

    return new SupervisionCheckpointLoadResult(
      checkpoints,
      issues
    );
  }

  public async Task<DurableSupervisionCheckpoint?> ReadAsync(
    string workspaceId,
    string conversationSessionId,
    string runId,
    CancellationToken cancellationToken
  )
  {
    ValidateIdentities(
      workspaceId,
      conversationSessionId,
      runId
    );
    var path = CheckpointPath(
      workspaceId,
      conversationSessionId,
      runId
    );

    return File.Exists(
      path
    )
      ? await ReadPathAsync(
        path,
        cancellationToken
      )
      : null;
  }

  public async Task<DurableSupervisionCheckpoint> WriteAsync(
    DurableSupervisionCheckpoint checkpoint,
    long? expectedRevision,
    CancellationToken cancellationToken
  )
  {
    ValidateCheckpoint(
      checkpoint
    );
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var path = CheckpointPath(
        checkpoint.WorkspaceId,
        checkpoint.ConversationSessionId,
        checkpoint.RunId
      );
      var existing = File.Exists(
        path
      )
        ? await ReadPathAsync(
          path,
          cancellationToken
        )
        : null;

      if (
        expectedRevision.HasValue
        && existing?.Revision != expectedRevision.Value
      )
      {
        throw new SupervisionException(
          "supervision-checkpoint-conflict",
          "supervision-checkpoint",
          "The supervision checkpoint changed before this update could be saved.",
          true,
          409
        );
      }

      if (
        existing is not null
        && checkpoint.Revision <= existing.Revision
      )
      {
        throw new SupervisionException(
          "supervision-checkpoint-revision-invalid",
          "supervision-checkpoint",
          "The supervision checkpoint revision must increase monotonically.",
          false,
          409
        );
      }

      var bounded = checkpoint with
      {
        Events = checkpoint.Events.TakeLast(
          MaximumEvents
        ).ToArray(),
        IntegritySha256 = string.Empty
      };
      bounded = bounded with
      {
        IntegritySha256 = ComputeIntegrity(
          bounded
        )
      };
      var json = JsonSerializer.Serialize(
        bounded,
        JsonOptions
      ).Replace(
        "\r\n",
        "\n",
        StringComparison.Ordinal
      ) + "\n";

      if (Encoding.UTF8.GetByteCount(
        json
      ) > MaximumCheckpointBytes)
      {
        throw new SupervisionException(
          "supervision-checkpoint-too-large",
          "supervision-checkpoint",
          "The supervision checkpoint exceeds the local durability limit.",
          false,
          413
        );
      }

      var directory = Path.GetDirectoryName(
        path
      )!;
      Directory.CreateDirectory(
        directory
      );
      var temporary = Path.Combine(
        directory,
        $".{checkpoint.RunId}-{Guid.NewGuid():N}.tmp"
      );

      try
      {
        await File.WriteAllTextAsync(
          temporary,
          json,
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

      return bounded;
    }
    catch (SupervisionException)
    {
      throw;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
      or InvalidDataException
    )
    {
      throw new SupervisionException(
        "supervision-checkpoint-failed",
        "supervision-checkpoint",
        "The supervision checkpoint could not be saved.",
        true,
        500,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task DeleteAsync(
    string workspaceId,
    string conversationSessionId,
    string runId,
    CancellationToken cancellationToken
  )
  {
    ValidateIdentities(
      workspaceId,
      conversationSessionId,
      runId
    );
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var path = CheckpointPath(
        workspaceId,
        conversationSessionId,
        runId
      );
      if (File.Exists(
        path
      ))
      {
        File.Delete(
          path
        );
      }
      DeleteEmptyParents(
        Path.GetDirectoryName(
          path
        )!,
        workspaceId
      );
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new SupervisionException(
        "supervision-checkpoint-delete-failed",
        "supervision-checkpoint",
        "The supervision checkpoint could not be deleted.",
        true,
        500,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task DeleteConversationAsync(
    string workspaceId,
    string conversationSessionId,
    CancellationToken cancellationToken
  )
  {
    SupervisionRequestPolicy.ValidateId(
      workspaceId,
      "workspace"
    );
    SupervisionRequestPolicy.ValidateId(
      conversationSessionId,
      "conversation session"
    );
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var directory = ConversationDirectory(
        workspaceId,
        conversationSessionId
      );
      if (Directory.Exists(
        directory
      ))
      {
        RejectReparsePoint(
          directory
        );
        Directory.Delete(
          directory,
          true
        );
      }
      DeleteEmptyParents(
        Path.GetDirectoryName(
          directory
        )!,
        workspaceId
      );
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new SupervisionException(
        "supervision-checkpoint-delete-failed",
        "supervision-checkpoint",
        "The conversation's supervision checkpoints could not be deleted.",
        true,
        500,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task<DurableSupervisionCheckpoint> ReadPathAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var info = new FileInfo(
        path
      );
      if (info.Length > MaximumCheckpointBytes)
      {
        throw InvalidCheckpoint(
          "The supervision checkpoint exceeds the local durability limit."
        );
      }

      await using var stream = File.OpenRead(
        path
      );
      var checkpoint = await JsonSerializer.DeserializeAsync<DurableSupervisionCheckpoint>(
        stream,
        JsonOptions,
        cancellationToken
      ) ?? throw InvalidCheckpoint(
        "The supervision checkpoint is empty."
      );
      ValidateCheckpoint(
        checkpoint
      );
      var expected = ComputeIntegrity(
        checkpoint with
        {
          IntegritySha256 = string.Empty
        }
      );
      if (!string.Equals(
        expected,
        checkpoint.IntegritySha256,
        StringComparison.Ordinal
      ))
      {
        throw InvalidCheckpoint(
          "The supervision checkpoint integrity hash is invalid."
        );
      }

      return checkpoint;
    }
    catch (SupervisionException)
    {
      throw;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
      or InvalidDataException
    )
    {
      throw new SupervisionException(
        "supervision-checkpoint-invalid",
        "supervision-checkpoint",
        "The supervision checkpoint is invalid or inaccessible.",
        false,
        500,
        exception
      );
    }
  }

  private static void ValidateCheckpoint(
    DurableSupervisionCheckpoint checkpoint
  )
  {
    ValidateIdentities(
      checkpoint.WorkspaceId,
      checkpoint.ConversationSessionId,
      checkpoint.RunId
    );

    if (
      checkpoint.SchemaVersion != DurableSupervisionCheckpoint.CurrentSchemaVersion
      || checkpoint.Revision < 1
      || checkpoint.Objective.Length is < 1 or > 16_384
      || !string.Equals(
        checkpoint.ObjectiveSha256,
        SupervisionRequestPolicy.Hash(
          checkpoint.Objective
        ),
        StringComparison.Ordinal
      )
      || !string.Equals(
        checkpoint.Route.Provider,
        AgenticRouter.Api.Providers.ModelProviderIds.OllamaLocal,
        StringComparison.Ordinal
      )
      || string.IsNullOrWhiteSpace(
        checkpoint.Route.Model
      )
      || string.IsNullOrWhiteSpace(
        checkpoint.Route.ModelDigest
      )
      || string.Equals(
        checkpoint.Route.ModelDigest,
        "unavailable",
        StringComparison.Ordinal
      )
      || string.IsNullOrWhiteSpace(
        checkpoint.Route.Harness
      )
      || string.IsNullOrWhiteSpace(
        checkpoint.Route.HarnessVersion
      )
      || string.Equals(
        checkpoint.Route.HarnessVersion,
        "unavailable",
        StringComparison.Ordinal
      )
      || checkpoint.ApprovalPolicy is not "auto" and not "ask"
      || checkpoint.ResumePolicy is not SupervisionResumePolicies.Manual
        and not SupervisionResumePolicies.AutoSafe
      || (
        checkpoint.ResumePolicy == SupervisionResumePolicies.AutoSafe
        && !checkpoint.Durable
      )
      || checkpoint.Events.Count > MaximumEvents
      || checkpoint.Events.Any(
        item => !string.Equals(
          item.RunId,
          checkpoint.RunId,
          StringComparison.Ordinal
        )
      )
    )
    {
      throw InvalidCheckpoint(
        "The supervision checkpoint contract is invalid."
      );
    }

    ValidateEvents(
      checkpoint
    );
  }

  private static void ValidateIdentities(
    string workspaceId,
    string conversationSessionId,
    string runId
  )
  {
    SupervisionRequestPolicy.ValidateId(
      workspaceId,
      "workspace"
    );
    SupervisionRequestPolicy.ValidateId(
      conversationSessionId,
      "conversation session"
    );
    SupervisionRequestPolicy.ValidateId(
      runId,
      "run"
    );
  }

  private static SupervisionException InvalidCheckpoint(string message)
  {
    return new SupervisionException(
      "supervision-checkpoint-invalid",
      "supervision-checkpoint",
      message,
      false,
      500
    );
  }

  private static void ValidateEvents(
    DurableSupervisionCheckpoint checkpoint
  )
  {
    if (checkpoint.Events.Count == 0)
    {
      throw InvalidCheckpoint(
        "The supervision checkpoint has no lifecycle event."
      );
    }

    long previous = 0;
    var terminalCount = 0;
    for (var index = 0; index < checkpoint.Events.Count; index++)
    {
      var progressEvent = checkpoint.Events[index];
      if (progressEvent.Sequence <= previous)
      {
        throw InvalidCheckpoint(
          "Supervision event sequences must increase monotonically."
        );
      }
      previous = progressEvent.Sequence;

      if (progressEvent.Terminal)
      {
        terminalCount++;
        if (index != checkpoint.Events.Count - 1)
        {
          throw InvalidCheckpoint(
            "A terminal supervision event must be the last retained event."
          );
        }
      }
    }

    if (
      terminalCount > 1
      || DurableSupervisionRunStates.IsTerminal(
        checkpoint.State
      ) != (terminalCount == 1)
      || !string.Equals(
        checkpoint.Events[^1].State,
        checkpoint.State,
        StringComparison.Ordinal
      )
    )
    {
      throw InvalidCheckpoint(
        "The supervision checkpoint terminal state is inconsistent."
      );
    }
  }

  private static string ComputeIntegrity(
    DurableSupervisionCheckpoint checkpoint
  )
  {
    return SupervisionRequestPolicy.Hash(
      JsonSerializer.Serialize(
        checkpoint,
        IntegrityOptions
      )
    );
  }

  private static IEnumerable<string> EnumerateCheckpointPaths(string root)
  {
    foreach (var workspace in Directory.EnumerateDirectories(
      root,
      "*",
      SearchOption.TopDirectoryOnly
    ))
    {
      if (IsReparsePoint(
        workspace
      ))
      {
        continue;
      }

      var supervision = Path.Combine(
        workspace,
        "supervision"
      );
      if (
        !Directory.Exists(
          supervision
        )
        || IsReparsePoint(
          supervision
        )
      )
      {
        continue;
      }

      foreach (var conversation in Directory.EnumerateDirectories(
        supervision,
        "*",
        SearchOption.TopDirectoryOnly
      ))
      {
        if (IsReparsePoint(
          conversation
        ))
        {
          continue;
        }

        foreach (var path in Directory.EnumerateFiles(
          conversation,
          "*.json",
          SearchOption.TopDirectoryOnly
        ))
        {
          yield return path;
        }
      }
    }
  }

  private string CheckpointPath(
    string workspaceId,
    string conversationSessionId,
    string runId
  )
  {
    return Path.Combine(
      ConversationDirectory(
        workspaceId,
        conversationSessionId
      ),
      $"{runId}.json"
    );
  }

  private string ConversationDirectory(
    string workspaceId,
    string conversationSessionId
  )
  {
    return Path.Combine(
      _dataDirectory,
      "workspaces",
      workspaceId,
      "supervision",
      conversationSessionId
    );
  }

  private void DeleteEmptyParents(
    string directory,
    string workspaceId
  )
  {
    var supervisionRoot = Path.Combine(
      _dataDirectory,
      "workspaces",
      workspaceId,
      "supervision"
    );
    var current = directory;

    while (
      Directory.Exists(
        current
      )
      && !IsReparsePoint(
        current
      )
      && !Directory.EnumerateFileSystemEntries(
        current
      ).Any()
      && current.StartsWith(
        supervisionRoot,
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      Directory.Delete(
        current
      );
      if (string.Equals(
        current,
        supervisionRoot,
        StringComparison.OrdinalIgnoreCase
      ))
      {
        break;
      }
      current = Path.GetDirectoryName(
        current
      )!;
    }
  }

  private static bool IsReparsePoint(string path)
  {
    return (
      File.GetAttributes(
        path
      )
      & FileAttributes.ReparsePoint
    ) != 0;
  }

  private static void RejectReparsePoint(string path)
  {
    if (IsReparsePoint(
      path
    ))
    {
      throw new SupervisionException(
        "supervision-checkpoint-reparse-rejected",
        "supervision-checkpoint",
        "Supervision checkpoint directories cannot be reparse points.",
        false,
        409
      );
    }
  }
}
