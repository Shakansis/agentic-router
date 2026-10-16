using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Execution;

// Applies only to Host file readers. Native harness readers retain their own contracts.
public sealed class HostFileReadPolicy(ITokenEstimator tokens)
{
  public const int MaximumReadBytes = 1024 * 1024;
  public const int MaximumBatchFiles = 128;
  public const string ContextRecoveryRequired = "host-file-read-context-recovery";
  private const int MaximumBatchReadBytes = 8 * MaximumReadBytes;
  private const int LegacyReadBytes = 128 * 1024;
  private const int FramingTokens = 512;
  private const int UsefulReadTokens = 1024;
  public const string ReadCompactionMarker = "HOST_READ_CONTEXT_COMPACTED_V1";
  public const string Guidance = "Prefer one complete read of a relevant file when it fits the available context; "
    + "prefer read_files for multiple relevant files, ordered by priority, and native parallel reads when supported. "
    + "Avoid unnecessary chunks. Host file reads adapt to the aggregate remaining context, returning truncation metadata "
    + "and nextOffsetBytes when partial. Continue at that exact byte offset only when more evidence is needed. "
    + "A large file does not forbid a smaller read. When context is exhausted the Host compacts context and retries "
    + "the existing objective; do not bypass a read failure through a process or unrelated capability. "
    + "These Host limits do not limit harness-native file tools.";

  public long AvailableTokens(long maximumContextTokens, long reservedOutputTokens, long usedTokens) =>
    Math.Max(0, maximumContextTokens - Math.Max(0, reservedOutputTokens) - Math.Max(0, usedTokens));

  public long AvailableTokens(ContextUsageView usage, int? reservedResponseTokens = null) => AvailableTokens(
    usage.EffectiveLimitTokens, reservedResponseTokens ?? usage.ReservedResponseTokens,
    Math.Max(usage.InputTokens + usage.OutputTokens,
      Math.Max(usage.ActiveContextTokens, usage.RequiredContextTokens - usage.ReservedResponseTokens)));

  public long MeasureRequest(IReadOnlyList<OllamaToolMessage> messages, object definitions) =>
    tokens.EstimateToolMessages(messages) + tokens.EstimateText(JsonSerializer.Serialize(definitions));

  public long MeasureRequest(JsonObject request) => new[] { "messages", "input", "instructions", "system", "prompt", "tools" }
    .Sum(name => MeasureNode(request[name]));

  private long MeasureNode(JsonNode? node)
  {
    if (node is null) return 0;
    if (node is JsonValue value && value.TryGetValue<string>(out var text)) return tokens.EstimateText(text);
    if (node is JsonArray array) return array.Sum(MeasureNode);
    if (node is JsonObject item)
    {
      var type = item["type"] is JsonValue kind && kind.TryGetValue<string>(out var name) ? name : null;
      if (type is "image" or "input_image" or "image_url")
        return 1024; // Native embedding occupancy is model-specific; never tokenize encoded image bytes as text.
      return 6 + item.Sum(field => field.Key == "images" && field.Value is JsonArray images
        ? images.Count * 1024L : tokens.EstimateText(field.Key) + MeasureNode(field.Value));
    }
    return tokens.EstimateText(node.ToJsonString());
  }

  // Covers the nested Host facts + Qwen tool-result envelope as well as simpler protocols.
  public long MeasureResult(string output) =>
    tokens.EstimateText(JsonSerializer.Serialize(JsonSerializer.Serialize(output))) + FramingTokens;

  public HostReadContextFit FitContextForRead(IReadOnlyList<OllamaToolMessage> messages, object definitions,
    long contextLimit, long reservedOutputTokens)
  {
    var before = MeasureRequest(messages, definitions);
    var after = before;
    if (AvailableTokens(contextLimit, reservedOutputTokens, before) >= UsefulReadTokens)
      return new(messages, before, after, false, AvailableTokens(contextLimit, reservedOutputTokens, after));
    var compact = messages.ToList();
    for (var index = 0; index < compact.Count; index++)
    {
      var message = compact[index];
      if (message.Role != "assistant" || string.IsNullOrEmpty(message.Thinking)) continue;
      after -= tokens.EstimateText(message.Thinking);
      compact[index] = message with { Thinking = null };
    }
    for (var index = 0; index < compact.Count
      && AvailableTokens(contextLimit, reservedOutputTokens, after) < UsefulReadTokens; index++)
    {
      var message = compact[index];
      if (message.Role != "tool" || string.IsNullOrEmpty(message.Content)
        || message.Content.StartsWith(ReadCompactionMarker, StringComparison.Ordinal)) continue;
      var summary = $"{ReadCompactionMarker}\nTool result {message.ToolCallId ?? message.ToolName ?? "previous"} "
        + $"({message.Content.Length} characters) remains in the conversation activity. "
        + "Its contents were removed only from active inference context to make room for the requested read. "
        + "Reinspect a relevant file/range if this evidence is needed; do not infer omitted content. "
        + "The original call, arguments and result identity are retained.";
      if (tokens.EstimateText(summary) >= tokens.EstimateText(message.Content)) continue;
      after += tokens.EstimateText(summary) - tokens.EstimateText(message.Content);
      compact[index] = message with { Content = summary };
    }
    return new(after < before ? compact : messages, before, after, after < before,
      AvailableTokens(contextLimit, reservedOutputTokens, after));
  }

  public async Task<HostFileRead> ReadAsync(string path, long offset, int? requestedLength,
    long? availableContextTokens, CancellationToken cancellationToken)
  {
    var file = await LoadAsync(path, offset, requestedLength, availableContextTokens, cancellationToken);
    return Prepare(file, availableContextTokens);
  }

  private async Task<LoadedFile> LoadAsync(string path, long offset, int? requestedLength,
    long? availableContextTokens, CancellationToken cancellationToken, int physicalBudget = MaximumReadBytes)
  {
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
      65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var size = stream.Length;
    if (offset < 0 || offset > size || (offset == size && size > 0) || requestedLength is < 1 or > MaximumReadBytes)
      throw new LocalActionException("action-validation",
        $"read_file requires a valid offsetBytes and lengthBytes between 1 and {MaximumReadBytes}.");

    var header = new byte[4];
    var headerLength = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken);
    var (encoding, bomBytes) = DetectEncoding(header.AsSpan(0, headerLength));
    var alignment = encoding is UTF32Encoding ? 4 : encoding is UnicodeEncoding ? 2 : 1;
    if (offset > 0 && (offset < bomBytes || (offset - bomBytes) % alignment != 0))
      throw new LocalActionException("action-validation", "read_file offsetBytes must be at a character boundary.");
    if (offset == 0 && physicalBudget < bomBytes) physicalBudget = 0;
    var start = offset == 0 && physicalBudget > 0 ? bomBytes : offset;
    if (start > size) start = size;
    stream.Seek(start, SeekOrigin.Begin);
    var memoryLimit = Math.Min(physicalBudget, availableContextTokens is null ? LegacyReadBytes : MaximumReadBytes);
    var prefixBytes = start - offset;
    if (requestedLength is not null && requestedLength < prefixBytes)
      throw new LocalActionException("action-validation", "read_file lengthBytes must include the complete encoding header.");
    var length = (int)Math.Min(size - start,
      Math.Min((requestedLength ?? memoryLimit) - prefixBytes, memoryLimit - prefixBytes));
    if (availableContextTokens is not null)
      // At most three UTF-16 units per estimated token and four bytes per unit.
      // This upper bound avoids loading content that cannot possibly fit the reply.
      length = (int)Math.Min(length, Math.Clamp(availableContextTokens.Value, 0, MaximumReadBytes) * 12);
    var buffer = new byte[length];
    var read = 0;
    while (read < buffer.Length)
    {
      var received = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
      if (received == 0) break;
      read += received;
    }
    var memoryDeferredPrefix = physicalBudget < Math.Min(size - offset, requestedLength ?? MaximumReadBytes);
    var content = DecodeCompletePrefix(encoding, buffer, ref read, start + read < size, memoryDeferredPrefix);
    if (read == 0 && buffer.Length > 0)
      return new LoadedFile(offset, requestedLength, size, offset, 0, "", encoding, 0,
        memoryDeferredPrefix && availableContextTokens is not <= 0);
    return new LoadedFile(offset, requestedLength, size, start, read, content, encoding, memoryLimit,
      memoryLimit == 0 && availableContextTokens is not <= 0);
  }

  private HostFileRead Prepare(LoadedFile file, long? availableContextTokens)
  {
    var (offset, requestedLength, size, start, read, content, encoding, memoryLimit, memoryDeferred) = file;
    var budget = availableContextTokens is null ? long.MaxValue : Math.Max(0, availableContextTokens.Value);
    var selective = tokens.EstimateText(content) > 60_000;
    var contentBudget = selective ? Math.Min(budget, 20_000) : budget;
    var explicitRange = requestedLength is not null || offset > 0;
    var complete = start + read == size && offset == 0;
    if (complete && !explicitRange && MeasureResult(content) <= contentBudget)
      return new HostFileRead(content, content, true);

    var reason = selective && budget >= 20_000 ? "selective-context-policy"
      : read < size - start && requestedLength is not null ? "requested-range"
      : read < size - start && read >= memoryLimit - (start - offset) - 3 ? "memory-limit" : "context-budget";
    string SerializeRange(int characters)
    {
      if (characters > 0 && char.IsHighSurrogate(content[characters - 1])) characters--;
      var part = content[..characters];
      var end = characters == 0 && size > start ? offset : start + encoding.GetByteCount(part);
      return JsonSerializer.Serialize(new
      {
        offsetBytes = offset,
        lengthBytes = end - offset,
        sizeBytes = size,
        content = part,
        truncated = end < size,
        nextOffsetBytes = end < size ? (long?)end : null,
        encoding = encoding.WebName,
        availableContextTokens,
        budgetSource = availableContextTokens is null ? "unavailable" : "host-context",
        readMode = selective ? "selective" : tokens.EstimateText(content) <= 20_000 ? "integral-preferred" : "adaptive",
        truncationReason = end < size ? reason : null,
        maxRangeBytes = memoryLimit
      });
    }

    var accepted = content.Length;
    var output = SerializeRange(accepted);
    if (MeasureResult(output) > contentBudget)
    {
      reason = selective && budget >= 20_000 ? "selective-context-policy" : "context-budget";
      var low = 0;
      var high = content.Length;
      while (low < high)
      {
        var middle = low + (high - low + 1) / 2;
        if (MeasureResult(SerializeRange(middle)) <= contentBudget) low = middle;
        else high = middle - 1;
      }
      accepted = low;
      if (accepted > 0 && char.IsHighSurrogate(content[accepted - 1])) accepted--;
      output = SerializeRange(accepted);
    }
    if (accepted == 0 && size > start)
      return new HostFileRead(output, null, false,
        memoryDeferred ? "FILE_READ_MEMORY_EXHAUSTED" : "FILE_READ_CONTEXT_EXHAUSTED");
    return new HostFileRead(output, null, offset == 0 && start + encoding.GetByteCount(content[..accepted]) == size);
  }

  public async Task<HostFileRead> ReadFilesAsync(IReadOnlyList<HostFileReadRequest> requests,
    long? availableContextTokens, CancellationToken cancellationToken)
  {
    if (requests.Count is < 1 or > MaximumBatchFiles)
      throw new LocalActionException("action-validation", $"read_files requires 1 to {MaximumBatchFiles} files in priority order.");
    var memoryRemaining = MaximumBatchReadBytes;
    var physicalBudgets = new List<int>();
    foreach (var request in requests)
    {
      if (request.OffsetBytes < 0 || request.LengthBytes is < 1 or > MaximumReadBytes)
        throw new LocalActionException("action-validation", "read_files requires valid byte ranges.");
      var bytes = (int)Math.Min(Math.Max(0, new FileInfo(request.Path).Length - request.OffsetBytes),
        Math.Min(request.LengthBytes ?? MaximumReadBytes, availableContextTokens is null ? LegacyReadBytes : MaximumReadBytes));
      if (availableContextTokens is not null)
        bytes = (int)Math.Min(bytes, Math.Clamp(availableContextTokens.Value, 0, MaximumReadBytes) * 12);
      var admittedBytes = Math.Min(bytes, memoryRemaining);
      memoryRemaining -= admittedBytes;
      physicalBudgets.Add(admittedBytes);
    }
    var loaded = await Task.WhenAll(requests.Select((request, index) => LoadAsync(request.Path,
      request.OffsetBytes, request.LengthBytes, availableContextTokens, cancellationToken, physicalBudgets[index])));
    JsonObject Node(int index, HostFileRead read)
    {
      var result = read.CompleteContent is not null
        ? new JsonObject
        {
          ["content"] = read.CompleteContent,
          ["truncated"] = false,
          ["nextOffsetBytes"] = null
        }
        : JsonNode.Parse(read.Output)!.AsObject();
      result["path"] = requests[index].RelativePath;
      if (read.Code is not null) result["code"] = read.Code;
      return result;
    }
    string Envelope(IReadOnlyList<JsonObject> files) => JsonSerializer.Serialize(new { files, availableContextTokens });
    var budget = availableContextTokens ?? long.MaxValue;
    var candidates = loaded.Select((file, index) => Node(index, Prepare(file, availableContextTokens))).ToArray();
    var output = Envelope(candidates);
    if (MeasureResult(output) <= budget) return new HostFileRead(output, null, candidates.All(file => file["truncated"]?.GetValue<bool>() == false));
    var empty = loaded.Select((file, index) => Node(index, Prepare(file, 0))).ToArray();
    var remaining = Math.Max(0, budget - MeasureResult(Envelope(empty)));
    var allocated = new List<JsonObject>();
    for (var index = 0; index < loaded.Length; index++)
    {
      var read = Prepare(loaded[index], remaining);
      var node = Node(index, read);
      var extra = Math.Max(0, MeasureResult(node.ToJsonString()) - MeasureResult(empty[index].ToJsonString()));
      remaining = Math.Max(0, remaining - extra);
      allocated.Add(node);
      if (node["truncated"]?.GetValue<bool>() == true
        && node["truncationReason"]?.GetValue<string>() == "context-budget")
        remaining = 0;
    }
    output = Envelope(allocated);
    // Recheck the complete envelope: per-file rounding must never admit excess content.
    for (var index = allocated.Count - 1; index >= 0 && MeasureResult(output) > budget; index--)
    {
      if (allocated[index]["content"]?.GetValue<string>().Length is not > 0) continue;
      var reducedBudget = Math.Max(0, MeasureResult(allocated[index].ToJsonString())
        - (MeasureResult(output) - budget) - FramingTokens);
      allocated[index] = Node(index, Prepare(loaded[index], reducedBudget));
      output = Envelope(allocated);
    }
    return new HostFileRead(output, null, allocated.All(file => file["truncated"]?.GetValue<bool>() == false),
      allocated.All(file => file["content"]?.GetValue<string>().Length == 0) ? "FILE_READ_CONTEXT_EXHAUSTED" : null);
  }

  private sealed record LoadedFile(long Offset, int? RequestedLength, long Size, long Start,
    int Read, string Content, Encoding Encoding, int MemoryLimit, bool MemoryDeferred);

  private static (Encoding Encoding, int BomBytes) DetectEncoding(ReadOnlySpan<byte> header)
  {
    if (header.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return (new UTF32Encoding(false, false, true), 4);
    if (header.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return (new UTF32Encoding(true, false, true), 4);
    if (header.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(false, true), 3);
    if (header.StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, false, true), 2);
    if (header.StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, false, true), 2);
    return (new UTF8Encoding(false, true), 0);
  }

  private static string DecodeCompletePrefix(Encoding encoding, byte[] buffer, ref int read, bool partial, bool memoryDeferredPrefix)
  {
    var original = read;
    while (true)
    {
      if (read == 0 && original > 0)
      {
        if (memoryDeferredPrefix) return "";
        throw new LocalActionException("action-validation",
          "read_file requires an offset and length containing at least one complete character.");
      }
      try { return encoding.GetString(buffer, 0, read); }
      catch (DecoderFallbackException) when (partial && read > 0 && original - read < 3) { read--; }
      catch (DecoderFallbackException exception)
      {
        throw new LocalActionException("action-validation",
          "read_file requires valid text and an offset at a complete character boundary.", exception);
      }
    }
  }
}

public sealed record HostFileRead(string Output, string? CompleteContent, bool Complete, string? Code = null);

public sealed record HostFileReadRequest(string Path, string RelativePath, long OffsetBytes, int? LengthBytes);

public sealed record HostReadContextFit(IReadOnlyList<OllamaToolMessage> Messages, long BeforeTokens,
  long AfterTokens, bool Compacted, long AvailableTokens);
