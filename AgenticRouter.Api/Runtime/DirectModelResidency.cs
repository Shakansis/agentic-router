using System.Runtime.CompilerServices;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Runtime;

// Called while the Direct turn owns the request tracker's exclusive lease.
public sealed class DirectModelResidency(IOllamaManagedServerManager servers)
{
  private readonly Dictionary<Uri, HashSet<string>> _externalModels = [];

  public void ObserveSelection(OllamaEndpointResolution target, string model)
  {
    if (target.Managed) return;
    // Supervised turns may register concurrently. Direct cleanup owns admission.
    lock (_externalModels)
    {
      if (!_externalModels.TryGetValue(target.Endpoint, out var models))
        _externalModels[target.Endpoint] = models = new HashSet<string>(StringComparer.Ordinal);
      models.Add(model);
    }
  }

  public async IAsyncEnumerable<(string Stage, string Model)> PrepareAsync(
    IOllamaClient client,
    OllamaEndpointResolution target,
    string model,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var managedEndpoints = servers.GetActiveServers().Select(server => server.Endpoint).ToHashSet();
    var endpoints = managedEndpoints
      .Concat(_externalModels.Keys).Distinct().ToArray();
    foreach (var endpoint in endpoints)
    {
      var managed = managedEndpoints.Contains(endpoint);
      var running = await client.GetRunningModelsAsync(endpoint, cancellationToken);
      if (!managed)
        _externalModels[endpoint].IntersectWith(running.Select(candidate => candidate.Name));
      foreach (var previous in running)
      {
        if (!managed && !_externalModels[endpoint].Contains(previous.Name)) continue;
        if (endpoint == target.Endpoint && previous.Name == model) continue;

        yield return ("runtime.model-unloading", previous.Name);
        await client.UnloadModelAtEndpointAsync(endpoint, previous.Name, cancellationToken);
        await VerifyUnloadAsync(client, endpoint, previous.Name, cancellationToken);
        if (!managed) _externalModels[endpoint].Remove(previous.Name);
        yield return ("runtime.model-unloaded", previous.Name);
      }
      if (!managed && _externalModels[endpoint].Count == 0) _externalModels.Remove(endpoint);
    }

    ObserveSelection(target, model);
  }

  private static async Task VerifyUnloadAsync(
    IOllamaClient client, Uri endpoint, string model, CancellationToken cancellationToken)
  {
    using var verification = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    verification.CancelAfter(TimeSpan.FromSeconds(10));
    try
    {
      while ((await client.GetRunningModelsAsync(endpoint, verification.Token))
        .Any(candidate => candidate.Name == model))
        await Task.Delay(100, verification.Token);
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
      throw new OllamaProviderException(
        "model-unload-unverified",
        "The previous model's unload could not be verified. The selected model was not loaded.",
        $"Direct model switch could not verify unloading '{model}'.",
        409,
        true,
        exception
      );
    }
  }
}
