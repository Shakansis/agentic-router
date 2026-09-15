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
using var listener = new TcpListener(IPAddress.Loopback, endpoint.Port);
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
  while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
  {
  }
  var requestPath = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    .ElementAtOrDefault(1);
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
