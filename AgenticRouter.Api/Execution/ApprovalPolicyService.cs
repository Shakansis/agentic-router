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

    if (action.Tool is "download_file" or "download_files")
    {
      if (policy == "autonomous"
        && action.DownloadConflicts?.Count > 0
        && action.DownloadConflicts.Count == (action.Tool == "download_file"
          ? 1
          : action.Arguments.GetProperty("files").GetArrayLength()))
      {
        return false;
      }
      return true;
    }

    if (string.Equals(
      policy,
      "ask",
      StringComparison.Ordinal
    ))
    {
      return !action.ProcessPermissionGranted;
    }

    if (string.Equals(
      policy,
      "autonomous",
      StringComparison.Ordinal
    ))
    {
      return false;
    }

    if (!string.Equals(
      policy,
      "auto",
      StringComparison.Ordinal
    ))
    {
      throw new LocalActionException(
        "approval-policy",
        "Approval policy must be ask, auto, or autonomous."
      );
    }

    return false;
  }
}
