using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Execution;

public interface IFunctionGemmaResidentProtocol
{
  bool Supports(
    string model
  );

  IReadOnlyList<FunctionGemmaTeacher> CreateTeacherCatalog(
    IReadOnlyList<InstalledModel> installedModels,
    string currentModel,
    string currentIntent
  );

  Task<FunctionGemmaRouteDecision> RouteAsync(
    Uri baseUri,
    string model,
    string request,
    IReadOnlyList<FunctionGemmaTeacher> teachers,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  );

  Task<FunctionGemmaFailureReview> ReviewFailureAsync(
    Uri baseUri,
    string model,
    FunctionGemmaFailureContext failure,
    ProviderCallContext evaluatorUsageContext,
    ProviderCallContext recoveryUsageContext,
    CancellationToken cancellationToken
  );
}

public sealed record FunctionGemmaTeacher(
  string Model,
  string Intent,
  string Description,
  bool Trained = false
);

public sealed record FunctionGemmaRouteDecision(
  string TeacherModel,
  string Intent,
  string Reason,
  string ContractIdentity,
  string? HostNormalization = null
);

public sealed record FunctionGemmaFailureContext(
  string Request,
  string FailureCode,
  string FailedStep,
  string Stage,
  string Detail,
  string ObservedKind,
  string? ObservedTool,
  string ExpectedTool,
  IReadOnlyList<string> AvailableTools,
  IReadOnlyList<string> AcceptanceCriteria,
  int RecoveryBudgetRemaining
);

public sealed record FunctionGemmaRecoveryDecision(
  string Action,
  string FailureCode,
  string FailedStep,
  string NextTool,
  string Reason
);

public sealed record FunctionGemmaFailureReview(
  string EvaluationReason,
  FunctionGemmaRecoveryDecision Recovery,
  string ContractIdentity
);

public sealed class FunctionGemmaProtocolException : Exception
{
  public FunctionGemmaProtocolException(
    string stage,
    string message
  ) : base(message)
  {
    Stage = stage;
  }

  public string Stage { get; }
}

public sealed class FunctionGemmaResidentProtocol : IFunctionGemmaResidentProtocol
{
  public const string DeveloperPrompt =
    "You can do function calling with the following functions:";
  public const string RouteTool = "route_to_teacher";
  public const string EvaluationTool = "explain_teacher_trace";
  public const string RecoveryTool = "recover_teacher_trace";
  public const string ContractIdentity =
    "ollama-tooling-lab-schema-5|evaluator-prompt-4";

  private const int MaximumReasonLength = 512;
  private const string RecoveryPolicyContract =
    "Policy meanings: retry_with_correction fixes a localized call, argument, "
    + "path, required field, protocol, or final-answer defect without new "
    + "evidence. refresh_state reinspects stale, moved, missing, diverged, or "
    + "changed evidence. handoff_specialist handles contradictory or ambiguous "
    + "intent, unexpected scope, or merge, compile, dependency, or syntax "
    + "failures requiring specialist judgment. stop_for_approval handles absent "
    + "or denied approval. stop_unsafe handles unsafe requests, paths, commands, "
    + "secrets, or remote input. accept is only for ACK.";

  private static readonly FunctionGemmaTeacher[] TrainedTeachers =
  [
    new(
      "qwen3-coder:30b",
      "software-development",
      "Implements and modifies source code using inspected workspace context.",
      true
    ),
    new(
      "gpt-oss:20b",
      "file-operations",
      "Handles file creation, inspection, replacement, and approved deletion workflows.",
      true
    ),
    new(
      "gemma4:26b-a4b-it-qat",
      "process-execution",
      "Runs allowlisted processes and diagnoses exit codes, timeouts, and cancellation.",
      true
    ),
    new(
      "qwen3:30b",
      "git-operations",
      "Inspects and performs bounded Git status, diff, staging, commit, and push operations.",
      true
    ),
    new(
      "gemma4:31b-it-qat",
      "review-and-testing",
      "Reviews changes and selects verification steps without modifying unrelated files.",
      true
    ),
    new(
      "qwen3.6:27b",
      "safety-and-approval",
      "Handles destructive-action approval, invalid paths, hash conflicts, and forbidden commands.",
      true
    )
  ];

  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };

  private readonly IOllamaClient _ollamaClient;

  public FunctionGemmaResidentProtocol(
    IOllamaClient ollamaClient
  )
  {
    _ollamaClient = ollamaClient;
  }

  public bool Supports(
    string model
  )
  {
    return string.Equals(
      model,
      "functiongemma",
      StringComparison.OrdinalIgnoreCase
    ) || model.StartsWith(
      "functiongemma:",
      StringComparison.OrdinalIgnoreCase
    );
  }

  public IReadOnlyList<FunctionGemmaTeacher> CreateTeacherCatalog(
    IReadOnlyList<InstalledModel> installedModels,
    string currentModel,
    string currentIntent
  )
  {
    var installed = new HashSet<string>(
      installedModels.Select(
        item => item.Name
      ),
      StringComparer.OrdinalIgnoreCase
    );
    var catalog = TrainedTeachers.Where(
      teacher => installed.Contains(
        teacher.Model
      )
    ).ToList();

    if (
      installed.Contains(
        currentModel
      )
      && catalog.All(
        teacher => !string.Equals(
          teacher.Model,
          currentModel,
          StringComparison.OrdinalIgnoreCase
        )
      )
    )
    {
      catalog.Add(
        new FunctionGemmaTeacher(
          currentModel,
          currentIntent,
          "Current Agentic Router specialist selected by the configured intent profile."
        )
      );
    }

    return catalog;
  }

  public async Task<FunctionGemmaRouteDecision> RouteAsync(
    Uri baseUri,
    string model,
    string request,
    IReadOnlyList<FunctionGemmaTeacher> teachers,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    if (teachers.Count == 0)
    {
      throw new FunctionGemmaProtocolException(
        "functiongemma-routing",
        "No installed Teacher is available for the FunctionGemma routing contract."
      );
    }

    var intents = teachers.Select(
      teacher => teacher.Intent
    ).Distinct(
      StringComparer.Ordinal
    ).ToArray();
    var messages = new OllamaToolMessage[]
    {
      new(
        "developer",
        DeveloperPrompt
      ),
      new(
        "user",
        "Choose exactly one configured Teacher. Return one native "
          + "route_to_teacher call with teacher_model, intent, and a short "
          + "diagnostic reason. All three arguments are mandatory. Copy the "
          + "selected teacher_model and intent exactly from their allowed "
          + "values. Do not answer the request.\n\nTEACHERS:\n"
          + string.Join(
            "\n",
            teachers.Select(
              teacher => $"- model: {teacher.Model}\n  intents: {teacher.Intent}\n  description: {teacher.Description}"
            )
          )
          + "\n\nREQUEST:\n"
          + request
      )
    };
    var response = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      messages,
      [
        CreateRouteTool(
          teachers,
          intents
        )
      ],
      "functiongemma-routing",
      usageContext,
      cancellationToken
    );
    var call = RequireSingleCall(
      response,
      RouteTool,
      "functiongemma-routing"
    );
    var teacherModel = RequireString(
      call.Arguments,
      "teacher_model",
      "functiongemma-routing"
    );
    var intent = RequireString(
      call.Arguments,
      "intent",
      "functiongemma-routing"
    );
    var reason = RequireReason(
      call.Arguments,
      "functiongemma-routing"
    );
    var selected = teachers.FirstOrDefault(
      teacher => string.Equals(
        teacher.Model,
        teacherModel,
        StringComparison.Ordinal
      )
    ) ?? throw new FunctionGemmaProtocolException(
      "functiongemma-routing",
      "FunctionGemma selected a Teacher outside the offered closed catalog."
    );
    var normalization = string.Equals(
      selected.Intent,
      intent,
      StringComparison.Ordinal
    )
      ? null
      : teachers.Any(
        teacher => string.Equals(
          teacher.Intent,
          intent,
          StringComparison.Ordinal
        )
      )
        ? $"The Host corrected the cross-paired intent '{intent}' to the catalog intent '{selected.Intent}' for Teacher '{selected.Model}'."
        : $"The Host replaced unknown intent '{intent}' with the catalog intent '{selected.Intent}' for Teacher '{selected.Model}'.";
    return new FunctionGemmaRouteDecision(
      selected.Model,
      selected.Intent,
      reason,
      ContractIdentity,
      normalization
    );
  }

  public async Task<FunctionGemmaFailureReview> ReviewFailureAsync(
    Uri baseUri,
    string model,
    FunctionGemmaFailureContext failure,
    ProviderCallContext evaluatorUsageContext,
    ProviderCallContext recoveryUsageContext,
    CancellationToken cancellationToken
  )
  {
    var evaluationResponse = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      EvaluationMessages(
        failure
      ),
      [CreateEvaluationTool()],
      "functiongemma-evaluator",
      evaluatorUsageContext,
      cancellationToken
    );
    var evaluationCall = RequireSingleCall(
      evaluationResponse,
      EvaluationTool,
      "functiongemma-evaluator"
    );
    var evaluationReason = RequireReason(
      evaluationCall.Arguments,
      "functiongemma-evaluator"
    );
    var policy = ResolveRequiredPolicy(
      failure
    );
    var recoveryResponse = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      RecoveryMessages(
        failure,
        evaluationReason,
        policy
      ),
      [
        CreateRecoveryTool(
          failure,
          policy
        )
      ],
      "functiongemma-recovery",
      recoveryUsageContext,
      cancellationToken
    );
    var recoveryCall = RequireSingleCall(
      recoveryResponse,
      RecoveryTool,
      "functiongemma-recovery"
    );
    var recovery = new FunctionGemmaRecoveryDecision(
      RequireString(
        recoveryCall.Arguments,
        "action",
        "functiongemma-recovery"
      ),
      RequireString(
        recoveryCall.Arguments,
        "failure_code",
        "functiongemma-recovery"
      ),
      RequireString(
        recoveryCall.Arguments,
        "failed_step",
        "functiongemma-recovery"
      ),
      RequireString(
        recoveryCall.Arguments,
        "next_tool",
        "functiongemma-recovery"
      ),
      RequireReason(
        recoveryCall.Arguments,
        "functiongemma-recovery"
      )
    );

    if (
      !string.Equals(
        recovery.Action,
        policy.Action,
        StringComparison.Ordinal
      )
      || !string.Equals(
        recovery.FailureCode,
        failure.FailureCode,
        StringComparison.Ordinal
      )
      || !string.Equals(
        recovery.FailedStep,
        failure.FailedStep,
        StringComparison.Ordinal
      )
      || !string.Equals(
        recovery.NextTool,
        policy.NextTool,
        StringComparison.Ordinal
      )
    )
    {
      throw new FunctionGemmaProtocolException(
        "functiongemma-recovery",
        "FunctionGemma did not copy the Host-owned recovery policy exactly."
      );
    }

    return new FunctionGemmaFailureReview(
      evaluationReason,
      recovery,
      ContractIdentity
    );
  }

  private static IReadOnlyList<OllamaToolMessage> EvaluationMessages(
    FunctionGemmaFailureContext failure
  )
  {
    var payload = new Dictionary<string, object?>
    {
      ["request"] = failure.Request,
      ["comparison_facts"] = new object[]
      {
        new Dictionary<string, object?>
        {
          ["step_id"] = failure.FailedStep,
          ["reached"] = true,
          ["kind"] = new
          {
            expected = failure.ExpectedTool == "none"
              ? "final"
              : "tool_call",
            observed = failure.ObservedKind
          },
          ["observed_evidence"] = new
          {
            parser = "host",
            error = Bounded(
              failure.Detail,
              512
            )
          },
          ["protocol"] = new
          {
            native_required = true,
            observed_parser = failure.ObservedKind == "protocol_error"
              ? "invalid"
              : "native"
          },
          ["tool"] = new
          {
            matches = string.Equals(
              failure.ObservedTool,
              failure.ExpectedTool,
              StringComparison.Ordinal
            )
          },
          ["arguments"] = (object?)null,
          ["host_result"] = new
          {
            expected = true,
            observed = false,
            missing = Array.Empty<string>(),
            mismatched = new[]
            {
              failure.FailureCode
            }
          }
        }
      },
      ["host_diagnosis"] = new Dictionary<string, object?>
      {
        ["decision"] = "NACK",
        ["failed_step"] = failure.FailedStep,
        ["reason_code"] = failure.FailureCode,
        ["details"] = new
        {
          stage = failure.Stage,
          message = Bounded(
            failure.Detail,
            512
          )
        }
      }
    };
    return
    [
      new OllamaToolMessage(
        "developer",
        DeveloperPrompt
      ),
      new OllamaToolMessage(
        "user",
        "Explain HOST_DIAGNOSIS for the complete Teacher tool trace in one "
          + "short diagnostic reason. The Host already owns decision and "
          + "failed_step; do not recompute or override them. Use observed evidence "
          + "only to make the explanation useful. Do not reject a correct safety "
          + "refusal merely because the original request is unsafe.\n\nTRACE:\n"
          + JsonSerializer.Serialize(
            payload,
            JsonOptions
          )
      )
    ];
  }

  private static IReadOnlyList<OllamaToolMessage> RecoveryMessages(
    FunctionGemmaFailureContext failure,
    string evaluationReason,
    FunctionGemmaRequiredPolicy policy
  )
  {
    var input = new Dictionary<string, object?>
    {
      ["request"] = failure.Request,
      ["acceptance_criteria"] = failure.AcceptanceCriteria,
      ["recovery_budget"] = failure.RecoveryBudgetRemaining,
      ["diagnosis"] = new
      {
        decision = "NACK",
        failure_code = failure.FailureCode,
        failed_step = failure.FailedStep,
        reason = evaluationReason
      },
      ["available_tools"] = failure.AvailableTools,
      ["expected_tool_for_failed_step"] = failure.ExpectedTool,
      ["failed_event"] = new Dictionary<string, object?>
      {
        ["step_id"] = failure.FailedStep,
        ["decision"] = new
        {
          kind = failure.ObservedKind,
          tool_name = failure.ObservedTool,
          arguments = new { },
          error = Bounded(
            failure.Detail,
            512
          )
        },
        ["host_result"] = (object?)null
      },
      ["prior_host_failures"] = new[]
      {
        new
        {
          stage = failure.Stage,
          failure_code = failure.FailureCode
        }
      }
    };
    var requiredPolicy = new
    {
      action = policy.Action,
      failure_code = failure.FailureCode,
      failed_step = failure.FailedStep,
      next_tool = policy.NextTool
    };
    return
    [
      new OllamaToolMessage(
        "developer",
        DeveloperPrompt
      ),
      new OllamaToolMessage(
        "user",
        "Choose exactly one bounded Host recovery policy from the compact "
          + "typed diagnosis. Do not execute a tool. Use accept only for ACK; "
          + "otherwise choose by the failure semantics described below. "
          + RecoveryPolicyContract
          + " Call recover_teacher_trace exactly once. Always return all five "
          + "arguments: action, failure_code, failed_step, next_tool, and a short "
          + "reason; never return a partial call. Copy diagnosis.failure_code and "
          + "diagnosis.failed_step exactly, without leading spaces or trailing "
          + "punctuation. Use next_tool=none unless the selected retry or refresh "
          + "policy needs a tool.\n\nINPUT:\n"
          + JsonSerializer.Serialize(
            input,
            JsonOptions
          )
          + "\n\nREQUIRED_POLICY (copy the four typed fields exactly):\n"
          + JsonSerializer.Serialize(
            requiredPolicy,
            JsonOptions
          )
      )
    ];
  }

  private static FunctionGemmaRequiredPolicy ResolveRequiredPolicy(
    FunctionGemmaFailureContext failure
  )
  {
    var code = failure.FailureCode;
    var action = code.Contains(
      "approval",
      StringComparison.Ordinal
    )
      ? "stop_for_approval"
      : code.Contains(
          "unsafe",
          StringComparison.Ordinal
        ) || code.Contains(
          "security",
          StringComparison.Ordinal
        ) || code.Contains(
          "forbidden",
          StringComparison.Ordinal
        )
          ? "stop_unsafe"
          : code.Contains(
              "stale",
              StringComparison.Ordinal
            ) || code.Contains(
              "changed",
              StringComparison.Ordinal
            ) || code.Contains(
              "missing_since",
              StringComparison.Ordinal
            ) || code.Contains(
              "moved",
              StringComparison.Ordinal
            )
              ? "refresh_state"
              : code.Contains(
                  "compile",
                  StringComparison.Ordinal
                ) || code.Contains(
                  "dependency",
                  StringComparison.Ordinal
                ) || code.Contains(
                  "merge",
                  StringComparison.Ordinal
                ) || code.Contains(
                  "syntax_invalid_after_edit",
                  StringComparison.Ordinal
                )
                  ? "handoff_specialist"
                  : "retry_with_correction";
    var nextTool = action is "retry_with_correction" or "refresh_state"
      ? failure.ExpectedTool
      : "none";
    return new FunctionGemmaRequiredPolicy(
      action,
      nextTool
    );
  }

  private static OllamaToolDefinition CreateRouteTool(
    IReadOnlyList<FunctionGemmaTeacher> teachers,
    IReadOnlyList<string> intents
  )
  {
    return new OllamaToolDefinition(
      RouteTool,
      "Choose one configured Teacher.",
      JsonSerializer.SerializeToElement(
        new
        {
          type = "object",
          required = new[]
          {
            "teacher_model",
            "intent",
            "reason"
          },
          properties = new Dictionary<string, object>
          {
            ["teacher_model"] = new
            {
              type = "string",
              @enum = teachers.Select(
                teacher => teacher.Model
              ).ToArray(),
              description = "Return exactly one configured Teacher model token."
            },
            ["intent"] = new
            {
              type = "string",
              @enum = intents,
              description = "Return exactly one offered routing intent token."
            },
            ["reason"] = new
            {
              type = "string",
              description = "Short reason for the selected route."
            }
          },
          additionalProperties = false
        }
      )
    );
  }

  private static OllamaToolDefinition CreateEvaluationTool()
  {
    return new OllamaToolDefinition(
      EvaluationTool,
      "Explain the deterministic Host diagnosis for the Teacher trace.",
      JsonSerializer.SerializeToElement(
        new
        {
          type = "object",
          required = new[]
          {
            "reason"
          },
          properties = new Dictionary<string, object>
          {
            ["reason"] = new
            {
              type = "string",
              description = "A concise diagnostic explanation of HOST_DIAGNOSIS. Do not invent a different verdict or failed step.",
              minLength = 1,
              maxLength = MaximumReasonLength
            }
          },
          additionalProperties = false
        }
      )
    );
  }

  private static OllamaToolDefinition CreateRecoveryTool(
    FunctionGemmaFailureContext failure,
    FunctionGemmaRequiredPolicy policy
  )
  {
    var toolNames = new[]
    {
      "none"
    }.Concat(
      failure.AvailableTools
    ).Distinct(
      StringComparer.Ordinal
    ).ToArray();
    return new OllamaToolDefinition(
      RecoveryTool,
      "Choose one bounded Host recovery policy and return one complete five-field decision.",
      JsonSerializer.SerializeToElement(
        new
        {
          type = "object",
          required = new[]
          {
            "action",
            "failure_code",
            "failed_step",
            "next_tool",
            "reason"
          },
          properties = new Dictionary<string, object>
          {
            ["action"] = new
            {
              type = "string",
              @enum = new[]
              {
                "accept",
                "retry_with_correction",
                "refresh_state",
                "handoff_specialist",
                "stop_for_approval",
                "stop_unsafe"
              },
              description = $"Copy REQUIRED_POLICY.action exactly: {policy.Action}."
            },
            ["failure_code"] = new
            {
              type = "string",
              description = "Copy diagnosis.failure_code exactly."
            },
            ["failed_step"] = new
            {
              type = "string",
              description = $"Copy diagnosis.failed_step exactly: {failure.FailedStep}."
            },
            ["next_tool"] = new
            {
              type = "string",
              @enum = toolNames,
              description = $"Copy REQUIRED_POLICY.next_tool exactly: {policy.NextTool}."
            },
            ["reason"] = new
            {
              type = "string",
              description = "Mandatory short diagnostic reason."
            }
          },
          additionalProperties = false
        }
      )
    );
  }

  private static OllamaToolCall RequireSingleCall(
    OllamaToolResponse response,
    string expectedTool,
    string stage
  )
  {
    if (
      response.ToolCalls.Count != 1
      || !string.Equals(
        response.ToolCalls[0].Name,
        expectedTool,
        StringComparison.Ordinal
      )
    )
    {
      throw new FunctionGemmaProtocolException(
        stage,
        $"FunctionGemma must return exactly one native {expectedTool} call."
      );
    }

    if (
      response.ToolCalls[0].Arguments.ValueKind
      != JsonValueKind.Object
    )
    {
      throw new FunctionGemmaProtocolException(
        stage,
        $"FunctionGemma {expectedTool} arguments must be a JSON object."
      );
    }

    return response.ToolCalls[0];
  }

  private static string RequireString(
    JsonElement arguments,
    string propertyName,
    string stage
  )
  {
    if (
      !arguments.TryGetProperty(
        propertyName,
        out var value
      )
      || value.ValueKind != JsonValueKind.String
      || string.IsNullOrWhiteSpace(
        value.GetString()
      )
    )
    {
      throw new FunctionGemmaProtocolException(
        stage,
        $"FunctionGemma omitted required string field '{propertyName}'."
      );
    }

    var normalized = value.GetString()!.Trim();
    if (normalized.Length == 0)
    {
      throw new FunctionGemmaProtocolException(
        stage,
        $"FunctionGemma returned an empty required string field '{propertyName}'."
      );
    }

    return normalized;
  }

  private static string RequireReason(
    JsonElement arguments,
    string stage
  )
  {
    var reason = RequireString(
      arguments,
      "reason",
      stage
    );
    return Bounded(
      reason,
      MaximumReasonLength
    );
  }

  private static string Bounded(
    string value,
    int maximumLength
  )
  {
    var sanitized = new string(
      value.Select(
        character => char.IsControl(
          character
        )
          ? ' '
          : character
      ).ToArray()
    ).Trim();
    return sanitized.Length <= maximumLength
      ? sanitized
      : sanitized[..maximumLength];
  }

  private sealed record FunctionGemmaRequiredPolicy(
    string Action,
    string NextTool
  );
}
