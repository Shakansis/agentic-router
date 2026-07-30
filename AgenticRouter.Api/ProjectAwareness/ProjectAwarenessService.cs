using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.ProjectAwareness;

public interface IProjectAwarenessService
{
  Task<ProjectProfile> GetAsync(
    bool refresh,
    CancellationToken cancellationToken
  );
}

public interface IRepositoryInstructionService
{
  Task<RepositoryInstructionSet> ResolveAsync(
    string? relativeTargetPath,
    CancellationToken cancellationToken
  );
}

public sealed class ProjectAwarenessService : IProjectAwarenessService
{
  private static readonly HashSet<string> IgnoredDirectories = new(
    [
      ".git",
      "bin",
      "obj",
      "node_modules"
    ],
    StringComparer.OrdinalIgnoreCase
  );
  private static readonly string[] RootNames =
  [
    "AGENTS.md",
    ".editorconfig",
    "package.json",
    "playwright.config.js",
    "playwright.config.mjs",
    "playwright.config.cjs",
    "playwright.runsettings",
    "Directory.Build.props",
    "Directory.Build.targets",
    "global.json"
  ];

  private readonly ISettingsStore _settingsStore;
  private readonly ITrustedWorkspaceService _workspace;
  private readonly IGitRepositoryService _git;
  private readonly IWorkspaceProfileService _workspaceProfiles;

  public ProjectAwarenessService(
    ISettingsStore settingsStore,
    ITrustedWorkspaceService workspace,
    IGitRepositoryService git,
    IWorkspaceProfileService workspaceProfiles
  )
  {
    _settingsStore = settingsStore;
    _workspace = workspace;
    _git = git;
    _workspaceProfiles = workspaceProfiles;
  }

  public async Task<ProjectProfile> GetAsync(
    bool refresh,
    CancellationToken cancellationToken
  )
  {
    _ = refresh;
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var status = await _workspace.GetStatusAsync(
      cancellationToken
    );

    if (!status.Valid || status.Path is null)
    {
      return Unavailable(
        status.Diagnostic
          ?? "Configure a valid trusted workspace to inspect the project."
      );
    }

    var detectedFiles = new List<string>();
    var instructionFiles = new List<string>();
    var diagnostics = new List<string>();
    var truncated = false;

    try
    {
      DiscoverMarkers(
        status.Path,
        settings.ProjectAwareness.MaxProjectMarkers,
        detectedFiles,
        instructionFiles,
        diagnostics,
        ref truncated,
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      diagnostics.Add(
        $"Project marker discovery was partial: {exception.Message}"
      );
    }

    var repository = await DetectRepositoryAsync(
      status.Path,
      cancellationToken
    );
    var projectTypes = DetectProjectTypes(
      detectedFiles
    );
    var detectedValidation = CreateDetectedValidationProfile(
      detectedFiles,
      projectTypes
    );
    var activeWorkspace = await _workspaceProfiles.GetActiveDataAsync(
      cancellationToken
    );
    var activeValidation = activeWorkspace?.ValidationProfile
      ?? settings.ValidationProfile;
    var selectedValidation = activeValidation is null
      ? detectedValidation is null
        ? null
        : new ValidationProfileReference(
          detectedValidation.Name,
          "detected"
        )
      : new ValidationProfileReference(
        activeValidation.Name,
        "user"
      );

    if (truncated)
    {
      diagnostics.Add(
        $"Project marker discovery stopped at the configured limit of {settings.ProjectAwareness.MaxProjectMarkers} markers."
      );
    }

    var profile = new ProjectProfile(
      status.Path,
      Path.GetFileName(
        status.Path.TrimEnd(
          Path.DirectorySeparatorChar,
          Path.AltDirectorySeparatorChar
        )
      ),
      repository,
      projectTypes,
      detectedFiles,
      instructionFiles,
      selectedValidation,
      detectedValidation,
      diagnostics.Count == 0
        ? "available"
        : "partial",
      diagnostics.Count == 0
        ? null
        : string.Join(
          " ",
          diagnostics
        ),
      truncated
    );
    await _workspaceProfiles.UpdateProjectProfileAsync(
      profile,
      cancellationToken
    );
    return profile;
  }

  private static void DiscoverMarkers(
    string root,
    int maximumMarkers,
    ICollection<string> detectedFiles,
    ICollection<string> instructionFiles,
    ICollection<string> diagnostics,
    ref bool truncated,
    CancellationToken cancellationToken
  )
  {
    if (Directory.Exists(
      Path.Combine(
        root,
        ".git"
      )
    ) || File.Exists(
      Path.Combine(
        root,
        ".git"
      )
    ))
    {
      detectedFiles.Add(
        ".git"
      );
    }

    var directories = new Queue<(
      string Path,
      int Depth
    )>();
    directories.Enqueue(
      (
        root,
        0
      )
    );
    var visitedDirectories = 0;

    while (
      directories.Count > 0
      && detectedFiles.Count < maximumMarkers
      && visitedDirectories < 500
    )
    {
      cancellationToken.ThrowIfCancellationRequested();
      var current = directories.Dequeue();
      visitedDirectories++;
      IEnumerable<string> entries;

      try
      {
        entries = Directory.EnumerateFileSystemEntries(
          current.Path
        );
      }
      catch (Exception exception) when (
        exception is IOException
        or UnauthorizedAccessException
      )
      {
        diagnostics.Add(
          $"{Relative(root, current.Path)}: {exception.Message}"
        );
        continue;
      }

      foreach (var entry in entries)
      {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes;

        try
        {
          attributes = File.GetAttributes(
            entry
          );
        }
        catch (Exception exception) when (
          exception is IOException
          or UnauthorizedAccessException
        )
        {
          diagnostics.Add(
            $"{Relative(root, entry)}: {exception.Message}"
          );
          continue;
        }

        if ((
          attributes
          & FileAttributes.ReparsePoint
        ) != 0)
        {
          continue;
        }

        if ((
          attributes
          & FileAttributes.Directory
        ) != 0)
        {
          if (
            current.Depth < 4
            && !IgnoredDirectories.Contains(
              Path.GetFileName(
                entry
              )
            )
          )
          {
            directories.Enqueue(
              (
                entry,
                current.Depth + 1
              )
            );
          }

          continue;
        }

        var fileName = Path.GetFileName(
          entry
        );

        if (!IsMarker(
          fileName
        ))
        {
          continue;
        }

        var relative = Relative(
          root,
          entry
        );
        detectedFiles.Add(
          relative
        );

        if (fileName.Equals(
          "AGENTS.md",
          StringComparison.OrdinalIgnoreCase
        ))
        {
          instructionFiles.Add(
            relative
          );
        }

        if (detectedFiles.Count >= maximumMarkers)
        {
          truncated = true;
          break;
        }
      }
    }

    if (directories.Count > 0 || visitedDirectories >= 500)
    {
      truncated = true;
    }
  }

  private async Task<ProjectRepositoryProfile> DetectRepositoryAsync(
    string root,
    CancellationToken cancellationToken
  )
  {
    if (
      !Directory.Exists(
        Path.Combine(
          root,
          ".git"
        )
      )
      && !File.Exists(
        Path.Combine(
          root,
          ".git"
        )
      )
    )
    {
      return EmptyRepository();
    }

    try
    {
      var status = await _git.GetStatusAsync(
        root,
        false,
        cancellationToken
      );
      var dirtyPaths = status.Paths.Select(
        path => path.Path
      ).Distinct(
        StringComparer.OrdinalIgnoreCase
      ).Take(
        500
      ).ToArray();

      return new ProjectRepositoryProfile(
        true,
        status.RepositoryRoot,
        status.Branch,
        dirtyPaths.Length > 0,
        dirtyPaths,
        status
      );
    }
    catch (GitDeliveryException exception)
    {
      return new ProjectRepositoryProfile(
        true,
        ".",
        null,
        false,
        [],
        new GitRepositoryStatusView(
          false,
          exception.Message,
          null,
          null,
          null,
          null,
          null,
          0,
          0,
          false,
          null,
          [],
          [],
          [],
          [],
          [],
          [],
          false,
          false,
          DateTimeOffset.UtcNow
        )
      );
    }
  }

  private static IReadOnlyList<string> DetectProjectTypes(
    IReadOnlyCollection<string> markers
  )
  {
    var types = new List<string>();

    if (markers.Any(
      marker => marker.EndsWith(
        ".sln",
        StringComparison.OrdinalIgnoreCase
      ) || marker.EndsWith(
        ".slnx",
        StringComparison.OrdinalIgnoreCase
      ) || marker.EndsWith(
        ".csproj",
        StringComparison.OrdinalIgnoreCase
      )
    ))
    {
      types.Add(
        "dotnet"
      );
    }

    if (markers.Any(
      marker => Path.GetFileName(
        marker
      ).Equals(
        "package.json",
        StringComparison.OrdinalIgnoreCase
      )
    ))
    {
      types.Add(
        "node"
      );
    }

    if (markers.Any(
      marker => Path.GetFileName(
        marker
      ).StartsWith(
        "playwright.",
        StringComparison.OrdinalIgnoreCase
      )
    ))
    {
      types.Add(
        "playwright"
      );
    }

    if (markers.Any(
      marker => Path.GetFileName(
        marker
      ).Equals(
        "index.html",
        StringComparison.OrdinalIgnoreCase
      )
    ))
    {
      types.Add(
        "vanilla-web"
      );
    }

    return types;
  }

  private static ValidationProfileSettings? CreateDetectedValidationProfile(
    IReadOnlyCollection<string> markers,
    IReadOnlyCollection<string> projectTypes
  )
  {
    if (!projectTypes.Contains(
      "dotnet",
      StringComparer.Ordinal
    ))
    {
      return null;
    }

    var solution = markers.FirstOrDefault(
      marker => marker.EndsWith(
        ".slnx",
        StringComparison.OrdinalIgnoreCase
      ) || marker.EndsWith(
        ".sln",
        StringComparison.OrdinalIgnoreCase
      )
    );
    var target = solution is null
      ? Array.Empty<string>()
      :
      [
        solution
      ];
    var steps = new List<ValidationStepSettings>
    {
      new()
      {
        Id = "format",
        Label = "Check formatting",
        Executable = "dotnet",
        Arguments =
        [
          "format",
          .. target,
          "--verify-no-changes"
        ],
        WorkingDirectory = ".",
        TimeoutSeconds = 120,
        Required = true
      },
      new()
      {
        Id = "build",
        Label = "Build Release",
        Executable = "dotnet",
        Arguments =
        [
          "build",
          .. target,
          "-c",
          "Release"
        ],
        WorkingDirectory = ".",
        TimeoutSeconds = 120,
        Required = true
      }
    };
    var testProject = markers.FirstOrDefault(
      marker => marker.EndsWith(
        ".csproj",
        StringComparison.OrdinalIgnoreCase
      ) && marker.Contains(
        "EndToEndTests",
        StringComparison.OrdinalIgnoreCase
      )
    );

    if (
      projectTypes.Contains(
        "playwright",
        StringComparer.Ordinal
      )
      && testProject is not null
    )
    {
      steps.Add(
        new ValidationStepSettings
        {
          Id = "e2e",
          Label = "Run Playwright E2E",
          Executable = "dotnet",
          Arguments =
          [
            "test",
            testProject,
            "-c",
            "Release"
          ],
          WorkingDirectory = ".",
          TimeoutSeconds = 120,
          Required = true
        }
      );
    }

    return new ValidationProfileSettings
    {
      Name = projectTypes.Contains(
        "playwright",
        StringComparer.Ordinal
      )
        ? "Detected .NET and Playwright"
        : "Detected .NET",
      Source = "detected",
      Steps = steps
    };
  }

  private static bool IsMarker(
    string fileName
  )
  {
    return RootNames.Contains(
      fileName,
      StringComparer.OrdinalIgnoreCase
    ) || fileName.EndsWith(
      ".sln",
      StringComparison.OrdinalIgnoreCase
    ) || fileName.EndsWith(
      ".slnx",
      StringComparison.OrdinalIgnoreCase
    ) || fileName.EndsWith(
      ".csproj",
      StringComparison.OrdinalIgnoreCase
    ) || fileName.StartsWith(
      "README",
      StringComparison.OrdinalIgnoreCase
    ) || fileName.Equals(
      "index.html",
      StringComparison.OrdinalIgnoreCase
    );
  }

  private static string Relative(
    string root,
    string path
  )
  {
    var relative = Path.GetRelativePath(
      root,
      path
    );
    return relative == "."
      ? "."
      : relative.Replace(
        '\\',
        '/'
      );
  }

  private static ProjectRepositoryProfile EmptyRepository()
  {
    return new ProjectRepositoryProfile(
      false,
      null,
      null,
      false,
      []
    );
  }

  private static ProjectProfile Unavailable(
    string diagnostic
  )
  {
    return new ProjectProfile(
      null,
      null,
      EmptyRepository(),
      [],
      [],
      [],
      null,
      null,
      "unavailable",
      diagnostic,
      false
    );
  }
}

public sealed class RepositoryInstructionService : IRepositoryInstructionService
{
  private readonly ISettingsStore _settingsStore;
  private readonly ITrustedWorkspaceService _workspace;

  public RepositoryInstructionService(
    ISettingsStore settingsStore,
    ITrustedWorkspaceService workspace
  )
  {
    _settingsStore = settingsStore;
    _workspace = workspace;
  }

  public async Task<RepositoryInstructionSet> ResolveAsync(
    string? relativeTargetPath,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var root = await _workspace.ResolvePathAsync(
      null,
      cancellationToken
    );
    var target = await _workspace.ResolvePathAsync(
      relativeTargetPath,
      cancellationToken
    );
    var targetDirectory = File.Exists(
      target
    ) || Path.HasExtension(
      target
    )
      ? Path.GetDirectoryName(
        target
      ) ?? root
      : target;
    var directories = new Stack<string>();
    var current = targetDirectory;

    while (true)
    {
      directories.Push(
        current
      );

      if (string.Equals(
        current,
        root,
        StringComparison.OrdinalIgnoreCase
      ))
      {
        break;
      }

      var parent = Directory.GetParent(
        current
      )?.FullName;

      if (
        parent is null
        || !parent.StartsWith(
          root,
          StringComparison.OrdinalIgnoreCase
        )
      )
      {
        throw new LocalActionException(
          "repository-instructions",
          "Instruction resolution escaped the trusted workspace."
        );
      }

      current = parent;
    }

    var applied = new List<string>();
    var content = new StringBuilder();
    var remaining = settings.ProjectAwareness.MaxInstructionBytes;
    var truncated = false;
    string? diagnostic = null;

    foreach (var directory in directories)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var candidate = Path.Combine(
        directory,
        "AGENTS.md"
      );

      if (!File.Exists(
        candidate
      ))
      {
        continue;
      }

      if ((
        File.GetAttributes(
          candidate
        )
        & FileAttributes.ReparsePoint
      ) != 0)
      {
        diagnostic = "A repository instruction file was ignored because it is a reparse point.";
        continue;
      }

      var bytes = await File.ReadAllBytesAsync(
        candidate,
        cancellationToken
      );
      var take = Math.Min(
        bytes.Length,
        remaining
      );
      var relative = Path.GetRelativePath(
        root,
        candidate
      ).Replace(
        '\\',
        '/'
      );
      applied.Add(
        relative
      );
      content.Append(
        "\nREPOSITORY_INSTRUCTIONS "
      ).AppendLine(
        relative
      ).AppendLine(
        Encoding.UTF8.GetString(
          bytes,
          0,
          take
        )
      );
      remaining -= take;

      if (take < bytes.Length || remaining == 0)
      {
        truncated = true;
        diagnostic = $"Repository instructions were truncated at {settings.ProjectAwareness.MaxInstructionBytes} bytes.";
        break;
      }
    }

    return new RepositoryInstructionSet(
      applied,
      content.ToString().Trim(),
      truncated,
      diagnostic
    );
  }
}
