using System.Collections.Concurrent;
using System.Text.Json;
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
}

public sealed record ToolProtocolConformanceResult(
  bool Passed,
  string Model,
  string Digest,
  string OllamaVersion,
  string? Failure
);

public sealed class ToolProtocolConformanceService : IToolProtocolConformanceService
{
  public const string BenchmarkMarker = "TOOL_PROTOCOL_CONFORMANCE_V1";

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
    var normalizedDigest = string.IsNullOrWhiteSpace(
      digest
    )
      ? "unknown"
      : digest;
    string version;

    try
    {
      version = await _ollamaClient.GetVersionAsync(
        baseUri,
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
        exception.Message
      );
    }

    var key = string.Join(
      "|",
      model,
      normalizedDigest,
      version
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
      result = new ToolProtocolConformanceResult(
        true,
        model,
        normalizedDigest,
        version,
        null
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
      or InvalidDataException
      or JsonException
    )
    {
      result = new ToolProtocolConformanceResult(
        false,
        model,
        normalizedDigest,
        version,
        exception.Message
      );
    }

    _results.TryAdd(
      key,
      result
    );
    return result;
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
      throw new InvalidDataException(
        $"The protocol probe requires a non-empty {property} string."
      );
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
