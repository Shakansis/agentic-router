using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.ProjectAwareness;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Execution;

public interface IValidationProfileService
{
  Task<ValidationProfileState> GetStateAsync(
    CancellationToken cancellationToken
  );

  Task<SettingsSaveResult> SaveAsync(
    ValidationProfileSettings profile,
    CancellationToken cancellationToken
  );

  Task<SettingsSaveResult> ClearAsync(
    CancellationToken cancellationToken
  );

  Task<ValidationRunView> RunAsync(
    ExecutionSession session,
    CancellationToken cancellationToken
  );
}

public sealed class ValidationProfileService : IValidationProfileService
{
  private readonly ISettingsStore _settingsStore;
  private readonly IProjectAwarenessService _projectAwareness;
  private readonly IProcessPolicyService _processPolicy;
  private readonly IProcessExecutionService _processExecution;
  private readonly IWorkspaceProfileService _workspaceProfiles;

  public ValidationProfileService(
    ISettingsStore settingsStore,
    IProjectAwarenessService projectAwareness,
    IProcessPolicyService processPolicy,
    IProcessExecutionService processExecution,
    IWorkspaceProfileService workspaceProfiles
  )
  {
    _settingsStore = settingsStore;
    _projectAwareness = projectAwareness;
    _processPolicy = processPolicy;
    _processExecution = processExecution;
    _workspaceProfiles = workspaceProfiles;
  }

  public async Task<ValidationProfileState> GetStateAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var project = await _projectAwareness.GetAsync(
      false,
      cancellationToken
    );
    var active = await _workspaceProfiles.GetActiveDataAsync(
      cancellationToken
    );
    return new ValidationProfileState(
      active?.ValidationProfile
        ?? settings.ValidationProfile,
      project.DetectedValidationProfile
    );
  }

  public async Task<SettingsSaveResult> SaveAsync(
    ValidationProfileSettings profile,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var normalized = profile with
    {
      Source = "user"
    };
    var result = await _settingsStore.SaveAsync(
      settings with
      {
        ValidationProfile = normalized
      },
      cancellationToken
    );
    if (result.IsValid)
    {
      await _workspaceProfiles.UpdateValidationProfileAsync(
        normalized,
        cancellationToken
      );
    }

    return result;
  }

  public async Task<SettingsSaveResult> ClearAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var result = await _settingsStore.SaveAsync(
      settings with
      {
        ValidationProfile = null
      },
      cancellationToken
    );
    if (result.IsValid)
    {
      await _workspaceProfiles.UpdateValidationProfileAsync(
        null,
        cancellationToken
      );
    }

    return result;
  }

  public async Task<ValidationRunView> RunAsync(
    ExecutionSession session,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var active = await _workspaceProfiles.GetActiveDataAsync(
      cancellationToken
    );
    var profile = active?.ValidationProfile
      ?? settings.ValidationProfile;
    session.SelectValidationProfile(
      profile
    );
    var started = DateTimeOffset.UtcNow;

    if (profile is null)
    {
      var missing = new ValidationRunView(
        "not-configured",
        null,
        started,
        DateTimeOffset.UtcNow,
        [],
        PriorAttempts(
          session
        )
      );
      session.RecordValidation(
        missing
      );
      return missing;
    }

    var results = new List<ValidationStepResultView>();
    var optionalFailure = false;
    var state = "passed";

    foreach (var step in profile.Steps)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var stepStarted = DateTimeOffset.UtcNow;
      ValidatedProcessCommand command;

      try
      {
        command = await _processPolicy.ValidateAsync(
          step.Executable,
          step.Arguments,
          step.WorkingDirectory,
          cancellationToken
        );

        if (command.RequiresExplicitApproval)
        {
          throw new LocalActionException(
            "validation-executable-blocked",
            "The validation step is not on the existing safe structured-command allowlist."
          );
        }
      }
      catch (LocalActionException exception)
      {
        results.Add(
          new ValidationStepResultView(
            step.Id,
            step.Label,
            step.Executable,
            step.Arguments,
            step.WorkingDirectory,
            step.Required,
            stepStarted,
            DateTimeOffset.UtcNow,
            null,
            0,
            false,
            false,
            false,
            false,
            "blocked",
            string.Empty,
            exception.Message
          )
        );
        state = "failed";
        break;
      }

      var process = await _processExecution.ExecuteAsync(
        new ProcessExecutionRequest(
          command.Executable,
          command.Arguments,
          command.WorkingDirectory,
          TimeSpan.FromSeconds(
            step.TimeoutSeconds
          )
        ),
        cancellationToken
      );
      var status = process.Cancelled
        ? "cancelled"
        : process.TimedOut
          ? "failed"
          : process.ExitCode == 0
            ? "passed"
            : "failed";
      var result = new ValidationStepResultView(
        step.Id,
        step.Label,
        step.Executable,
        command.Arguments,
        Path.GetRelativePath(
          session.WorkspacePath,
          command.WorkingDirectory
        ),
        step.Required,
        stepStarted,
        DateTimeOffset.UtcNow,
        process.ExitCode,
        process.DurationMilliseconds,
        process.TimedOut,
        process.Cancelled,
        process.StandardOutputTruncated,
        process.StandardErrorTruncated,
        status,
        process.StandardOutput,
        process.StandardError
      );
      results.Add(
        result
      );
      session.RecordProcess(
        new ExecutionProcessReview(
          step.Executable,
          command.Arguments,
          result.WorkingDirectory,
          process.ExitCode,
          process.DurationMilliseconds,
          process.TimedOut,
          process.Cancelled,
          process.StandardOutputTruncated,
          process.StandardErrorTruncated,
          process.StandardOutput,
          process.StandardError
        )
      );

      if (process.Cancelled)
      {
        state = "cancelled";
        break;
      }

      if (status != "passed")
      {
        if (step.Required)
        {
          state = "failed";
          break;
        }

        optionalFailure = true;
      }
    }

    if (state == "passed" && optionalFailure)
    {
      state = "passed-with-warnings";
    }

    var validation = new ValidationRunView(
      state,
      profile.Name,
      started,
      DateTimeOffset.UtcNow,
      results,
      PriorAttempts(
        session
      )
    );
    session.RecordValidation(
      validation
    );
    return validation;
  }

  public static string FormatResult(
    ValidationRunView validation
  )
  {
    var output = new StringBuilder();
    output.Append(
      "Validation "
    ).Append(
      validation.State
    ).Append(
      ": "
    ).AppendLine(
      validation.ProfileName
        ?? "not configured"
    );

    foreach (var step in validation.Steps)
    {
      output.Append(
        step.Id
      ).Append(
        ": "
      ).Append(
        step.Status
      ).Append(
        " (exit "
      ).Append(
        step.ExitCode?.ToString()
          ?? "n/a"
      ).AppendLine(
        ")"
      );
    }

    return output.ToString().TrimEnd();
  }

  private static IReadOnlyList<ValidationRunView> PriorAttempts(
    ExecutionSession session
  )
  {
    var current = session.CreateReview().Validation;

    if (current is null)
    {
      return [];
    }

    return
    [
      current with
      {
        PriorAttempts = []
      },
      .. current.PriorAttempts.Take(
        2
      )
    ];
  }
}
