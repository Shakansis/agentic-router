using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;

namespace AgenticRouter.Api.Execution;

public enum CoordinatorFailureCategory
{
  CorrectablePlanning,
  ContextFit,
  Provider,
  PolicyDenied,
  SecurityDenied,
  ToolExecution,
  Other
}

public interface IPlanningFailureClassifier
{
  CoordinatorFailureCategory Classify(
    Exception exception
  );
}

public sealed class PlanningFailureClassifier : IPlanningFailureClassifier
{
  public CoordinatorFailureCategory Classify(
    Exception exception
  )
  {
    if (exception is ToolProtocolException)
    {
      return CoordinatorFailureCategory.CorrectablePlanning;
    }

    if (exception is OllamaProviderException)
    {
      return CoordinatorFailureCategory.Provider;
    }

    if (
      exception is OllamaRuntimeProfileException runtime
      && runtime.Error.Code is "request-context-does-not-fit" or "context-item-too-large"
    )
    {
      return CoordinatorFailureCategory.ContextFit;
    }

    if (exception is not LocalActionException localAction)
    {
      return CoordinatorFailureCategory.Other;
    }

    return localAction.Stage switch
    {
      "local-action-planning" => CoordinatorFailureCategory.CorrectablePlanning,
      "action-validation" => CoordinatorFailureCategory.CorrectablePlanning,
      "execution-plan" => CoordinatorFailureCategory.CorrectablePlanning,
      "file-not-inspected" => CoordinatorFailureCategory.CorrectablePlanning,
      "process-validation" => CoordinatorFailureCategory.PolicyDenied,
      "path-validation" => CoordinatorFailureCategory.SecurityDenied,
      "trusted-workspace" => CoordinatorFailureCategory.SecurityDenied,
      "action-execution" => CoordinatorFailureCategory.ToolExecution,
      "process-execution" => CoordinatorFailureCategory.ToolExecution,
      "file-conflict" => CoordinatorFailureCategory.ToolExecution,
      "post-write-verification" => CoordinatorFailureCategory.ToolExecution,
      _ => CoordinatorFailureCategory.Other
    };
  }

}
