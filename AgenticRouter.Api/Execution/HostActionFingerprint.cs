using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Execution;

public static class HostActionFingerprint
{
  public static string ArgumentsSha256(JsonElement arguments)
  {
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
      WriteCanonical(writer, arguments);
    }
    return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
  }

  public static string Action(string canonicalTool, JsonElement arguments)
  {
    return Action(canonicalTool, ArgumentsSha256(arguments));
  }

  public static string Action(string canonicalTool, string argumentsSha256)
  {
    var value = $"{canonicalTool}:{argumentsSha256}";
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
  }

  private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
  {
    switch (value.ValueKind)
    {
      case JsonValueKind.Object:
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
          writer.WritePropertyName(property.Name);
          WriteCanonical(writer, property.Value);
        }
        writer.WriteEndObject();
        break;
      case JsonValueKind.Array:
        writer.WriteStartArray();
        foreach (var item in value.EnumerateArray())
        {
          WriteCanonical(writer, item);
        }
        writer.WriteEndArray();
        break;
      default:
        value.WriteTo(writer);
        break;
    }
  }
}
