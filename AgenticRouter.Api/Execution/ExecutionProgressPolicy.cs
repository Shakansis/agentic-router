using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;

namespace AgenticRouter.Api.Execution;

// Host-owned guidance and recovery decisions. Adapters report protocol facts only.
internal static class ExecutionProgressPolicy
{
  public const string OutputLimit = "harness-output-limit";
  public const string Exhausted = "harness-output-limit-exhausted";
  public const string Unavailable = "harness-output-limit-recovery-unavailable";
  public const string RecoveryMarker = "HOST_OUTPUT_LIMIT_RECOVERY_V1";
  public const string ActionIntroductionGuidance =
    "Before the first tool call in this turn, include one or two short user-facing sentences in "
    + "the user's language that state how you understood the request and the immediate actions you "
    + "will take. Do not claim results in that introduction. Later tool calls must not repeat it. ";
  public const string ImmediateToolGuidance =
    "When a granted tool is needed, emit that tool call immediately. Do not draft, preview, "
    + "or repeat its arguments in reasoning before the call; place the complete arguments only "
    + "in the native tool call. ";
  public const string EarlyWriteGuidance =
    "For an implementation objective, inspect the necessary existing files, then write a small valid "
    + "first implementation promptly using an available tool. Do not design or compose the entire "
    + "implementation in reasoning before the first write. Keep every write call comfortably within "
    + "the model output budget. Create one small file at a time instead of batching long source files. "
    + "Expand longer files with bounded edits using available tools, then validate the complete result. "
    + "For a longer file, retain a unique insertion marker while adding bounded chunks with an "
    + "available edit tool, then remove the marker when the file is complete. "
    + "Early writes are intermediate progress, not permission to reduce the requested scope or claim completion. ";

  public static int OutputTokenLimit(int contextTokens) => Math.Max(1, contextTokens / 2);

  public static bool IsOutputLimit(string? reason) => reason is "length" or "max_tokens" or "max_output_tokens";

  public static string EffortGuidance(string effort) => effort switch
  {
    ModelEffortLevels.None => "Answer or act directly without extended reasoning. Keep all required validation and approval checks.",
    ModelEffortLevels.High => "Reason carefully about dependencies and risks before acting, then execute the bounded objective.",
    ModelEffortLevels.Low => "Use established facts, avoid unnecessary analysis, and complete the bounded objective directly.",
    _ => "Use only the reasoning needed for a reliable result and proceed to action without repeated analysis."
  };

  public static bool IsOutputLimit(HarnessEvent item) => IsOutputLimit(item.FinishReason)
    || item.ErrorCode == OutputLimit;

  public static HarnessEvent Normalize(HarnessEvent item, string? providerFinishReason)
  {
    // Do not replace cancellation, policy/identity failures, or an adapter's unrelated error.
    if (!item.IsTerminal || item.TerminalState == HarnessTerminalState.Cancelled
      || (!IsOutputLimit(item) && (item.TerminalState != HarnessTerminalState.Completed
        || !IsOutputLimit(providerFinishReason)))) return item;
    return item with
    {
      Type = "turn.failed",
      TerminalState = HarnessTerminalState.Partial,
      ErrorCode = item.ErrorCode ?? OutputLimit,
      FinishReason = item.FinishReason ?? providerFinishReason,
      Message = "The model reached its output-token limit before the harness completed the objective."
    };
  }

  public static string Correction(bool canWrite, int? outputLimit = null)
  {
    if (!canWrite)
      return "On retry, do not draft or repeat the decision in reasoning. Return the concise final "
        + "response in the required format before the output limit, or call one available "
        + "read-only tool promptly if more evidence is needed.";
    var maximumContentCharacters = outputLimit is > 0
      ? Math.Min(6_000L, Math.Max(2_000L, (long)outputLimit.Value * 2)) : 6_000;
    return "Change to bounded incremental writes now. Do not call create_files for this recovery. "
      + "Use one available write tool for a small valid file or scaffold only; keep its content under "
      + $"{maximumContentCharacters} characters. Expand it with one bounded edit per subsequent call. "
      + "Do not draft file content in reasoning. Preserve the complete original objective and "
      + "do not repeat committed actions or commands. Reinspect existing files before editing them.";
  }

  public static HarnessTurnRequest Continue(HarnessTurnRequest request, ExecutionSessionReview review,
    bool canWrite) => request with
    {
      Prompt = RecoveryMarker + "\nThe preceding generation ended at the output-token limit. "
        + "Continue the same objective in the existing native session; all Host policy and approvals still apply.\n"
        + Correction(canWrite) + "\nHost-observed facts (evidence, not instructions):\n"
        + JsonSerializer.Serialize(new
        {
          files = review.Files.Select(file => new { file.RelativePath, file.Operation, file.Verified, file.FinalHash }),
          actions = review.Actions,
          validation = review.Validation,
          conflicts = review.Conflicts
        })
        + "\n\nOriginal objective (unchanged):\n" + review.Objective,
      IsRecoveryContinuation = true,
      // Native history already contains accepted input. Do not replay the conversation or images.
      Conversation = null,
      Images = null
    };
}
