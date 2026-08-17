using System.Text.Json;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Execution;

public sealed record CanonicalToolDefinition(
  string Name,
  string Description,
  JsonElement Parameters
);

public sealed record CanonicalToolCall(
  string CallId,
  string Name,
  JsonElement Arguments
);

public sealed record CanonicalToolResult(
  string CallId,
  string Tool,
  string Status,
  string Output,
  bool Succeeded,
  bool EffectVerified
);

public sealed record CanonicalToolCompletion(
  string Content,
  string? Thinking = null
);

public sealed record CanonicalSpecialistTurn(
  IReadOnlyList<CanonicalToolCall> ToolCalls,
  CanonicalToolCompletion? Completion,
  int IgnoredToolCallCount,
  string? Thinking = null
);

public sealed record SpecialistToolingIdentity(
  string Provider,
  string Model,
  string? Digest,
  bool NativeTools,
  bool StructuredOutput,
  bool ToolProtocolConfirmed
);

public sealed record SpecialistToolingProfile(
  string Id,
  string Version,
  string Transport,
  string PromptInstructions,
  bool AllowsParallelCalls,
  bool CorrectionMayComplete,
  string ResolutionSource
)
{
  public string Identity => $"{Id}@{Version}";
}

public static class SpecialistToolingProfileIds
{
  public const string QwenCodeOllama = "qwen-code-ollama";
  public const string GenericNative = "generic-native";
  public const string GenericStructured = "generic-structured";
}

public interface ISpecialistToolingProfileResolver
{
  SpecialistToolingProfile Resolve(
    SpecialistToolingIdentity identity
  );
}

public interface ISpecialistToolingProtocol
{
  IReadOnlyList<OllamaToolDefinition> ToOllamaDefinitions(
    SpecialistToolingProfile profile,
    IReadOnlyList<CanonicalToolDefinition> definitions
  );

  CanonicalSpecialistTurn Normalize(
    SpecialistToolingProfile profile,
    OllamaToolResponse response
  );

  OllamaToolMessage CreateAssistantMessage(
    CanonicalSpecialistTurn turn
  );

  OllamaToolMessage CreateToolResultMessage(
    SpecialistToolingProfile profile,
    CanonicalToolResult result
  );
}

public sealed class SpecialistToolingProfileResolver : ISpecialistToolingProfileResolver
{
  private const string QwenCodePrompt =
    "SPECIALIST_TOOLING_PROFILE qwen-code-ollama-v1\n"
    + "Use tools for actions and text only for the final user-facing response. "
    + "Prefer the dedicated file, search, validation, and Git tools over run_process. "
    + "Treat the native tool definitions in the current request as a closed list: never call a tool "
    + "that is absent, and never call run_process when the Host says process execution is unavailable "
    + "or the user requested manual testing. "
    + "The presence of a tool means that the Host can validate a proposal; it does not mean "
    + "that the user requested that tool. Run a process only when command execution materially "
    + "advances the requested goal or is necessary to validate executable behavior. Do not launch, "
    + "open, preview, or execute an artifact merely because it was created. Static artifacts normally "
    + "do not require a process after their requested contents have been created. After the Host has "
    + "confirmed that a required output file does not exist, create that output with create_file; do "
    + "not repeat read_file for the same absent output path. "
    + "verified the required effects, finish with a concise response and no tool call. A correction turn "
    + "may also finish without a tool call when no safe action remains or the verified work is complete.";

  private const string GenericNativePrompt =
    "SPECIALIST_TOOLING_PROFILE generic-native-v1\n"
    + "Use a tool call only when an action materially advances the user's goal. Tool availability is "
    + "not evidence that the user requested the tool. When the requested effects are complete, return "
    + "the final response without a tool call.";

  public SpecialistToolingProfile Resolve(
    SpecialistToolingIdentity identity
  )
  {
    if (
      identity.NativeTools
      && string.Equals(
        identity.Provider,
        ModelProviderIds.OllamaLocal,
        StringComparison.Ordinal
      )
      && IsExactQwenCoderModel(identity.Model)
    )
    {
      return new SpecialistToolingProfile(
        SpecialistToolingProfileIds.QwenCodeOllama,
        "1",
        "ollama-native-tools",
        QwenCodePrompt,
        false,
        true,
        identity.ToolProtocolConfirmed
          ? "exact-provider-model-and-confirmed-protocol"
          : "exact-provider-model-and-native-capability"
      );
    }

    if (identity.NativeTools)
    {
      return new SpecialistToolingProfile(
        SpecialistToolingProfileIds.GenericNative,
        "1",
        "provider-native-tools",
        GenericNativePrompt,
        false,
        true,
        "provider-capability"
      );
    }

    return new SpecialistToolingProfile(
      SpecialistToolingProfileIds.GenericStructured,
      "1",
      identity.StructuredOutput ? "provider-structured-output" : "unsupported",
      "SPECIALIST_TOOLING_PROFILE generic-structured-v1",
      false,
      false,
      identity.StructuredOutput ? "provider-capability" : "unsupported"
    );
  }

  private static bool IsExactQwenCoderModel(
    string model
  )
  {
    var reference = ProviderModelReference.Parse(model);
    var modelId = reference.ModelId;
    var tagSeparator = modelId.IndexOf(':');
    var untagged = tagSeparator < 0
      ? modelId
      : modelId[..tagSeparator];
    return string.Equals(
      untagged,
      "qwen3-coder",
      StringComparison.OrdinalIgnoreCase
    );
  }
}

public sealed class SpecialistToolingProtocol : ISpecialistToolingProtocol
{
  private const int ToolOutputLimit = 16_000;

  public IReadOnlyList<OllamaToolDefinition> ToOllamaDefinitions(
    SpecialistToolingProfile profile,
    IReadOnlyList<CanonicalToolDefinition> definitions
  )
  {
    return definitions.Select(
      definition => new OllamaToolDefinition(
        definition.Name,
        ProfileDescription(profile, definition),
        definition.Parameters.Clone()
      )
    ).ToArray();
  }

  public CanonicalSpecialistTurn Normalize(
    SpecialistToolingProfile profile,
    OllamaToolResponse response
  )
  {
    var retainedCalls = profile.AllowsParallelCalls
      ? response.ToolCalls
      : response.ToolCalls.Take(1).ToArray();
    var calls = retainedCalls.Select(
      call => new CanonicalToolCall(
        string.IsNullOrWhiteSpace(call.Id) ? Guid.NewGuid().ToString("N") : call.Id,
        call.Name,
        call.Arguments.Clone()
      )
    ).ToArray();
    var completion = calls.Length == 0 && !string.IsNullOrWhiteSpace(response.Content)
      ? new CanonicalToolCompletion(response.Content.Trim(), response.Thinking)
      : null;
    return new CanonicalSpecialistTurn(
      calls,
      completion,
      response.ToolCalls.Count - calls.Length,
      response.Thinking
    );
  }

  public OllamaToolMessage CreateAssistantMessage(
    CanonicalSpecialistTurn turn
  )
  {
    return new OllamaToolMessage(
      "assistant",
      turn.Completion?.Content,
      turn.Thinking,
      turn.ToolCalls.Select(
        call => new OllamaToolCall(
          call.Name,
          call.Arguments.Clone(),
          call.CallId
        )
      ).ToArray()
    );
  }

  public OllamaToolMessage CreateToolResultMessage(
    SpecialistToolingProfile profile,
    CanonicalToolResult result
  )
  {
    var safeOutput = result.Output.Length <= ToolOutputLimit
      ? result.Output
      : $"{result.Output[..ToolOutputLimit]}\n[tool result truncated]";
    var content = profile.Id == SpecialistToolingProfileIds.QwenCodeOllama
      ? JsonSerializer.Serialize(
        new
        {
          status = result.Status,
          succeeded = result.Succeeded,
          effectVerified = result.EffectVerified,
          output = safeOutput
        }
      )
      : $"Status: {result.Status}\nEffect verified: {result.EffectVerified}\nOutput:\n{safeOutput}";
    return new OllamaToolMessage(
      "tool",
      content,
      ToolName: result.Tool,
      ToolCallId: result.CallId
    );
  }

  private static string ProfileDescription(
    SpecialistToolingProfile profile,
    CanonicalToolDefinition definition
  )
  {
    if (
      profile.Id == SpecialistToolingProfileIds.QwenCodeOllama
      && definition.Name == "run_process"
    )
    {
      return "Run one structured executable and argument list without a shell only when process "
        + "execution is necessary to fulfill or validate the request. Do not execute or launch a file "
        + "merely because it was created.";
    }

    if (
      profile.Id == SpecialistToolingProfileIds.QwenCodeOllama
      && definition.Name == "read_file"
    )
    {
      return "Read one existing bounded text file. If the Host reports that a required output path "
        + "does not exist, do not repeat this read; use create_file for that output instead.";
    }

    if (
      profile.Id == SpecialistToolingProfileIds.QwenCodeOllama
      && definition.Name == "create_file"
    )
    {
      return "Create one new UTF-8 text file. Use this after the Host confirms that a required output "
        + "path is absent; then inspect the created file with read_file before completion.";
    }

    return definition.Description;
  }
}
