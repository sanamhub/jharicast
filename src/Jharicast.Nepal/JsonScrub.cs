using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jharicast.Nepal;

/// <summary>
/// Removes fields from a feed before it is stored (ADR-0008). An allow list, not a block list:
/// a field the feed adds tomorrow is dropped until someone decides it is safe to keep.
/// </summary>
internal static class JsonScrub
{
    /// <summary>
    /// Copies a body keeping, in each named array of objects, only the listed fields. Scalar root
    /// members are kept; other root members are dropped. A root that is itself an array is looked
    /// up under the name <c>""</c>.
    /// </summary>
    /// <param name="utf8Json">The body.</param>
    /// <param name="arrays">Array name to the fields kept in each of its objects.</param>
    /// <returns>The scrubbed body, compact.</returns>
    /// <exception cref="JsonException">The body is not a JSON object or array.</exception>
    public static byte[] KeepFields(System.ReadOnlyMemory<byte> utf8Json, IReadOnlyDictionary<string, HashSet<string>> arrays)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    WriteArray(writer, root, arrays.GetValueOrDefault(string.Empty) ?? []);
                    break;
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in root.EnumerateObject())
                    {
                        if (arrays.TryGetValue(property.Name, out var fields) && property.Value.ValueKind == JsonValueKind.Array)
                        {
                            writer.WritePropertyName(property.Name);
                            WriteArray(writer, property.Value, fields);
                        }
                        else if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                        {
                            property.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                    break;
                default:
                    throw new JsonException("The body is not a JSON object or array.");
            }
        }

        return buffer.ToArray();
    }

    private static void WriteArray(Utf8JsonWriter writer, JsonElement array, HashSet<string> fields)
    {
        writer.WriteStartArray();
        foreach (var item in array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            writer.WriteStartObject();
            foreach (var field in item.EnumerateObject().Where(f => fields.Contains(f.Name)))
            {
                field.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
