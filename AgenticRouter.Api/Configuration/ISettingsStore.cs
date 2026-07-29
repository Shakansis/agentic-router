namespace AgenticRouter.Api.Configuration;

public interface ISettingsStore
{
    Task<ApplicationSettings> GetAsync(
      CancellationToken cancellationToken
    );

    Task<SettingsSaveResult> SaveAsync(
      ApplicationSettings settings,
      CancellationToken cancellationToken
    );
}

public sealed record SettingsSaveResult(
  bool IsValid,
  ApplicationSettings? Settings,
  IReadOnlyDictionary<string, string[]> Errors
);

public interface ISettingsValidator
{
    IReadOnlyDictionary<string, string[]> Validate(
      ApplicationSettings settings
    );
}
