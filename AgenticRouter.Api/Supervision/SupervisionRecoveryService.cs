using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.ProjectAwareness;

namespace AgenticRouter.Api.Supervision;

internal sealed record SupervisionReconciliationResult(
  bool Eligible,
  string? WaitCode,
  string? Reason,
  SupervisionRuntimeView Runtime,
  SupervisionRecoverySnapshot Recovery
);

internal interface ISupervisionRecoveryService
{
  Task<SupervisionRecoverySnapshot> CaptureAsync(
    DurableSupervisionCheckpoint checkpoint,
    SupervisionRuntimeView runtime,
    IReadOnlyList<SupervisionActionCheckpoint>? actions,
    CancellationToken cancellationToken
  );

  Task<SupervisionReconciliationResult> ReconcileAsync(
    DurableSupervisionCheckpoint checkpoint,
    bool manualContinuation,
    CancellationToken cancellationToken
  );
}

internal sealed class SupervisionRecoveryService : ISupervisionRecoveryService
{
  private const int MaximumTrackedFiles = 64;
  private const int MaximumActions = 64;
  private readonly ITrustedWorkspaceService _workspace;
  private readonly IRepositoryInstructionService _instructions;
  private readonly ISettingsStore _settings;

  public SupervisionRecoveryService(
    ITrustedWorkspaceService workspace,
    IRepositoryInstructionService instructions,
    ISettingsStore settings
  )
  {
    _workspace = workspace;
    _instructions = instructions;
    _settings = settings;
  }

  public async Task<SupervisionRecoverySnapshot> CaptureAsync(
    DurableSupervisionCheckpoint checkpoint,
    SupervisionRuntimeView runtime,
    IReadOnlyList<SupervisionActionCheckpoint>? actions,
    CancellationToken cancellationToken
  )
  {
    var instructionSet = await _instructions.ResolveAsync(null, cancellationToken);
    if (!string.IsNullOrWhiteSpace(instructionSet.Diagnostic))
    {
      throw new SupervisionException(
        "supervision-instructions-unavailable",
        "supervision-checkpoint",
        "Repository instructions could not be loaded completely: " + instructionSet.Diagnostic,
        true,
        409
      );
    }

    var retainedActions = (actions ?? checkpoint.Recovery?.Actions ?? [])
      .TakeLast(MaximumActions)
      .ToArray();
    var paths = runtime.WorkItems.SelectMany(item => item.EvidencePaths)
      .Concat(retainedActions.SelectMany(action => action.FileEffects.Select(effect => effect.RelativePath)))
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Take(MaximumTrackedFiles + 1)
      .ToArray();
    if (paths.Length > MaximumTrackedFiles)
    {
      throw new SupervisionException(
        "supervision-recovery-file-limit",
        "supervision-checkpoint",
        "The durable supervision file ledger exceeds its bounded recovery limit.",
        false,
        413
      );
    }

    var tracked = new List<SupervisionTrackedFileSnapshot>(paths.Length);
    foreach (var path in paths)
    {
      tracked.Add(await InspectAsync(path, cancellationToken));
    }

    var settings = await _settings.GetAsync(cancellationToken);
    var budgets = checkpoint.Recovery?.Budgets ?? new SupervisionBudgetSnapshot(
      settings.ProjectAwareness.MaxPlanSteps,
      checked(settings.ProjectAwareness.MaxPlanSteps * 2),
      settings.Execution.MaxRecoveryAttemptsPerTurn
    );
    return new SupervisionRecoverySnapshot(
      HashInstructions(instructionSet.AppliedFiles, instructionSet.Content),
      instructionSet.AppliedFiles.Take(32).ToArray(),
      tracked,
      retainedActions,
      budgets,
      runtime.ActiveRole is not null,
      DateTimeOffset.UtcNow,
      checkpoint.Recovery?.ImagesPending == true
    );
  }

  public async Task<SupervisionReconciliationResult> ReconcileAsync(
    DurableSupervisionCheckpoint checkpoint,
    bool manualContinuation,
    CancellationToken cancellationToken
  )
  {
    var runtime = checkpoint.Runtime
      ?? SupervisionRuntimeView.Empty(recoverableInCurrentProcess: false);
    var prior = checkpoint.Recovery;
    if (prior is null || checkpoint.Runtime is null)
    {
      return Failed(
        "supervision-recovery-state-missing",
        "The durable checkpoint predates executable M3 recovery state and requires discard or a fresh run.",
        runtime,
        prior ?? await CaptureAsync(checkpoint, runtime, [], cancellationToken)
      );
    }

    var current = await CaptureAsync(checkpoint, runtime, prior.Actions, cancellationToken);
    if (prior.ImagesPending)
    {
      return Failed(
        "supervision-recovery-images-required",
        "The first worker turn still requires image attachments that are intentionally not persisted.",
        runtime,
        current
      );
    }
    if (!string.Equals(prior.InstructionSha256, current.InstructionSha256, StringComparison.Ordinal))
    {
      if (!manualContinuation)
      {
        return Failed(
          "supervision-recovery-instructions-changed",
          "Repository instructions changed after the last committed checkpoint.",
          runtime,
          current
        );
      }
    }

    if (
      runtime.SupervisorTransitionCount >= prior.Budgets.MaximumSupervisorTransitions
      || runtime.WorkItems.Any(item =>
        item.Status != SupervisionWorkItemStates.Completed
        && item.AttemptCount >= prior.Budgets.MaximumWorkerAttempts
      )
    )
    {
      return Failed(
        "supervision-recovery-budget-exhausted",
        "The durable checkpoint has no remaining supervisor or worker recovery budget.",
        runtime,
        current
      );
    }

    var actions = prior.Actions.ToList();
    var excludedDriftPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var unresolved = actions.Where(action => action.Phase is
      SupervisionActionPhases.Prepared
      or SupervisionActionPhases.AwaitingApproval
      or SupervisionActionPhases.InFlight
      or SupervisionActionPhases.Ambiguous).ToArray();
    foreach (var action in unresolved)
    {
      var index = actions.FindIndex(candidate => string.Equals(
        candidate.ActionId,
        action.ActionId,
        StringComparison.Ordinal
      ));
      if (action.Phase == SupervisionActionPhases.AwaitingApproval
        || action.RequiresApproval && action.Phase == SupervisionActionPhases.Prepared)
      {
        if (!manualContinuation)
        {
          return Failed(
            "supervision-recovery-approval-pending",
            "A Host-governed action was awaiting user approval when the prior process stopped.",
            runtime,
            current
          );
        }
        actions[index] = action with
        {
          Phase = SupervisionActionPhases.Abandoned,
          UpdatedAt = DateTimeOffset.UtcNow,
          Reconciliation = "The pending approval was abandoned; a new action requires a new approval."
        };
        continue;
      }

      if (action.Phase == SupervisionActionPhases.Prepared)
      {
        actions[index] = action with
        {
          Phase = SupervisionActionPhases.Abandoned,
          UpdatedAt = DateTimeOffset.UtcNow,
          Reconciliation = "The action was durably prepared but never dispatched."
        };
        continue;
      }

      if (action.Phase == SupervisionActionPhases.Ambiguous)
      {
        if (!manualContinuation)
        {
          return Failed(
            "supervision-recovery-action-ambiguous",
            $"Action {action.ActionId} ({action.Tool}) has an unresolved effect from an earlier process.",
            runtime,
            current
          );
        }
        continue;
      }

      foreach (var effect in action.FileEffects)
      {
        excludedDriftPaths.Add(effect.RelativePath);
      }
      var classification = await ClassifyInFlightAsync(action, cancellationToken);
      if (classification is InFlightClassification.Ambiguous && !manualContinuation)
      {
        return Failed(
          "supervision-recovery-action-ambiguous",
          $"Action {action.ActionId} ({action.Tool}) may have executed, but its complete effect cannot be proven.",
          runtime,
          current
        );
      }
      actions[index] = action with
      {
        Phase = classification switch
        {
          InFlightClassification.IntendedEffectObserved => SupervisionActionPhases.Committed,
          InFlightClassification.EffectAbsent => SupervisionActionPhases.Abandoned,
          _ => SupervisionActionPhases.Ambiguous
        },
        UpdatedAt = DateTimeOffset.UtcNow,
        Reconciliation = classification switch
        {
          InFlightClassification.IntendedEffectObserved => "The intended file effect was independently observed after restart.",
          InFlightClassification.EffectAbsent => "The pre-action file state was independently observed after restart.",
          _ => "The user explicitly resumed from an ambiguous action boundary; the reconstructed worker must inspect before mutation."
        }
      };
    }

    foreach (var before in prior.TrackedFiles)
    {
      if (excludedDriftPaths.Contains(before.RelativePath))
      {
        continue;
      }
      var after = current.TrackedFiles.FirstOrDefault(item => string.Equals(
        item.RelativePath,
        before.RelativePath,
        StringComparison.OrdinalIgnoreCase
      ));
      if (!SameFile(before, after))
      {
        if (!manualContinuation)
        {
          return Failed(
            "supervision-recovery-workspace-drift",
            $"Tracked workspace path '{before.RelativePath}' changed after the last committed checkpoint.",
            runtime,
            current
          );
        }
      }
    }

    if (
      prior.TurnInFlight
      && string.Equals(runtime.ActiveRole, "worker", StringComparison.Ordinal)
      && unresolved.Length == 0
      && !manualContinuation
    )
    {
      return Failed(
        "supervision-recovery-worker-turn-ambiguous",
        "A worker or harness turn was in flight without a fully reconciled Host action.",
        runtime,
        current
      );
    }

    var reconciliationMessage = "CRASH_RECONCILIATION: The prior Host process stopped. Inspect current Host facts before proposing any mutation.";
    var workItems = runtime.WorkItems.Select(item =>
      item.Status is SupervisionWorkItemStates.Active or SupervisionWorkItemStates.Verifying
        ? item with
        {
          Status = SupervisionWorkItemStates.Pending,
          LastDiscrepancy = reconciliationMessage
        }
        : item
    ).ToArray();
    var contexts = runtime.Contexts.Select(context =>
      context.State == SupervisionContextStates.Active
        ? context with
        {
          State = SupervisionContextStates.Abandoned,
          Revision = context.Revision + 1,
          LastOutcome = reconciliationMessage,
          UpdatedAt = DateTimeOffset.UtcNow
        }
        : context
    ).ToArray();
    runtime = runtime with
    {
      WorkItems = workItems,
      Contexts = contexts,
      ActiveRole = null,
      ActiveWorkItemId = null,
      LastFailure = null,
      RecoverableInCurrentProcess = true
    };
    current = await CaptureAsync(checkpoint, runtime, actions, cancellationToken);
    current = current with { TurnInFlight = false };
    return new SupervisionReconciliationResult(true, null, null, runtime, current);
  }

  private async Task<InFlightClassification> ClassifyInFlightAsync(
    SupervisionActionCheckpoint action,
    CancellationToken cancellationToken
  )
  {
    if (action.ReadOnly)
    {
      return InFlightClassification.EffectAbsent;
    }
    if (action.FileEffects.Count == 0)
    {
      return InFlightClassification.Ambiguous;
    }

    var intended = true;
    var absent = true;
    foreach (var effect in action.FileEffects)
    {
      var observed = await InspectAsync(effect.RelativePath, cancellationToken);
      intended &= MatchesExpected(effect, observed);
      absent &= MatchesOriginal(effect, observed);
    }
    return intended
      ? InFlightClassification.IntendedEffectObserved
      : absent
        ? InFlightClassification.EffectAbsent
        : InFlightClassification.Ambiguous;
  }

  private async Task<SupervisionTrackedFileSnapshot> InspectAsync(
    string relativePath,
    CancellationToken cancellationToken
  )
  {
    var resolution = await _workspace.ResolveCreationPathAsync(relativePath, cancellationToken);
    if (File.Exists(resolution.FullPath))
    {
      var bytes = await File.ReadAllBytesAsync(resolution.FullPath, cancellationToken);
      return new SupervisionTrackedFileSnapshot(
        resolution.RelativePath.Replace('\\', '/'),
        "file",
        Hash(bytes),
        bytes.LongLength
      );
    }
    if (Directory.Exists(resolution.FullPath))
    {
      return new SupervisionTrackedFileSnapshot(
        resolution.RelativePath.Replace('\\', '/'),
        "directory",
        null,
        null
      );
    }
    return new SupervisionTrackedFileSnapshot(
      resolution.RelativePath.Replace('\\', '/'),
      "missing",
      null,
      null
    );
  }

  private static bool MatchesExpected(
    SupervisionActionFileEffect effect,
    SupervisionTrackedFileSnapshot observed
  )
  {
    return effect.Operation.StartsWith("deleted", StringComparison.Ordinal)
      ? observed.State == "missing"
      : observed.State == "file"
        && string.Equals(observed.Sha256, effect.ExpectedFinalSha256, StringComparison.Ordinal);
  }

  private static bool MatchesOriginal(
    SupervisionActionFileEffect effect,
    SupervisionTrackedFileSnapshot observed
  )
  {
    return !effect.ExistedBefore
      ? observed.State == "missing"
      : effect.Operation == "deleted-directory"
        ? observed.State == "directory"
        : observed.State == "file"
          && string.Equals(observed.Sha256, effect.OriginalSha256, StringComparison.Ordinal);
  }

  private static bool SameFile(
    SupervisionTrackedFileSnapshot before,
    SupervisionTrackedFileSnapshot? after
  )
  {
    return after is not null
      && string.Equals(before.State, after.State, StringComparison.Ordinal)
      && string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal)
      && before.Bytes == after.Bytes;
  }

  private static string HashInstructions(
    IReadOnlyList<string> files,
    string content
  )
  {
    return Hash(Encoding.UTF8.GetBytes(
      string.Join("\n", files.Select(file => file.Replace('\\', '/'))) + "\n" + content
    ));
  }

  private static string Hash(byte[] bytes)
  {
    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
  }

  private static SupervisionReconciliationResult Failed(
    string code,
    string reason,
    SupervisionRuntimeView runtime,
    SupervisionRecoverySnapshot recovery
  )
  {
    return new SupervisionReconciliationResult(false, code, reason, runtime, recovery);
  }

  private enum InFlightClassification
  {
    IntendedEffectObserved,
    EffectAbsent,
    Ambiguous
  }
}
