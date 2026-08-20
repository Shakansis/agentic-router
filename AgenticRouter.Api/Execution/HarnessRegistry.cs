namespace AgenticRouter.Api.Execution;

public interface IHarnessRegistry
{
  IReadOnlyList<HarnessDefinition> Definitions { get; }

  bool TryGetDefinition(string harnessId, out HarnessDefinition definition);

  bool TryGetAdapter(string harnessId, out IAgentHarness adapter);

  Task<IReadOnlyList<HarnessStatus>> DiscoverAsync(
    CancellationToken cancellationToken
  );
}

public sealed class HarnessRegistry : IHarnessRegistry
{
  private static readonly HarnessDefinition NativeDefinition = new(
    HarnessIds.Native,
    "Native",
    false,
    "Agentic Router's built-in specialist and Host tool loop.",
    new HarnessCapabilities(
      SupportsStreaming: true,
      SupportsThinking: true,
      SupportsResume: false,
      SupportsCancel: true,
      SupportsApprovals: true,
      SupportsToolEvents: true,
      SupportsStructuredEdits: true,
      SupportsStaleProtection: true,
      SupportsSubagents: false,
      SupportsSandbox: false,
      SupportsSessionDiff: true,
      SupportsNativePermissions: false
    )
  );

  private readonly IReadOnlyDictionary<string, HarnessDefinition> _definitions;
  private readonly IReadOnlyDictionary<string, IAgentHarness> _adapters;

  public HarnessRegistry(IEnumerable<IAgentHarness> adapters)
  {
    var adapterMap = new Dictionary<string, IAgentHarness>(
      StringComparer.OrdinalIgnoreCase
    );
    var definitions = new Dictionary<string, HarnessDefinition>(
      StringComparer.OrdinalIgnoreCase
    )
    {
      [NativeDefinition.Id] = NativeDefinition
    };

    foreach (var adapter in adapters)
    {
      if (!adapterMap.TryAdd(adapter.Definition.Id, adapter))
      {
        throw new InvalidOperationException(
          $"Duplicate harness adapter id '{adapter.Definition.Id}'."
        );
      }
      if (!definitions.TryAdd(adapter.Definition.Id, adapter.Definition))
      {
        throw new InvalidOperationException(
          $"Duplicate harness definition id '{adapter.Definition.Id}'."
        );
      }
    }

    _adapters = adapterMap;
    _definitions = definitions;
    Definitions = definitions.Values
      .OrderBy(definition => definition.Id == HarnessIds.Native ? 0 : 1)
      .ThenBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  public IReadOnlyList<HarnessDefinition> Definitions { get; }

  public bool TryGetDefinition(
    string harnessId,
    out HarnessDefinition definition
  )
  {
    return _definitions.TryGetValue(harnessId, out definition!);
  }

  public bool TryGetAdapter(
    string harnessId,
    out IAgentHarness adapter
  )
  {
    return _adapters.TryGetValue(harnessId, out adapter!);
  }

  public async Task<IReadOnlyList<HarnessStatus>> DiscoverAsync(
    CancellationToken cancellationToken
  )
  {
    var statuses = new List<HarnessStatus>(Definitions.Count);
    foreach (var definition in Definitions)
    {
      if (!_adapters.TryGetValue(definition.Id, out var adapter))
      {
        statuses.Add(
          new HarnessStatus(
            definition,
            HarnessAvailability.Ready("built-in")
          )
        );
        continue;
      }

      HarnessAvailability availability;
      try
      {
        availability = await adapter.GetAvailabilityAsync(cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch
      {
        availability = HarnessAvailability.Missing(
          $"{definition.DisplayName} availability could not be determined."
        );
      }
      statuses.Add(new HarnessStatus(definition, availability));
    }
    return statuses;
  }
}
