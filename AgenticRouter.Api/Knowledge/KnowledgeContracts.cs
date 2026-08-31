namespace AgenticRouter.Api.Knowledge;

public static class KnowledgeProviderIds
{
  public const string AnythingLlm = "anythingllm";
}

public sealed record KnowledgeProviderDefinition(
  string Id,
  string DisplayName
);

public sealed record KnowledgeProviderAvailability(
  bool Configured,
  bool Available,
  string? Diagnostic
);

public sealed record KnowledgeLibrary(
  string Id,
  string Name
);

public sealed record KnowledgeRetrievalRequest(
  string Query,
  IReadOnlyList<string> LibraryIds
);

public sealed record KnowledgeChunk(
  string LibraryId,
  string LibraryName,
  string Text,
  string? Title,
  string? Source,
  double? Score
);

public sealed record KnowledgeRetrievalResult(
  IReadOnlyList<KnowledgeChunk> Chunks
);

public interface IKnowledgeProvider
{
  KnowledgeProviderDefinition Definition { get; }

  ValueTask<KnowledgeProviderAvailability> GetAvailabilityAsync(
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<KnowledgeLibrary>> ListLibrariesAsync(
    CancellationToken cancellationToken
  );

  Task<KnowledgeRetrievalResult> RetrieveAsync(
    KnowledgeRetrievalRequest request,
    CancellationToken cancellationToken
  );
}

public sealed class KnowledgeProviderException : Exception
{
  public KnowledgeProviderException(
    string code,
    string stage,
    string provider,
    string message,
    bool retryable,
    int? httpStatus = null,
    Exception? innerException = null
  ) : base(message, innerException)
  {
    Code = code;
    Stage = stage;
    Provider = provider;
    Retryable = retryable;
    HttpStatus = httpStatus;
  }

  public string Code { get; }

  public string Stage { get; }

  public string Provider { get; }

  public bool Retryable { get; }

  public int? HttpStatus { get; }
}
