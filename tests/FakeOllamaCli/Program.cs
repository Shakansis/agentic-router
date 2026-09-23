using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (string.Equals(
  Path.GetFileNameWithoutExtension(Environment.ProcessPath),
  "nvidia-smi",
  StringComparison.OrdinalIgnoreCase
))
{
  Console.WriteLine("0, GPU-nvidia-4090-fixture, NVIDIA GeForce RTX 4090, 24564");
  Console.WriteLine("1, GPU-nvidia-2070-fixture, NVIDIA GeForce RTX 2070 SUPER, 8192");
  return;
}

if (!args.SequenceEqual(["serve"], StringComparer.Ordinal))
{
  Console.Error.WriteLine($"Unexpected fake Ollama arguments: {string.Join(' ', args)}");
  Environment.ExitCode = 2;
  return;
}

var host = Environment.GetEnvironmentVariable("OLLAMA_HOST")
  ?? throw new InvalidOperationException("OLLAMA_HOST is required.");
var prefix = host.StartsWith("http", StringComparison.OrdinalIgnoreCase)
  ? host
  : $"http://{host}";
var endpoint = new Uri(prefix, UriKind.Absolute);
var library = Environment.GetEnvironmentVariable("OLLAMA_LLM_LIBRARY") ?? "unknown";
var startupFailurePath = Path.ChangeExtension(
  Environment.ProcessPath!,
  ".startup-failure"
);
if (File.Exists(startupFailurePath))
{
  var failure = await File.ReadAllTextAsync(startupFailurePath);
  File.Delete(startupFailurePath);
  Console.Error.WriteLine(failure);
  Environment.ExitCode = 1;
  return;
}
var startupPlanPath = Path.ChangeExtension(Environment.ProcessPath!, ".startup-plan.json");
var startsPath = Path.ChangeExtension(Environment.ProcessPath!, ".starts");
var readyPath = Path.ChangeExtension(Environment.ProcessPath!, ".ready");
var delayMilliseconds = 0;
if (File.Exists(startupPlanPath))
{
  var plan = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(startupPlanPath))!;
  var attempt = File.Exists(startsPath) ? File.ReadAllLines(startsPath).Length : 0;
  await File.AppendAllTextAsync(startsPath, $"{Environment.ProcessId}{Environment.NewLine}");
  delayMilliseconds = plan[Math.Min(attempt, plan.Length - 1)];
}
_ = EmitBackendAsync();

async Task EmitBackendAsync()
{
  if (delayMilliseconds < 0)
  {
    Console.Error.WriteLine("inference compute id=cpu library=cpu");
    while (!File.Exists(readyPath)) await Task.Delay(50);
  }
  else
  {
    await Task.Delay(delayMilliseconds);
  }
  EmitBackend();
}

void EmitBackend()
{
  if (string.Equals(library, "vulkan", StringComparison.OrdinalIgnoreCase))
  {
    Console.Error.WriteLine(
      "inference compute id=0 library=Vulkan description=\"NVIDIA GeForce RTX 4090\""
    );
    Console.Error.WriteLine(
      "inference compute id=1 library=Vulkan description=\"AMD Radeon RX 7900 XTX\""
    );
  }
  else
  {
    var backend = library.StartsWith("cuda", StringComparison.OrdinalIgnoreCase)
      ? "CUDA"
      : library.StartsWith("rocm", StringComparison.OrdinalIgnoreCase)
        ? "ROCm"
        : library;
    Console.Error.WriteLine($"inference compute id={backend}0 library={backend}");
  }
}
using var listener = new TcpListener(IPAddress.Parse(endpoint.Host), endpoint.Port);
listener.Start();

while (true)
{
  TcpClient client;
  try
  {
    client = await listener.AcceptTcpClientAsync();
  }
  catch (SocketException)
  {
    break;
  }
  await using var stream = client.GetStream();
  using var reader = new StreamReader(
    stream,
    Encoding.ASCII,
    false,
    1024,
    leaveOpen: true
  );
  var requestLine = await reader.ReadLineAsync() ?? string.Empty;
  var contentLength = 0;
  while (await reader.ReadLineAsync() is { Length: > 0 } header)
  {
    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
    {
      contentLength = int.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
  }
  var requestPath = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    .ElementAtOrDefault(1);
  var forwardPath = Path.ChangeExtension(Environment.ProcessPath!, ".forward-url");
  if (File.Exists(forwardPath) && requestPath is not "/api/version" and not "/test/environment")
  {
    var body = new char[contentLength];
    if (contentLength > 0) await reader.ReadBlockAsync(body.AsMemory());
    using var proxy = new HttpClient();
    using var forwarded = new HttpRequestMessage(
      new HttpMethod(requestLine.Split(' ')[0]),
      new Uri(new Uri((await File.ReadAllTextAsync(forwardPath)).Trim()), requestPath)
    );
    if (contentLength > 0) forwarded.Content = new StringContent(new string(body), Encoding.UTF8, "application/json");
    using var response = await proxy.SendAsync(forwarded);
    var forwardedBytes = await response.Content.ReadAsByteArrayAsync();
    await stream.WriteAsync(Encoding.ASCII.GetBytes(
      $"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n"
      + $"Content-Type: {response.Content.Headers.ContentType}\r\n"
      + $"Content-Length: {forwardedBytes.Length}\r\nConnection: close\r\n\r\n"
    ));
    await stream.WriteAsync(forwardedBytes);
    client.Dispose();
    continue;
  }
  var payload = requestPath switch
  {
    "/api/version" => JsonSerializer.Serialize(new { version = "fake-managed-1.0" }),
    "/api/tags" => JsonSerializer.Serialize(new
    {
      models = new[]
      {
        new
        {
          name = "managed:latest",
          size = 1_000_000_000L,
          digest = "managed-digest"
        }
      }
    }),
    "/api/ps" => JsonSerializer.Serialize(new { models = Array.Empty<object>() }),
    "/test/environment" => JsonSerializer.Serialize(new
    {
      processId = Environment.ProcessId,
      host,
      library = Environment.GetEnvironmentVariable("OLLAMA_LLM_LIBRARY"),
      cudaVisibleDevices = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES"),
      hipVisibleDevices = Environment.GetEnvironmentVariable("HIP_VISIBLE_DEVICES"),
      rocrVisibleDevices = Environment.GetEnvironmentVariable("ROCR_VISIBLE_DEVICES"),
      vulkanVisibleDevices = Environment.GetEnvironmentVariable(
        "GGML_VK_VISIBLE_DEVICES"
      ),
      vulkan = Environment.GetEnvironmentVariable("OLLAMA_VULKAN"),
      spread = Environment.GetEnvironmentVariable("OLLAMA_SCHED_SPREAD"),
      contextLength = Environment.GetEnvironmentVariable("OLLAMA_CONTEXT_LENGTH"),
      noCloud = Environment.GetEnvironmentVariable("OLLAMA_NO_CLOUD")
    }),
    _ => JsonSerializer.Serialize(new { error = "not found" })
  };
  var status = requestPath is
    "/api/version" or "/api/tags" or "/api/ps" or "/test/environment"
      ? 200
      : 404;
  var bytes = Encoding.UTF8.GetBytes(payload);
  var headers = Encoding.ASCII.GetBytes(
    $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
      + "Content-Type: application/json\r\n"
      + $"Content-Length: {bytes.Length}\r\n"
      + "Connection: close\r\n\r\n"
  );
  await stream.WriteAsync(headers);
  await stream.WriteAsync(bytes);
  client.Dispose();
}
