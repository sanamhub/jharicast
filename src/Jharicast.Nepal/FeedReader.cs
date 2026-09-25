using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jharicast.Nepal;

/// <summary>Tolerant readers for feed records: a missing or odd value is null, or drift, never an exception.</summary>
internal static class FeedReader
{
    public static int Id(JsonElement e) => Int(e, "id") ?? 0;

    public static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } t ? t : null;

    public static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static DateTimeOffset? Time(JsonElement e, string name, SortedSet<string> drift)
    {
        if (Text(e, name) is not { } text)
        {
            return null;
        }

        // BIPAD sends +05:45 offsets. A time without one is read as Nepal time, never as this machine's.
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Unspecified
                ? NepalTime.ToUtc(parsed)
                : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None).ToUniversalTime();
        }

        drift.Add($"{name}:{text}");
        return null;
    }

    // GeoJSON point: [longitude, latitude].
    public static GeoPoint? Point(JsonElement e, SortedSet<string> drift)
    {
        if (!e.TryGetProperty("point", out var p) || p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (p.TryGetProperty("coordinates", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 2
            && c[0].TryGetDouble(out var lon) && c[1].TryGetDouble(out var lat) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
        {
            return new GeoPoint(lat, lon);
        }

        drift.Add(string.Create(CultureInfo.InvariantCulture, $"point:{Id(e)}"));
        return null;
    }

    /// <summary>A number sent as a JSON number or as an invariant-culture string.</summary>
    public static double? Number(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
            _ => null,
        };
    }
}
