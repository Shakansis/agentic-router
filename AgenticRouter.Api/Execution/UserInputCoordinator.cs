using System.Text.Json;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public sealed record UserInputStoreOptions(string FilePath);

public sealed record UserInputOutcome(
  bool Cancelled,
  IReadOnlyList<UserInputAnswerView> Answers
);

public sealed record PendingUserInputHandle(
  UserInputRequestView Request,
  Task<UserInputOutcome> Completion
);

public sealed record UserInputCoordinatorResult(
  bool Found,
  bool Accepted,
  bool Cancelled,
  string? Diagnostic,
  UserInputRequestView? Request = null
);

public interface IUserInputCoordinator
{
  Task<PendingUserInputHandle> BeginAsync(
    string browserSessionId,
    string executionSessionId,
    string harness,
    IReadOnlyList<UserInputQuestionView> questions,
    CancellationToken cancellationToken
  );

  Task<UserInputCoordinatorResult> SaveDraftAsync(
    string userInputId,
    UserInputDraftRequest request,
    CancellationToken cancellationToken
  );

  Task<UserInputCoordinatorResult> DecideAsync(
    string userInputId,
    UserInputDecisionRequest request,
    CancellationToken cancellationToken
  );

  Task CancelAsync(
    string userInputId,
    CancellationToken cancellationToken
  );

  void AbandonLiveRequest(string userInputId);

  IReadOnlyList<UserInputRequestView> GetPending(string browserSessionId);

  bool HasPendingForExecution(string? executionSessionId);
}

public sealed class UserInputCoordinator : IUserInputCoordinator
{
  private const int MaximumAnswerLength = 16_384;
  private readonly object _gate = new();
  private readonly Dictionary<string, PersistedUserInput> _pending = new(StringComparer.Ordinal);
  private readonly Dictionary<string, TaskCompletionSource<UserInputOutcome>> _live = new(StringComparer.Ordinal);
  private readonly SemaphoreSlim _persistenceGate = new(1, 1);
  private readonly UserInputStoreOptions _options;
  private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true
  };

  public UserInputCoordinator(UserInputStoreOptions options)
  {
    _options = options;
    Load();
  }

  public async Task<PendingUserInputHandle> BeginAsync(
    string browserSessionId,
    string executionSessionId,
    string harness,
    IReadOnlyList<UserInputQuestionView> questions,
    CancellationToken cancellationToken
  )
  {
    ValidateQuestions(questions);
    var id = Guid.NewGuid().ToString("N");
    var persisted = new PersistedUserInput(
      id,
      browserSessionId,
      executionSessionId,
      harness,
      questions,
      0,
      [],
      DateTimeOffset.UtcNow
    );
    var completion = new TaskCompletionSource<UserInputOutcome>(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    lock (_gate)
    {
      if (_pending.Values.Any(item => string.Equals(
        item.ExecutionSessionId,
        executionSessionId,
        StringComparison.Ordinal
      )))
      {
        throw new InvalidOperationException(
          $"Execution session {executionSessionId} already has pending user input."
        );
      }
      _pending.Add(id, persisted);
      _live.Add(id, completion);
    }
    try
    {
      await PersistAsync(cancellationToken);
    }
    catch
    {
      lock (_gate)
      {
        _pending.Remove(id);
        _live.Remove(id);
      }
      throw;
    }
    return new PendingUserInputHandle(ToView(persisted, true), completion.Task);
  }

  public async Task<UserInputCoordinatorResult> SaveDraftAsync(
    string userInputId,
    UserInputDraftRequest request,
    CancellationToken cancellationToken
  )
  {
    PersistedUserInput updated;
    lock (_gate)
    {
      if (!_pending.TryGetValue(userInputId, out var current))
      {
        return Missing();
      }
      var identityFailure = ValidateIdentity(current, request.BrowserSessionId, request.ExecutionSessionId);
      if (identityFailure is not null)
      {
        return identityFailure;
      }
      if (request.CurrentQuestionIndex < 0 || request.CurrentQuestionIndex >= current.Questions.Count)
      {
        return Invalid("The current question index is outside this request.");
      }
      var answers = ValidateDraftAnswers(current.Questions, request.Answers, requireAll: false, out var diagnostic);
      if (diagnostic is not null)
      {
        return Invalid(diagnostic);
      }
      updated = current with
      {
        CurrentQuestionIndex = request.CurrentQuestionIndex,
        Answers = answers!
      };
      _pending[userInputId] = updated;
    }
    await PersistAsync(cancellationToken);
    return new UserInputCoordinatorResult(
      true,
      true,
      false,
      null,
      ToView(updated, IsLive(userInputId))
    );
  }

  public async Task<UserInputCoordinatorResult> DecideAsync(
    string userInputId,
    UserInputDecisionRequest request,
    CancellationToken cancellationToken
  )
  {
    PersistedUserInput current;
    TaskCompletionSource<UserInputOutcome>? completion;
    IReadOnlyList<UserInputAnswerView> answers = [];
    lock (_gate)
    {
      if (!_pending.TryGetValue(userInputId, out current!))
      {
        return Missing();
      }
      var identityFailure = ValidateIdentity(current, request.BrowserSessionId, request.ExecutionSessionId);
      if (identityFailure is not null)
      {
        return identityFailure;
      }
      _live.TryGetValue(userInputId, out completion);
      if (!request.Cancelled && completion is null)
      {
        return Invalid(
          "The underlying harness request cannot be resumed after the Host restart. Cancel it or start a new turn."
        );
      }
      if (!request.Cancelled)
      {
        answers = ValidateDraftAnswers(current.Questions, request.Answers, requireAll: true, out var diagnostic)
          ?? [];
        if (diagnostic is not null)
        {
          return Invalid(diagnostic);
        }
      }
      _pending.Remove(userInputId);
      _live.Remove(userInputId);
    }
    await PersistAsync(cancellationToken);
    completion?.TrySetResult(new UserInputOutcome(request.Cancelled, answers));
    return new UserInputCoordinatorResult(
      true,
      true,
      request.Cancelled,
      null
    );
  }

  public async Task CancelAsync(string userInputId, CancellationToken cancellationToken)
  {
    TaskCompletionSource<UserInputOutcome>? completion;
    lock (_gate)
    {
      _pending.Remove(userInputId);
      _live.Remove(userInputId, out completion);
    }
    await PersistAsync(cancellationToken);
    completion?.TrySetResult(new UserInputOutcome(true, []));
  }

  public void AbandonLiveRequest(string userInputId)
  {
    lock (_gate)
    {
      _live.Remove(userInputId);
    }
  }

  public IReadOnlyList<UserInputRequestView> GetPending(string browserSessionId)
  {
    lock (_gate)
    {
      return _pending.Values.Where(item => string.Equals(
        item.BrowserSessionId,
        browserSessionId,
        StringComparison.Ordinal
      )).OrderBy(item => item.CreatedAt).Select(
        item => ToView(item, _live.ContainsKey(item.Id))
      ).ToArray();
    }
  }

  public bool HasPendingForExecution(string? executionSessionId)
  {
    if (string.IsNullOrWhiteSpace(executionSessionId)) return false;
    lock (_gate)
    {
      return _pending.Values.Any(item => string.Equals(
        item.ExecutionSessionId,
        executionSessionId,
        StringComparison.Ordinal
      ));
    }
  }

  private bool IsLive(string id)
  {
    lock (_gate)
    {
      return _live.ContainsKey(id);
    }
  }

  private async Task PersistAsync(CancellationToken cancellationToken)
  {
    await _persistenceGate.WaitAsync(cancellationToken);
    try
    {
      PersistedUserInput[] snapshot;
      lock (_gate)
      {
        snapshot = _pending.Values.OrderBy(item => item.CreatedAt).ToArray();
      }
      var directory = Path.GetDirectoryName(_options.FilePath);
      if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
      var temporary = _options.FilePath + ".tmp";
      await File.WriteAllTextAsync(
        temporary,
        JsonSerializer.Serialize(snapshot, _json),
        cancellationToken
      );
      File.Move(temporary, _options.FilePath, true);
    }
    finally
    {
      _persistenceGate.Release();
    }
  }

  private void Load()
  {
    if (!File.Exists(_options.FilePath)) return;
    try
    {
      var items = JsonSerializer.Deserialize<PersistedUserInput[]>(
        File.ReadAllText(_options.FilePath),
        _json
      ) ?? [];
      foreach (var item in items)
      {
        ValidateQuestions(item.Questions);
        _pending[item.Id] = item;
      }
    }
    catch (Exception exception) when (
      exception is JsonException or IOException or UnauthorizedAccessException
    )
    {
      throw new InvalidOperationException(
        "The persisted user-input state could not be loaded.",
        exception
      );
    }
  }

  private static void ValidateQuestions(IReadOnlyList<UserInputQuestionView> questions)
  {
    if (questions.Count is < 1 or > UserInputProtocol.MaximumQuestions)
    {
      throw new InvalidOperationException("A user-input batch must contain between 1 and 4 questions.");
    }
    if (questions.Any(question => question.Options.Count > UserInputProtocol.MaximumOptions))
    {
      throw new InvalidOperationException("A user-input question cannot contain more than 4 options.");
    }
    if (questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != questions.Count)
    {
      throw new InvalidOperationException("Question identifiers must be unique inside a user-input batch.");
    }
  }

  private static IReadOnlyList<UserInputAnswerView>? ValidateDraftAnswers(
    IReadOnlyList<UserInputQuestionView> questions,
    IReadOnlyList<UserInputAnswerView> answers,
    bool requireAll,
    out string? diagnostic
  )
  {
    diagnostic = null;
    var questionIds = questions.Select(question => question.Id).ToHashSet(StringComparer.Ordinal);
    if (
      answers.Count > questions.Count
      || answers.Select(answer => answer.QuestionId).Distinct(StringComparer.Ordinal).Count() != answers.Count
      || answers.Any(answer => !questionIds.Contains(answer.QuestionId))
    )
    {
      diagnostic = "The submitted answers do not match this question batch.";
      return null;
    }
    if (answers.Any(answer => string.IsNullOrWhiteSpace(answer.Answer)))
    {
      diagnostic = "Saved answers cannot be empty.";
      return null;
    }
    if (answers.Any(answer => answer.Answer.Length > MaximumAnswerLength))
    {
      diagnostic = $"An answer exceeds the {MaximumAnswerLength}-character limit.";
      return null;
    }
    if (requireAll && answers.Count != questions.Count)
    {
      diagnostic = "Answer every question before submitting the batch.";
      return null;
    }
    return answers.Select(answer => answer with { Answer = answer.Answer.Trim() }).ToArray();
  }

  private static UserInputCoordinatorResult? ValidateIdentity(
    PersistedUserInput current,
    string browserSessionId,
    string executionSessionId
  )
  {
    return string.Equals(current.BrowserSessionId, browserSessionId, StringComparison.Ordinal)
      && string.Equals(current.ExecutionSessionId, executionSessionId, StringComparison.Ordinal)
        ? null
        : Invalid("This user-input request belongs to a different browser or execution session.");
  }

  private static UserInputRequestView ToView(PersistedUserInput item, bool resumable)
  {
    return new UserInputRequestView(
      item.Id,
      item.ExecutionSessionId,
      item.Harness,
      item.Questions,
      item.CurrentQuestionIndex,
      item.Answers,
      resumable
    );
  }

  private static UserInputCoordinatorResult Missing()
  {
    return new UserInputCoordinatorResult(false, false, false, "The user-input request is no longer pending.");
  }

  private static UserInputCoordinatorResult Invalid(string diagnostic)
  {
    return new UserInputCoordinatorResult(true, false, false, diagnostic);
  }

  private sealed record PersistedUserInput(
    string Id,
    string BrowserSessionId,
    string ExecutionSessionId,
    string Harness,
    IReadOnlyList<UserInputQuestionView> Questions,
    int CurrentQuestionIndex,
    IReadOnlyList<UserInputAnswerView> Answers,
    DateTimeOffset CreatedAt
  );
}
