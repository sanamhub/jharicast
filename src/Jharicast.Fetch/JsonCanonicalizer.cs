using System;
using System.Buffers;
using System.Linq;
using System.Text.Json;

namespace Jharicast.Fetch;

/// <summary>
/// Rewrites JSON with object keys in ordinal order and no whitespace, so two bodies that differ
/// only in key order or formatting hash the same (ADR-0008). Numbers keep their original text:
/// re-formatting 1.10 as 1.1 would hide a change a feed actually made.
/// </summary>
internal static class JsonCanonicalizer
{
    /// <summary>The canonical UTF-8 bytes, or null when the body is not JSON.</summary>
    public static byte[]? TryCanonicalize(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var buffer = new ArrayBufferWriter<byte>(utf8Json.Length);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true }))
            {
                Write(document.RootElement, writer);
            }

            return buffer.WrittenSpan.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            default:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
        }
    }
}
