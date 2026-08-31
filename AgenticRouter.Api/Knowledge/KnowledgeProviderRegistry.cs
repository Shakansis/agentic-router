namespace AgenticRouter.Api.Knowledge;

public interface IKnowledgeProviderRegistry
{
  IReadOnlyList<IKnowledgeProvider> Providers { get; }

  bool TryGet(
    string id,
    out IKnowledgeProvider provider
  );
}

public sealed class KnowledgeProviderRegistry : IKnowledgeProviderRegistry
{
  private readonly IReadOnlyDictionary<string, IKnowledgeProvider> _providers;

  public KnowledgeProviderRegistry(
    IEnumerable<IKnowledgeProvider> providers
  )
  {
    _providers = providers.ToDictionary(
      provider => provider.Definition.Id,
      StringComparer.Ordinal
    );
    Providers = _providers.Values.OrderBy(
      provider => provider.Definition.DisplayName,
      StringComparer.Ordinal
    ).ToArray();
  }

  public IReadOnlyList<IKnowledgeProvider> Providers { get; }

  public bool TryGet(
    string id,
    out IKnowledgeProvider provider
  )
  {
    return _providers.TryGetValue(
      id,
      out provider!
    );
  }
}
