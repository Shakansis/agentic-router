using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Platform;

namespace AgenticRouter.Api.WorkspaceProfiles;

public interface IWorkspaceProfileService
{
  Task InitializeAsync(
    CancellationToken cancellationToken
  );

  Task<WorkspaceProfilesResponse> GetAllAsync(
    CancellationToken cancellationToken
  );

  Task<WorkspaceProfileData?> GetActiveDataAsync(
    CancellationToken cancellationToken
  );

  Task<WorkspaceProfileView> CreateAsync(
    string name,
    string path,
    CancellationToken cancellationToken
  );

  Task<WorkspaceProfileView> RenameAsync(
    string id,
    string name,
    CancellationToken cancellationToken
  );

  Task<WorkspaceProfileView> ActivateAsync(
    string id,
    CancellationToken cancellationToken
  );

  Task RemoveAsync(
    string id,
    CancellationToken cancellationToken
  );

  Task<WorkspaceProfileView> SetHistoryEnabledAsync(
    string id,
    bool enabled,
    CancellationToken cancellationToken
  );

  Task UpdateProjectProfileAsync(
    ProjectProfile profile,
    CancellationToken cancellationToken
  );

  Task UpdateValidationProfileAsync(
    ValidationProfileSettings? profile,
    CancellationToken cancellationToken
  );

  Task UpdateDefaultModelAsync(
    string model,
    CancellationToken cancellationToken
  );
}

public sealed class WorkspaceProfileService : IWorkspaceProfileService
{
  private readonly IWorkspaceProfileStore _store;
  private readonly ISettingsStore _settings;
  private readonly IExecutionSessionStore _executionSessions;
  private readonly IApprovalCoordinator _approvals;
  private readonly IRecoveryDecisionCoordinator _recoveryDecisions;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public WorkspaceProfileService(
    IWorkspaceProfileStore store,
    ISettingsStore settings,
    IExecutionSessionStore executionSessions,
    IApprovalCoordinator approvals,
    IRecoveryDecisionCoordinator recoveryDecisions
  )
  {
    _store = store;
    _settings = settings;
    _executionSessions = executionSessions;
    _approvals = approvals;
    _recoveryDecisions = recoveryDecisions;
  }

  public async Task InitializeAsync(
    CancellationToken cancellationToken
  )
  {
    var document = await _store.ReadAsync(
      cancellationToken
    );

    if (document.Profiles.Count > 0)
    {
      return;
    }

    var settings = await _settings.GetAsync(
      cancellationToken
    );

    if (string.IsNullOrWhiteSpace(
      settings.TrustedWorkspacePath
    ))
    {
      return;
    }

    var validation = WorkspacePathValidator.Inspect(
      settings.TrustedWorkspacePath
    );

    if (!validation.Valid || validation.Path is null)
    {
      throw new WorkspaceProfileException(
        "workspace-migration-failed",
        "workspace-migration",
        validation.Diagnostic
          ?? "The existing trusted workspace could not be migrated.",
        true
      );
    }

    var now = DateTimeOffset.UtcNow;
    var profile = new WorkspaceProfileData
    {
      Id = Guid.NewGuid().ToString(
        "N"
      ),
      Name = Path.GetFileName(
        validation.Path
      ),
      Path = validation.Path,
      Active = true,
      HistoryEnabled = false,
      CreatedAt = now,
      LastOpenedAt = now,
      DefaultModel = settings.DefaultModel,
      ValidationProfile = settings.ValidationProfile
    };
    await _store.WriteAsync(
      new WorkspaceProfileDocument
      {
        Profiles =
        [
          profile
        ]
      },
      cancellationToken
    );
  }

  public async Task<WorkspaceProfilesResponse> GetAllAsync(
    CancellationToken cancellationToken
  )
  {
    await InitializeAsync(
      cancellationToken
    );
    var document = await _store.ReadAsync(
      cancellationToken
    );
    var profiles = document.Profiles.Select(
      ToView
    ).ToArray();
    return new WorkspaceProfilesResponse(
      document.SchemaVersion,
      profiles,
      profiles.SingleOrDefault(
        profile => profile.Active
      )?.Id
    );
  }

  public async Task<WorkspaceProfileData?> GetActiveDataAsync(
    CancellationToken cancellationToken
  )
  {
    await InitializeAsync(
      cancellationToken
    );
    return (
      await _store.ReadAsync(
        cancellationToken
      )
    ).Profiles.SingleOrDefault(
      profile => profile.Active
    );
  }

  public async Task<WorkspaceProfileView> CreateAsync(
    string name,
    string path,
    CancellationToken cancellationToken
  )
  {
    var canonical = RequirePath(
      path
    );
    var displayName = RequireName(
      name
    );
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var document = await _store.ReadAsync(
        cancellationToken
      );
      EnsureUnique(
        document,
        canonical,
        null
      );
      var now = DateTimeOffset.UtcNow;
      var profile = new WorkspaceProfileData
      {
        Id = Guid.NewGuid().ToString(
          "N"
        ),
        Name = displayName,
        Path = canonical,
        Active = document.Profiles.Count == 0,
        HistoryEnabled = false,
        CreatedAt = now,
        LastOpenedAt = now
      };
      await _store.WriteAsync(
        document with
        {
          Profiles = document.Profiles.Append(
            profile
          ).ToArray()
        },
        cancellationToken
      );
      if (profile.Active)
      {
        var settings = await _settings.GetAsync(
          cancellationToken
        );
        await _settings.SaveAsync(
          settings with
          {
            TrustedWorkspacePath = profile.Path,
            ValidationProfile = profile.ValidationProfile
          },
          cancellationToken
        );
        _approvals.InvalidateAll();
        _recoveryDecisions.InvalidateAll();
      }

      return ToView(
        profile
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public Task<WorkspaceProfileView> RenameAsync(
    string id,
    string name,
    CancellationToken cancellationToken
  )
  {
    var displayName = RequireName(
      name
    );
    return UpdateAsync(
      id,
      profile => profile with
      {
        Name = displayName
      },
      cancellationToken
    );
  }

  public async Task<WorkspaceProfileView> ActivateAsync(
    string id,
    CancellationToken cancellationToken
  )
  {
    if (_executionSessions.HasActiveSession())
    {
      throw new WorkspaceProfileException(
        "workspace-activation-blocked",
        "workspace-activation",
        "Finish or cancel the active Execute session before switching workspaces.",
        true
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var document = await _store.ReadAsync(
        cancellationToken
      );
      var selected = Find(
        document,
        id
      );
      var canonical = RequirePath(
        selected.Path
      );
      var now = DateTimeOffset.UtcNow;
      var profiles = document.Profiles.Select(
        profile => profile with
        {
          Active = string.Equals(
            profile.Id,
            id,
            StringComparison.Ordinal
          ),
          LastOpenedAt = string.Equals(
            profile.Id,
            id,
            StringComparison.Ordinal
          )
            ? now
            : profile.LastOpenedAt,
          Path = string.Equals(
            profile.Id,
            id,
            StringComparison.Ordinal
          )
            ? canonical
            : profile.Path
        }
      ).ToArray();
      var active = profiles.Single(
        profile => profile.Active
      );
      var settings = await _settings.GetAsync(
        cancellationToken
      );
      var save = await _settings.SaveAsync(
        settings with
        {
          TrustedWorkspacePath = active.Path,
          ValidationProfile = active.ValidationProfile,
          DefaultModel = string.IsNullOrWhiteSpace(
            active.DefaultModel
          )
            ? settings.DefaultModel
            : active.DefaultModel
        },
        cancellationToken
      );

      if (!save.IsValid)
      {
        throw new WorkspaceProfileException(
          "workspace-profile-unavailable",
          "workspace-activation",
          "Workspace preferences could not be activated.",
          true
        );
      }

      await _store.WriteAsync(
        document with
        {
          Profiles = profiles
        },
        cancellationToken
      );
      _approvals.InvalidateAll();
      _recoveryDecisions.InvalidateAll();
      return ToView(
        active
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task RemoveAsync(
    string id,
    CancellationToken cancellationToken
  )
  {
    if (_executionSessions.HasActiveSession())
    {
      throw new WorkspaceProfileException(
        "workspace-activation-blocked",
        "workspace-removal",
        "Finish or cancel the active Execute session before removing a workspace profile.",
        true
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var document = await _store.ReadAsync(
        cancellationToken
      );
      var removed = Find(
        document,
        id
      );
      var remaining = document.Profiles.Where(
        profile => !string.Equals(
          profile.Id,
          id,
          StringComparison.Ordinal
        )
      ).ToArray();

      if (removed.Active && remaining.Length > 0)
      {
        remaining[0] = remaining[0] with
        {
          Active = true,
          LastOpenedAt = DateTimeOffset.UtcNow
        };
      }

      var sessionRoot = Path.Combine(
        _store.DataDirectory,
        "workspaces",
        id
      );

      if (Directory.Exists(
        sessionRoot
      ))
      {
        Directory.Delete(
          sessionRoot,
          true
        );
      }

      await _store.WriteAsync(
        document with
        {
          Profiles = remaining
        },
        cancellationToken
      );
      var settings = await _settings.GetAsync(
        cancellationToken
      );
      var newActive = remaining.SingleOrDefault(
        profile => profile.Active
      );
      await _settings.SaveAsync(
        settings with
        {
          TrustedWorkspacePath = newActive?.Path,
          ValidationProfile = newActive?.ValidationProfile,
          DefaultModel = newActive?.DefaultModel
            ?? settings.DefaultModel
        },
        cancellationToken
      );
      _approvals.InvalidateAll();
      _recoveryDecisions.InvalidateAll();
    }
    finally
    {
      _gate.Release();
    }
  }

  public Task<WorkspaceProfileView> SetHistoryEnabledAsync(
    string id,
    bool enabled,
    CancellationToken cancellationToken
  )
  {
    return UpdateAsync(
      id,
      profile => profile with
      {
        HistoryEnabled = enabled
      },
      cancellationToken
    );
  }

  public async Task UpdateProjectProfileAsync(
    ProjectProfile profile,
    CancellationToken cancellationToken
  )
  {
    var active = await GetActiveDataAsync(
      cancellationToken
    );

    if (active is not null)
    {
      await UpdateAsync(
        active.Id,
        current => current with
        {
          ProjectProfile = profile
        },
        cancellationToken
      );
    }
  }

  public async Task UpdateValidationProfileAsync(
    ValidationProfileSettings? profile,
    CancellationToken cancellationToken
  )
  {
    var active = await GetActiveDataAsync(
      cancellationToken
    );

    if (active is not null)
    {
      await UpdateAsync(
        active.Id,
        current => current with
        {
          ValidationProfile = profile
        },
        cancellationToken
      );
    }
  }

  public async Task UpdateDefaultModelAsync(
    string model,
    CancellationToken cancellationToken
  )
  {
    var active = await GetActiveDataAsync(
      cancellationToken
    );

    if (active is not null)
    {
      await UpdateAsync(
        active.Id,
        current => current with
        {
          DefaultModel = model
        },
        cancellationToken
      );
    }
  }

  private async Task<WorkspaceProfileView> UpdateAsync(
    string id,
    Func<WorkspaceProfileData, WorkspaceProfileData> update,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var document = await _store.ReadAsync(
        cancellationToken
      );
      var current = Find(
        document,
        id
      );
      var updated = update(
        current
      );
      await _store.WriteAsync(
        document with
        {
          Profiles = document.Profiles.Select(
            profile => string.Equals(
              profile.Id,
              id,
              StringComparison.Ordinal
            )
              ? updated
              : profile
          ).ToArray()
        },
        cancellationToken
      );
      return ToView(
        updated
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private static WorkspaceProfileData Find(
    WorkspaceProfileDocument document,
    string id
  )
  {
    return document.Profiles.FirstOrDefault(
      profile => string.Equals(
        profile.Id,
        id,
        StringComparison.Ordinal
      )
    ) ?? throw new WorkspaceProfileException(
      "workspace-profile-unavailable",
      "workspace-profile",
      "The workspace profile was not found.",
      false
    );
  }

  private static void EnsureUnique(
    WorkspaceProfileDocument document,
    string canonicalPath,
    string? exceptId
  )
  {
    if (document.Profiles.Any(
      profile => !string.Equals(
        profile.Id,
        exceptId,
        StringComparison.Ordinal
      ) && string.Equals(
        NormalizeComparisonPath(
          profile.Path
        ),
        NormalizeComparisonPath(
          canonicalPath
        ),
        FileSystemPathSemantics.Comparison
      )
    ))
    {
      throw new WorkspaceProfileException(
        "duplicate-workspace-path",
        "workspace-validation",
        "A workspace profile already uses this canonical directory.",
        false
      );
    }
  }

  private static string RequireName(
    string name
  )
  {
    var value = name.Trim();

    if (value.Length is < 1 or > 80)
    {
      throw new WorkspaceProfileException(
        "workspace-name-invalid",
        "workspace-validation",
        "Workspace name must contain between 1 and 80 characters.",
        false
      );
    }

    return value;
  }

  private static string RequirePath(
    string path
  )
  {
    var validation = WorkspacePathValidator.Inspect(
      path
    );

    if (!validation.Valid || validation.Path is null)
    {
      throw new WorkspaceProfileException(
        "workspace-profile-unavailable",
        "workspace-validation",
        validation.Diagnostic
          ?? "The workspace directory is unavailable.",
        true
      );
    }

    return validation.Path;
  }

  private static string NormalizeComparisonPath(
    string path
  )
  {
    return Path.GetFullPath(
      path
    ).TrimEnd(
      Path.DirectorySeparatorChar,
      Path.AltDirectorySeparatorChar
    );
  }

  private static WorkspaceProfileView ToView(
    WorkspaceProfileData profile
  )
  {
    var validation = WorkspacePathValidator.Inspect(
      profile.Path
    );
    return new WorkspaceProfileView(
      profile.Id,
      profile.Name,
      validation.Path
        ?? profile.Path,
      profile.Active,
      profile.HistoryEnabled,
      profile.CreatedAt,
      profile.LastOpenedAt,
      profile.ProjectProfile,
      profile.DefaultModel,
      profile.ValidationProfile,
      validation.Valid,
      validation.Diagnostic,
      profile.PreferredModelProfileId
    );
  }
}

public static class WorkspacePathValidator
{
  public static TrustedWorkspaceStatus Inspect(
    string? path
  )
  {
    if (string.IsNullOrWhiteSpace(
      path
    ))
    {
      return new TrustedWorkspaceStatus(
        false,
        false,
        null,
        "Not configured",
        null
      );
    }

    try
    {
      if (path.StartsWith(
        @"\\",
        StringComparison.Ordinal
      ))
      {
        return Invalid(
          path,
          "Network paths cannot be trusted workspaces."
        );
      }

      var fullPath = Path.GetFullPath(
        path
      ).TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      );
      var root = Path.GetPathRoot(
        fullPath
      )?.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      );

      if (
        OperatingSystem.IsWindows()
        && !string.IsNullOrWhiteSpace(
          root
        )
        && new DriveInfo(
          root
        ).DriveType == DriveType.Network
      )
      {
        return Invalid(
          fullPath,
          "Network paths cannot be trusted workspaces."
        );
      }

      if (string.Equals(
        fullPath,
        root,
        FileSystemPathSemantics.Comparison
      ))
      {
        return Invalid(
          fullPath,
          "Filesystem roots cannot be trusted workspaces."
        );
      }

      if (!Directory.Exists(
        fullPath
      ))
      {
        return Invalid(
          fullPath,
          "The configured directory does not exist."
        );
      }

      if ((
        File.GetAttributes(
          fullPath
        )
        & FileAttributes.ReparsePoint
      ) != 0)
      {
        return Invalid(
          fullPath,
          "Reparse points cannot be used as a trusted workspace."
        );
      }

      _ = Directory.EnumerateFileSystemEntries(
        fullPath
      ).Take(
        1
      ).ToArray();
      return new TrustedWorkspaceStatus(
        true,
        true,
        fullPath,
        "Configured",
        null
      );
    }
    catch (Exception exception) when (
      exception is ArgumentException
      or NotSupportedException
      or PathTooLongException
      or IOException
      or UnauthorizedAccessException
    )
    {
      return Invalid(
        path,
        "The configured directory is invalid or inaccessible."
      );
    }
  }

  private static TrustedWorkspaceStatus Invalid(
    string path,
    string diagnostic
  )
  {
    return new TrustedWorkspaceStatus(
      true,
      false,
      path,
      "Invalid",
      diagnostic
    );
  }
}
