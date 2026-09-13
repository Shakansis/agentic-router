using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
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

      var json = await File.ReadAllTextAsync(path, cancellationToken);
      using var document = JsonDocument.Parse(json);
      if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
        || !schemaElement.TryGetInt32(out var schemaVersion))
      {
        throw InvalidCheckpoint("The supervision checkpoint schema version is missing.");
      }
      DurableSupervisionCheckpoint checkpoint;
      if (schemaVersion == 1)
      {
        var legacy = JsonSerializer.Deserialize<DurableSupervisionCheckpointV1>(json, JsonOptions)
          ?? throw InvalidCheckpoint("The supervision checkpoint is empty.");
        var legacyExpected = ComputeLegacyJsonIntegrity(json);
        if (!string.Equals(legacyExpected, legacy.IntegritySha256, StringComparison.Ordinal))
        {
          throw InvalidCheckpoint("The supervision checkpoint integrity hash is invalid.");
        }
        checkpoint = Migrate(legacy);
      }
      else if (schemaVersion == 2)
      {
        var legacy = JsonSerializer.Deserialize<DurableSupervisionCheckpointV2>(json, JsonOptions)
          ?? throw InvalidCheckpoint("The supervision checkpoint is empty.");
        var legacyExpected = ComputeLegacyJsonIntegrity(json);
        if (!string.Equals(legacyExpected, legacy.IntegritySha256, StringComparison.Ordinal))
        {
          throw InvalidCheckpoint("The supervision checkpoint integrity hash is invalid.");
        }
        checkpoint = Migrate(legacy);
      }
      else if (schemaVersion == 3)
      {
        var legacy = JsonSerializer.Deserialize<DurableSupervisionCheckpointV3>(json, JsonOptions)
          ?? throw InvalidCheckpoint("The supervision checkpoint is empty.");
        var legacyExpected = ComputeLegacyJsonIntegrity(json);
        if (!string.Equals(legacyExpected, legacy.IntegritySha256, StringComparison.Ordinal))
        {
          throw InvalidCheckpoint("The supervision checkpoint integrity hash is invalid.");
        }
        checkpoint = Migrate(legacy);
      }
      else if (schemaVersion == 4)
      {
        var legacy = JsonSerializer.Deserialize<DurableSupervisionCheckpoint>(json, JsonOptions)
          ?? throw InvalidCheckpoint("The supervision checkpoint is empty.");
        var legacyExpected = ComputeLegacyJsonIntegrity(json);
        if (!string.Equals(legacyExpected, legacy.IntegritySha256, StringComparison.Ordinal))
        {
          throw InvalidCheckpoint("The supervision checkpoint integrity hash is invalid.");
        }
        checkpoint = MigrateV4(legacy);
      }
      else
      {
        checkpoint = JsonSerializer.Deserialize<DurableSupervisionCheckpoint>(json, JsonOptions)
          ?? throw InvalidCheckpoint("The supervision checkpoint is empty.");
      }
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
      || checkpoint.ExecutionStrategy is not SupervisionExecutionStrategies.Auto
        and not SupervisionExecutionStrategies.Autonomous
        and not SupervisionExecutionStrategies.Supervised
      || checkpoint.ResumePolicy is not SupervisionResumePolicies.Manual
        and not SupervisionResumePolicies.AutoSafe
      || (
        checkpoint.ExecutionStrategy == SupervisionExecutionStrategies.Autonomous
        && checkpoint.Durable
        && checkpoint.ResumePolicy != SupervisionResumePolicies.AutoSafe
      )
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
      || checkpoint.WaitCode?.Length > 128
      || (checkpoint.Runtime is null) != (checkpoint.Recovery is null)
      || !IsValidTakeover(checkpoint.Takeover)
    )
    {
      throw InvalidCheckpoint(
        "The supervision checkpoint contract is invalid."
      );
    }

    ValidateEvents(
      checkpoint
    );
    if (checkpoint.Runtime is not null && checkpoint.Recovery is not null)
    {
      ValidateRecovery(checkpoint.Runtime, checkpoint.Recovery);
    }
  }

  private static void ValidateRecovery(
    SupervisionRuntimeView runtime,
    SupervisionRecoverySnapshot recovery
  )
  {
    if (
      recovery.InstructionSha256.Length != 64
      || recovery.InstructionFiles.Count > 32
      || recovery.InstructionFiles.Any(IsUnsafeRelativePath)
      || recovery.TrackedFiles.Count > 64
      || recovery.TrackedFiles.Any(file =>
        IsUnsafeRelativePath(file.RelativePath)
        || file.State is not "file" and not "directory" and not "missing"
        || file.Sha256 is not null && file.Sha256.Length != 64
      )
      || recovery.Actions.Count > 64
      || recovery.Actions.Any(action =>
        string.IsNullOrWhiteSpace(action.ActionId)
        || action.ActionId.Length > 128
        || string.IsNullOrWhiteSpace(action.ContextId)
        || action.ContextId.Length > 128
        || string.IsNullOrWhiteSpace(action.Tool)
        || action.Tool.Length > 128
        || action.ArgumentsSha256.Length != 64
        || action.ResultSha256 is not null && action.ResultSha256.Length != 64
        || action.Phase is not SupervisionActionPhases.Prepared
          and not SupervisionActionPhases.AwaitingApproval
          and not SupervisionActionPhases.InFlight
          and not SupervisionActionPhases.Committed
          and not SupervisionActionPhases.Failed
          and not SupervisionActionPhases.Rejected
          and not SupervisionActionPhases.Abandoned
          and not SupervisionActionPhases.Ambiguous
        || action.FileEffects.Count > 64
        || action.FileEffects.Any(effect =>
          IsUnsafeRelativePath(effect.RelativePath)
          || effect.OriginalSha256.Length != 64
          || effect.ExpectedFinalSha256.Length != 64
        )
      )
      || recovery.Budgets.MaximumWorkItems is < 1 or > 128
      || recovery.Budgets.MaximumSupervisorTransitions is < 1 or > 256
      || recovery.Budgets.MaximumWorkerAttempts is < 1 or > 64
      || runtime.WorkItems.Count > recovery.Budgets.MaximumWorkItems
      || runtime.WorkItems.Any(item =>
        item.RejectionReason?.Length > 128
        || item.RetryReason?.Length > 128
      )
      || runtime.Contexts.Count > recovery.Budgets.MaximumWorkItems + 1
      || runtime.CompletedItems < 0
      || runtime.CompletedItems > runtime.TotalItems
      || runtime.TotalItems != runtime.WorkItems.Count
      || runtime.Telemetry is { } telemetry && (
        telemetry.DecompositionDurationMilliseconds < 0
        || telemetry.WorkerDurationMilliseconds < 0
        || telemetry.VerificationDurationMilliseconds < 0
        || telemetry.CorrectionDurationMilliseconds < 0
        || telemetry.FinalCompletionDurationMilliseconds < 0
        || telemetry.WorkerAttemptCount < 0
        || telemetry.SupervisorTransitionCount < 0
        || telemetry.ActualWorkspaceMutationCount < 0
        || telemetry.RejectionReason?.Length > 128
        || telemetry.RetryReason?.Length > 128
      )
    )
    {
      throw InvalidCheckpoint("The supervision recovery ledger is invalid or exceeds its bounds.");
    }
  }

  private static bool IsUnsafeRelativePath(string path)
  {
    return string.IsNullOrWhiteSpace(path)
      || path.Length > 512
      || Path.IsPathFullyQualified(path)
      || path.Split('/', '\\').Any(segment => segment == "..");
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
      if (
        progressEvent.RejectionReason?.Length > 128
        || progressEvent.RetryReason?.Length > 128
        || progressEvent.DurationMilliseconds < 0
      )
      {
        throw InvalidCheckpoint(
          "Supervision event telemetry is invalid or exceeds its bounds."
        );
      }
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

  private static string ComputeLegacyJsonIntegrity(string json)
  {
    var source = Encoding.UTF8.GetBytes(json);
    var reader = new Utf8JsonReader(source);
    var integrityValueStart = -1;
    var integrityValueEnd = -1;

    while (reader.Read())
    {
      if (
        reader.TokenType != JsonTokenType.PropertyName
        || !reader.ValueTextEquals("integritySha256")
      )
      {
        continue;
      }
      if (
        integrityValueStart >= 0
        || !reader.Read()
        || reader.TokenType != JsonTokenType.String
      )
      {
        throw InvalidCheckpoint(
          "The supervision checkpoint integrity field is invalid."
        );
      }
      integrityValueStart = checked((int)reader.TokenStartIndex);
      integrityValueEnd = checked((int)reader.BytesConsumed);
    }

    if (integrityValueStart < 0 || integrityValueEnd <= integrityValueStart)
    {
      throw InvalidCheckpoint(
        "The supervision checkpoint integrity field is missing."
      );
    }

    using var canonical = new MemoryStream(source.Length);
    var inString = false;
    var escaped = false;
    var index = 0;
    while (index < source.Length)
    {
      if (index == integrityValueStart)
      {
        canonical.WriteByte((byte)'"');
        canonical.WriteByte((byte)'"');
        index = integrityValueEnd;
        continue;
      }

      var current = source[index++];
      if (inString)
      {
        canonical.WriteByte(current);
        if (escaped)
        {
          escaped = false;
        }
        else if (current == (byte)'\\')
        {
          escaped = true;
        }
        else if (current == (byte)'"')
        {
          inString = false;
        }
        continue;
      }

      if (current == (byte)'"')
      {
        inString = true;
        canonical.WriteByte(current);
      }
      else if (current is not (byte)' ' and not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
      {
        canonical.WriteByte(current);
      }
    }

    return SupervisionRequestPolicy.Hash(
      Encoding.UTF8.GetString(canonical.ToArray())
    );
  }

  private static DurableSupervisionCheckpoint Migrate(
    DurableSupervisionCheckpointV1 checkpoint
  )
  {
    var migrated = new DurableSupervisionCheckpoint(
      DurableSupervisionCheckpoint.CurrentSchemaVersion,
      checkpoint.RunId,
      checkpoint.WorkspaceId,
      checkpoint.ConversationSessionId,
      checkpoint.BrowserSessionId,
      checkpoint.Objective,
      checkpoint.ObjectiveSha256,
      checkpoint.Route,
      checkpoint.ApprovalPolicy,
      checkpoint.ResumePolicy,
      checkpoint.State,
      checkpoint.Phase,
      checkpoint.Revision,
      checkpoint.Durable,
      false,
      checkpoint.WaitReason,
      checkpoint.Events,
      checkpoint.CreatedAt,
      checkpoint.UpdatedAt,
      string.Empty,
      Runtime: null,
      Recovery: null,
      WaitCode: "supervision-recovery-state-missing"
    );
    return migrated with { IntegritySha256 = ComputeIntegrity(migrated) };
  }

  private static DurableSupervisionCheckpoint Migrate(
    DurableSupervisionCheckpointV2 checkpoint
  )
  {
    var migrated = new DurableSupervisionCheckpoint(
      DurableSupervisionCheckpoint.CurrentSchemaVersion,
      checkpoint.RunId,
      checkpoint.WorkspaceId,
      checkpoint.ConversationSessionId,
      checkpoint.BrowserSessionId,
      checkpoint.Objective,
      checkpoint.ObjectiveSha256,
      checkpoint.Route,
      checkpoint.ApprovalPolicy,
      checkpoint.ResumePolicy,
      checkpoint.State,
      checkpoint.Phase,
      checkpoint.Revision,
      checkpoint.Durable,
      checkpoint.AutoResumeEligible,
      checkpoint.WaitReason,
      checkpoint.Events,
      checkpoint.CreatedAt,
      checkpoint.UpdatedAt,
      string.Empty,
      checkpoint.Runtime,
      checkpoint.Recovery,
      checkpoint.WaitCode
    );
    return migrated with { IntegritySha256 = ComputeIntegrity(migrated) };
  }

  private static DurableSupervisionCheckpoint Migrate(
    DurableSupervisionCheckpointV3 checkpoint
  )
  {
    var migrated = new DurableSupervisionCheckpoint(
      DurableSupervisionCheckpoint.CurrentSchemaVersion,
      checkpoint.RunId,
      checkpoint.WorkspaceId,
      checkpoint.ConversationSessionId,
      checkpoint.BrowserSessionId,
      checkpoint.Objective,
      checkpoint.ObjectiveSha256,
      checkpoint.Route,
      checkpoint.ApprovalPolicy,
      checkpoint.ResumePolicy,
      checkpoint.State,
      checkpoint.Phase,
      checkpoint.Revision,
      checkpoint.Durable,
      checkpoint.AutoResumeEligible,
      checkpoint.WaitReason,
      checkpoint.Events,
      checkpoint.CreatedAt,
      checkpoint.UpdatedAt,
      string.Empty,
      checkpoint.Runtime,
      checkpoint.Recovery,
      checkpoint.WaitCode,
      checkpoint.Takeover
    );
    return migrated with { IntegritySha256 = ComputeIntegrity(migrated) };
  }

  private static DurableSupervisionCheckpoint MigrateV4(
    DurableSupervisionCheckpoint checkpoint
  )
  {
    var runtime = checkpoint.Runtime is null
      ? null
      : checkpoint.Runtime with
      {
        Telemetry = checkpoint.Runtime.Telemetry ?? SupervisionTelemetryView.Empty
      };
    var migrated = checkpoint with
    {
      SchemaVersion = DurableSupervisionCheckpoint.CurrentSchemaVersion,
      Runtime = runtime,
      IntegritySha256 = string.Empty
    };
    return migrated with { IntegritySha256 = ComputeIntegrity(migrated) };
  }

  private static bool IsValidTakeover(SupervisionTakeoverSnapshot? takeover)
  {
    return takeover is null
      || (
        takeover.Trigger is { Length: > 0 and <= 128 }
        && takeover.DirectExecutionSessionId is { Length: > 0 and <= 64 }
        && takeover.DetectedPlanSteps is >= 0 and <= ProjectAwarenessSettings.MaximumPlanSteps
        && takeover.MaximumDirectPlanSteps is >= 1 and <= ProjectAwarenessSettings.MaximumPlanSteps
        && (
          takeover.Plan is null
            ? takeover.DetectedPlanSteps == 0
            : takeover.Plan.Steps.Count == takeover.DetectedPlanSteps
        )
        && takeover.Files is { Count: <= 64 }
        && takeover.Files.All(file =>
          file.RelativePath is { Length: > 0 and <= 1_024 }
          && !IsUnsafeRelativePath(file.RelativePath)
          && file.Operation is { Length: > 0 and <= 64 }
          && file.FinalHash is { Length: > 0 and <= 128 }
          && file.Verified
        )
        && takeover.DirectCompletionStatus is { Length: <= 128 }
        && takeover.ValidationStatus?.Length <= 128
      );
  }

  private sealed record DurableSupervisionCheckpointV1(
    int SchemaVersion,
    string RunId,
    string WorkspaceId,
    string ConversationSessionId,
    string BrowserSessionId,
    string Objective,
    string ObjectiveSha256,
    SupervisionRouteSnapshot Route,
    string ApprovalPolicy,
    string ResumePolicy,
    string State,
    string Phase,
    long Revision,
    bool Durable,
    bool AutoResumeEligible,
    string? WaitReason,
    IReadOnlyList<SupervisionRunEvent> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string IntegritySha256
  );

  private sealed record DurableSupervisionCheckpointV2(
    int SchemaVersion,
    string RunId,
    string WorkspaceId,
    string ConversationSessionId,
    string BrowserSessionId,
    string Objective,
    string ObjectiveSha256,
    SupervisionRouteSnapshot Route,
    string ApprovalPolicy,
    string ResumePolicy,
    string State,
    string Phase,
    long Revision,
    bool Durable,
    bool AutoResumeEligible,
    string? WaitReason,
    IReadOnlyList<SupervisionRunEvent> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string IntegritySha256,
    SupervisionRuntimeView? Runtime = null,
    SupervisionRecoverySnapshot? Recovery = null,
    string? WaitCode = null
  );

  private sealed record DurableSupervisionCheckpointV3(
    int SchemaVersion,
    string RunId,
    string WorkspaceId,
    string ConversationSessionId,
    string BrowserSessionId,
    string Objective,
    string ObjectiveSha256,
    SupervisionRouteSnapshot Route,
    string ApprovalPolicy,
    string ResumePolicy,
    string State,
    string Phase,
    long Revision,
    bool Durable,
    bool AutoResumeEligible,
    string? WaitReason,
    IReadOnlyList<SupervisionRunEvent> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string IntegritySha256,
    SupervisionRuntimeView? Runtime = null,
    SupervisionRecoverySnapshot? Recovery = null,
    string? WaitCode = null,
    SupervisionTakeoverSnapshot? Takeover = null
  );

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
