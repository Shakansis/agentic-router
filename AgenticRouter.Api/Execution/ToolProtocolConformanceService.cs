using System.Collections.Concurrent;
using System.Text.Json;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Execution;

public interface IToolProtocolConformanceService
{
  Task<ToolProtocolConformanceResult> VerifyAsync(
    Uri baseUri,
    string model,
    string? digest,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  );

  Task<ToolProtocolConformanceResult?> GetCachedAsync(
    Uri baseUri,
    string model,
    string? digest,
    CancellationToken cancellationToken
  );

  Task<ToolProtocolConformanceResult> VerifyPathAsync(
    Uri baseUri,
    string model,
    string? providerRevision,
    string profile,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  );

  Task<ToolProtocolConformanceResult?> GetCachedPathAsync(
    Uri baseUri,
    string model,
    string? providerRevision,
    string profile,
    CancellationToken cancellationToken
  );
}

public sealed record ToolProtocolConformanceResult(
  bool Passed,
  string Model,
  string Digest,
  string OllamaVersion,
  string? Failure,
  string Profile = CoordinationConformanceProfiles.NativeStrict,
  string Status = CoordinationConformanceProfiles.Unknown,
  string Provider = ModelProviderIds.OllamaLocal,
  string AdapterVersion = ToolProtocolConformanceService.AdapterContractVersion,
  string BenchmarkVersion = ToolProtocolConformanceService.BenchmarkContractVersion,
  string Identity = "",
  bool AdaptiveRepairEligible = false
);

public static class CoordinationConformanceProfiles
{
  public const string NativeStrict = "native-strict";
  public const string NativeAdaptive = "native-adaptive";
  public const string StructuredAction = "structured-action";
  public const string GuidanceOnly = "guidance-only";
  public const string Failed = "failed";
  public const string Unknown = "unknown";

  public static bool IsKnown(
    string profile
  )
  {
    return profile is NativeStrict or NativeAdaptive or StructuredAction or GuidanceOnly;
  }
}

public sealed class ToolProtocolConformanceService : IToolProtocolConformanceService
{
  public const string BenchmarkMarker = "TOOL_PROTOCOL_CONFORMANCE_V1";
  public const string NativeAdaptiveBenchmarkMarker = "NATIVE_ADAPTIVE_CONFORMANCE_V1";
  public const string StructuredBenchmarkMarker = "STRUCTURED_ACTION_CONFORMANCE_V1";
  public const string AdapterContractVersion = "provider-chat-adapter-v1";
  public const string BenchmarkContractVersion = "coordination-conformance-v3";

  private static readonly OllamaToolDefinition EchoTool = Tool(
    "benchmark_echo",
    "Return the supplied benchmark value.",
    new
    {
      value = StringProperty()
    },
    ["value"]
  );

  private static readonly OllamaToolDefinition PlanTool = Tool(
    "benchmark_plan",
    "Return a nested plan containing an objective and ordered step titles.",
    new
    {
      objective = StringProperty(),
      steps = new
      {
        type = "array",
        minItems = 2,
        items = new
        {
          type = "object",
          properties = new
          {
            title = StringProperty()
          },
          required = new[]
          {
            "title"
          }
        }
      }
    },
    ["objective", "steps"]
  );

  private static readonly OllamaToolDefinition ReadTool = Tool(
    "benchmark_read",
    "Read the synthetic benchmark file.",
    new
    {
      path = StringProperty()
    },
    ["path"]
  );

  private static readonly OllamaToolDefinition EditTool = Tool(
    "benchmark_edit",
    "Edit the synthetic benchmark file after reading its tool result.",
    new
    {
      path = StringProperty(),
      content = StringProperty()
    },
    ["path", "content"]
  );

  private static readonly JsonElement StructuredActionSchema =
    JsonSerializer.SerializeToElement(
      new
      {
        type = "object",
        properties = new
        {
          completed = new
          {
            type = "boolean"
          },
          title = StringProperty(),
          tool = new
          {
            type = "string",
            @enum = new[]
            {
              "benchmark_echo"
            }
          },
          arguments = new
          {
            type = "object",
            properties = new
            {
              value = StringProperty()
            },
            required = new[]
            {
              "value"
            }
          }
        },
        required = new[]
        {
          "completed",
          "title",
          "tool",
          "arguments"
        }
      }
    );

  private readonly IOllamaClient _ollamaClient;
  private readonly ConcurrentDictionary<string, ToolProtocolConformanceResult> _results = new(
    StringComparer.Ordinal
  );

  public ToolProtocolConformanceService(
    IOllamaClient ollamaClient
  )
  {
    _ollamaClient = ollamaClient;
  }

  public async Task<ToolProtocolConformanceResult> VerifyAsync(
    Uri baseUri,
    string model,
    string? digest,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    return await VerifyPathAsync(
      baseUri,
      model,
      digest,
      CoordinationConformanceProfiles.NativeStrict,
      usageContext,
      cancellationToken
    );
  }

  public async Task<ToolProtocolConformanceResult> VerifyPathAsync(
    Uri baseUri,
    string model,
    string? providerRevision,
    string profile,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    if (!CoordinationConformanceProfiles.IsKnown(
      profile
    ))
    {
      throw new ArgumentOutOfRangeException(
        nameof(profile),
        profile,
        "Unknown coordination conformance profile."
      );
    }

    var normalizedDigest = string.IsNullOrWhiteSpace(
      providerRevision
    )
      ? "unknown"
      : providerRevision;
    var reference = ProviderModelReference.Parse(
      model
    );
    string version;

    try
    {
      version = await _ollamaClient.GetProtocolVersionAsync(
        baseUri,
        model,
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
      or JsonException
    )
    {
      return new ToolProtocolConformanceResult(
        false,
        model,
        normalizedDigest,
        "unavailable",
        exception.Message,
        profile,
        CoordinationConformanceProfiles.Failed,
        reference.ProviderId,
        Identity: CreateIdentity(
          reference.ProviderId,
          model,
          normalizedDigest,
          "unavailable",
          profile
        )
      );
    }

    var key = CacheKey(
      reference.ProviderId,
      model,
      normalizedDigest,
      version,
      profile
    );

    if (_results.TryGetValue(
      key,
      out var cached
    ))
    {
      return cached;
    }

    ToolProtocolConformanceResult result;

    try
    {
      if (profile == CoordinationConformanceProfiles.NativeStrict)
      {
        await VerifyNativeStrictAsync(
          baseUri,
          model,
          usageContext,
          cancellationToken
        );
      }
      else if (profile == CoordinationConformanceProfiles.NativeAdaptive)
      {
        await VerifyNativeAdaptiveAsync(
          baseUri,
          model,
          usageContext,
          cancellationToken
        );
      }
      else if (profile == CoordinationConformanceProfiles.StructuredAction)
      {
        await VerifyStructuredActionAsync(
          baseUri,
          model,
          usageContext,
          cancellationToken
        );
      }
      else
      {
        throw new InvalidDataException(
          $"The {profile} benchmark is not implemented by contract {BenchmarkContractVersion}."
        );
      }

      result = new ToolProtocolConformanceResult(
        true,
        model,
        normalizedDigest,
        version,
        null,
        profile,
        profile,
        reference.ProviderId,
        Identity: CreateIdentity(
          reference.ProviderId,
          model,
          normalizedDigest,
          version,
          profile
        )
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
      or InvalidDataException
      or SemanticConformanceException
      or JsonException
    )
    {
      result = new ToolProtocolConformanceResult(
        false,
        model,
        normalizedDigest,
        version,
        exception.Message,
        profile,
        CoordinationConformanceProfiles.Failed,
        reference.ProviderId,
        Identity: CreateIdentity(
          reference.ProviderId,
          model,
          normalizedDigest,
          version,
          profile
        ),
        AdaptiveRepairEligible: exception is SemanticConformanceException
      );
    }

    _results.TryAdd(
      key,
      result
    );
    return result;
  }

  public async Task<ToolProtocolConformanceResult?> GetCachedAsync(
    Uri baseUri,
    string model,
    string? digest,
    CancellationToken cancellationToken
  )
  {
    return await GetCachedPathAsync(
      baseUri,
      model,
      digest,
      CoordinationConformanceProfiles.NativeStrict,
      cancellationToken
    );
  }

  public async Task<ToolProtocolConformanceResult?> GetCachedPathAsync(
    Uri baseUri,
    string model,
    string? providerRevision,
    string profile,
    CancellationToken cancellationToken
  )
  {
    if (!CoordinationConformanceProfiles.IsKnown(
      profile
    ))
    {
      return null;
    }

    string version;

    try
    {
      version = await _ollamaClient.GetProtocolVersionAsync(
        baseUri,
        model,
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
      or JsonException
    )
    {
      return null;
    }

    var key = CacheKey(
      ProviderModelReference.Parse(
        model
      ).ProviderId,
      model,
      string.IsNullOrWhiteSpace(
        providerRevision
      )
        ? "unknown"
        : providerRevision,
      version,
      profile
    );

    return _results.TryGetValue(
      key,
      out var cached
    )
      ? cached
      : null;
  }

  private static string CacheKey(
    string provider,
    string model,
    string digest,
    string version,
    string profile
  )
  {
    return string.Join(
      "|",
      BenchmarkContractVersion,
      AdapterContractVersion,
      provider,
      model,
      digest,
      version,
      profile
    );
  }

  private async Task VerifyNativeStrictAsync(
    Uri baseUri,
    string model,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    await VerifySimpleCallAsync(
      baseUri,
      model,
      usageContext with
      {
        ModelRole = UsageModelRoles.Benchmark,
        RequestPurpose = "tool-conformance-simple"
      },
      cancellationToken
    );
    await VerifyNestedPlanAsync(
      baseUri,
      model,
      usageContext with
      {
        ModelRole = UsageModelRoles.Benchmark,
        RequestPurpose = "tool-conformance-nested-plan"
      },
      cancellationToken
    );
    await VerifyToolResultLoopAsync(
      baseUri,
      model,
      usageContext with
      {
        ModelRole = UsageModelRoles.Benchmark,
        RequestPurpose = "tool-conformance-loop"
      },
      cancellationToken
    );
  }

  private async Task VerifyNativeAdaptiveAsync(
    Uri baseUri,
    string model,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var rejectedArguments = JsonSerializer.SerializeToElement(
      new
      {
        path = "sample.txt",
        content = string.Empty
      }
    );
    var response = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      [
        new OllamaToolMessage(
          "system",
          NativeAdaptiveBenchmarkMarker
            + "\nThis is a non-executing protocol benchmark. Return exactly one corrected native tool call and no prose."
        ),
        new OllamaToolMessage(
          "user",
          "Edit the synthetic benchmark path \"sample.txt\" with content \"after\"."
        ),
        new OllamaToolMessage(
          "assistant",
          ToolCalls:
          [
            new OllamaToolCall(
              "benchmark_edit",
              rejectedArguments
            )
          ]
        ),
        new OllamaToolMessage(
          "tool",
          "{\"status\":\"rejected\",\"field\":\"content\",\"reason\":\"A non-empty string is required.\",\"expected\":\"after\"}",
          ToolName: "benchmark_edit"
        )
      ],
      [EditTool],
      "tool-conformance-native-adaptive-repair",
      usageContext with
      {
        ModelRole = UsageModelRoles.Benchmark,
        RequestPurpose = "tool-conformance-native-adaptive-repair"
      },
      cancellationToken
    );
    var call = RequireSingleCall(
      response,
      "benchmark_edit"
    );
    RequireExactString(
      call.Arguments,
      "path",
      "sample.txt"
    );
    RequireExactString(
      call.Arguments,
      "content",
      "after"
    );
  }

  private async Task VerifyStructuredActionAsync(
    Uri baseUri,
    string model,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var content = await _ollamaClient.GenerateStructuredAsync(
      baseUri,
      model,
      [
        new(
          "system",
          StructuredBenchmarkMarker
            + "\nReturn one structured action proposal. This benchmark never executes the action."
        ),
        new(
          "user",
          "Propose benchmark_echo with value ok and title Verify structured action."
        )
      ],
      StructuredActionSchema,
      "structured-action-conformance",
      usageContext with
      {
        ModelRole = UsageModelRoles.Benchmark,
        RequestPurpose = "structured-action-conformance"
      },
      cancellationToken
    );
    using var document = JsonDocument.Parse(
      content
    );
    var root = document.RootElement;

    if (
      root.ValueKind != JsonValueKind.Object
      || !root.TryGetProperty(
        "completed",
        out var completed
      )
      || completed.ValueKind != JsonValueKind.False
      || !root.TryGetProperty(
        "title",
        out var title
      )
      || title.ValueKind != JsonValueKind.String
      || string.IsNullOrWhiteSpace(
        title.GetString()
      )
      || !root.TryGetProperty(
        "tool",
        out var tool
      )
      || tool.GetString() != "benchmark_echo"
      || !root.TryGetProperty(
        "arguments",
        out var arguments
      )
    )
    {
      throw new InvalidDataException(
        "The structured-action probe returned an invalid action envelope."
      );
    }

    RequireString(
      arguments,
      "value"
    );
  }

  private static string CreateIdentity(
    string provider,
    string model,
    string revision,
    string runtimeVersion,
    string profile
  )
  {
    return string.Join(
      "|",
      provider,
      model,
      revision,
      AdapterContractVersion,
      runtimeVersion,
      BenchmarkContractVersion,
      profile
    );
  }

  private async Task VerifySimpleCallAsync(
    Uri baseUri,
    string model,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var response = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      Messages(
        "Call benchmark_echo exactly once with value \"ok\"."
      ),
      [EchoTool],
      "tool-conformance-simple",
      usageContext,
      cancellationToken
    );
    var call = RequireSingleCall(
      response,
      "benchmark_echo"
    );
    RequireString(
      call.Arguments,
      "value"
    );
  }

  private async Task VerifyNestedPlanAsync(
    Uri baseUri,
    string model,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var response = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      Messages(
        "Call benchmark_plan exactly once with objective \"verify\" and two titled steps."
      ),
      [PlanTool],
      "tool-conformance-nested-plan",
      usageContext,
      cancellationToken
    );
    var call = RequireSingleCall(
      response,
      "benchmark_plan"
    );
    RequireString(
      call.Arguments,
      "objective"
    );

    if (
      !call.Arguments.TryGetProperty(
        "steps",
        out var steps
      )
      || steps.ValueKind != JsonValueKind.Array
      || steps.GetArrayLength() < 2
      || steps.EnumerateArray().Any(
        step => step.ValueKind != JsonValueKind.Object
          || !step.TryGetProperty(
            "title",
            out var title
          )
          || title.ValueKind != JsonValueKind.String
          || string.IsNullOrWhiteSpace(
            title.GetString()
          )
      )
    )
    {
      throw new InvalidDataException(
        "The nested-plan probe returned invalid step titles."
      );
    }
  }

  private async Task VerifyToolResultLoopAsync(
    Uri baseUri,
    string model,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var initialMessages = Messages(
      "Call benchmark_read for path \"sample.txt\". After its tool result, call benchmark_edit for the same path."
    );
    var readResponse = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      initialMessages,
      [ReadTool],
      "tool-conformance-loop-read",
      usageContext with
      {
        RequestPurpose = "tool-conformance-loop-read"
      },
      cancellationToken
    );
    var readCall = RequireSingleCall(
      readResponse,
      "benchmark_read"
    );
    RequireString(
      readCall.Arguments,
      "path"
    );
    var loopMessages = initialMessages.Concat(
      [
        new OllamaToolMessage(
          "assistant",
          readResponse.Content,
          readResponse.Thinking,
          readResponse.ToolCalls
        ),
        new OllamaToolMessage(
          "tool",
          "{\"content\":\"before\"}",
          ToolName: "benchmark_read"
        )
      ]
    ).ToArray();
    var editResponse = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      loopMessages,
      [EditTool],
      "tool-conformance-loop-edit",
      usageContext with
      {
        RequestPurpose = "tool-conformance-loop-edit"
      },
      cancellationToken
    );
    var editCall = RequireSingleCall(
      editResponse,
      "benchmark_edit"
    );
    RequireString(
      editCall.Arguments,
      "path"
    );
    RequireString(
      editCall.Arguments,
      "content"
    );
  }

  private static IReadOnlyList<OllamaToolMessage> Messages(
    string instruction
  )
  {
    return
    [
      new OllamaToolMessage(
        "system",
        BenchmarkMarker
          + "\nThis is a non-executing protocol benchmark. Return exactly one native tool call and no prose."
      ),
      new OllamaToolMessage(
        "user",
        instruction
      )
    ];
  }

  private static OllamaToolCall RequireSingleCall(
    OllamaToolResponse response,
    string expectedName
  )
  {
    if (
      response.ToolCalls.Count != 1
      || !string.Equals(
        response.ToolCalls[0].Name,
        expectedName,
        StringComparison.Ordinal
      )
      || response.ToolCalls[0].Arguments.ValueKind != JsonValueKind.Object
    )
    {
      throw new InvalidDataException(
        $"The protocol probe expected exactly one {expectedName} call."
      );
    }

    return response.ToolCalls[0];
  }

  private static void RequireString(
    JsonElement arguments,
    string property
  )
  {
    if (
      !arguments.TryGetProperty(
        property,
        out var value
      )
      || value.ValueKind != JsonValueKind.String
      || string.IsNullOrWhiteSpace(
        value.GetString()
      )
    )
    {
      throw new SemanticConformanceException(
        $"The protocol probe requires a non-empty {property} string."
      );
    }
  }

  private static void RequireExactString(
    JsonElement arguments,
    string property,
    string expected
  )
  {
    RequireString(
      arguments,
      property
    );

    if (!string.Equals(
      arguments.GetProperty(
        property
      ).GetString(),
      expected,
      StringComparison.Ordinal
    ))
    {
      throw new SemanticConformanceException(
        $"The adaptive protocol probe requires {property} to equal \"{expected}\"."
      );
    }
  }

  private sealed class SemanticConformanceException : Exception
  {
    public SemanticConformanceException(
      string message
    )
      : base(
        message
      )
    {
    }
  }

  private static OllamaToolDefinition Tool(
    string name,
    string description,
    object properties,
    IReadOnlyList<string> required
  )
  {
    return new OllamaToolDefinition(
      name,
      description,
      JsonSerializer.SerializeToElement(
        new
        {
          type = "object",
          properties,
          required
        }
      )
    );
  }

  private static object StringProperty()
  {
    return new
    {
      type = "string",
      minLength = 1
    };
  }
}
