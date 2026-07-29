namespace AgenticRouter.Api.Execution;

public interface IApprovalPolicyService
{
  bool RequiresApproval(
    ValidatedLocalAction action,
    string policy
  );
}

public sealed class ApprovalPolicyService : IApprovalPolicyService
{
  public bool RequiresApproval(
    ValidatedLocalAction action,
    string policy
  )
  {
    if (action.ReadOnly)
    {
      return false;
    }

    if (string.Equals(
      policy,
      "ask",
      StringComparison.Ordinal
    ))
    {
      return true;
    }

    if (!string.Equals(
      policy,
      "auto",
      StringComparison.Ordinal
    ))
    {
      throw new LocalActionException(
        "approval-policy",
        "Approval policy must be ask or auto."
      );
    }

    return action.RequiresExplicitApproval;
  }
}
