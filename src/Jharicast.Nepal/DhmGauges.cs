using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jharicast.Nepal;

/// <summary>A gauge's state as DHM labels it.</summary>
public enum GaugeStatus
{
    /// <summary>No status: <c>N/A</c> or blank, usually a station without recent data.</summary>
    Unknown = 0,

    /// <summary><c>BELOW WARNING LEVEL</c>.</summary>
    BelowWarning = 1,

    /// <summary><c>WARNING</c>.</summary>
    Warning = 2,

    /// <summary><c>DANGER</c>.</summary>
    Danger = 3,
}

/// <summary>Which way a river level is moving, as DHM labels it.</summary>
public enum RiverTrend
{
    /// <summary>Blank in the feed.</summary>
    Unknown = 0,

    /// <summary><c>STEADY</c>.</summary>
    Steady = 1,

    /// <summary><c>RISING</c>.</summary>
    Rising = 2,

    /// <summary><c>FALLING</c>.</summary>
    Falling = 3,
}

/// <summary>Rain over the hours before a station's latest observation.</summary>
/// <param name="Hours">Window: 1, 3, 6, 12 or 24.</param>
/// <param name="Mm">Total in mm, or null when the station has no value for it.</param>
/// <param name="Warning">DHM's warning flag for the window.</param>
/// <param name="Danger">DHM's danger flag for the window.</param>
public sealed record RainTotal(int Hours, double? Mm, bool Warning, bool Danger);

/// <summary>A rain gauge from DHM's <c>rainfall_watch</c>.</summary>
/// <param name="Id">DHM station id.</param>
/// <param name="Name">Station name, trimmed.</param>
/// <param name="District">District, or null when the feed gives none or only a pre-2015 name such as "Rukum".</param>
/// <param name="Basin">River basin as the feed spells it, or null.</param>
/// <param name="Location">Position, or null when the feed has none.</param>
/// <param name="ObservedAt">Time of the latest observation, or null when there is none.</param>
/// <param name="LatestMm">Latest observed value, mm, or null.</param>
/// <param name="Totals">Totals over 1, 3, 6, 12 and 24 h, as the feed lists them.</param>
/// <param name="Status">Station status.</param>
public sealed record RainStation(int Id, string Name, District? District, string? Basin, GeoPoint? Location, DateTimeOffset? ObservedAt, double? LatestMm, IReadOnlyList<RainTotal> Totals, GaugeStatus Status);

/// <summary>A river gauge from DHM's <c>river_watch</c>.</summary>
/// <param name="Id">DHM station id.</param>
/// <param name="Name">Station name, trimmed.</param>
/// <param name="District">District, or null when the feed gives none or only a pre-2015 name such as "Nawalparasi".</param>
/// <param name="Basin">River basin as the feed spells it, or null.</param>
/// <param name="Location">Position, or null when the feed has none.</param>
/// <param name="ObservedAt">Time of the latest level, or null when there is none.</param>
/// <param name="LevelM">Latest water level, m, or null.</param>
/// <param name="WarningLevelM">Warning level, m, or null when the station has none.</param>
/// <param name="DangerLevelM">Danger level, m, or null when the station has none.</param>
/// <param name="Status">Station status.</param>
/// <param name="Trend">Level trend.</param>
public sealed record RiverStation(int Id, string Name, District? District, string? Basin, GeoPoint? Location, DateTimeOffset? ObservedAt, double? LevelM, double? WarningLevelM, double? DangerLevelM, GaugeStatus Status, RiverTrend Trend);

/// <summary>Parsed <c>dhm.gov.np/home/getAPIData/3</c>: rain and river gauges.</summary>
/// <param name="Rain">Rain gauges.</param>
/// <param name="Rivers">River gauges.</param>
/// <param name="Drift">Values the parser did not recognise. Non-empty means the feed changed: alert the maintainer.</param>
/// <param name="Provenance">Where and when; kind <see cref="SourceKind.Observation"/>.</param>
public sealed record DhmGaugeSnapshot(IReadOnlyList<RainStation> Rain, IReadOnlyList<RiverStation> Rivers, IReadOnlyList<string> Drift, Provenance Provenance);

/// <summary>
/// Parses DHM's public gauge JSON. Station names and districts come with stray spaces, districts
/// in several spellings and cases, warning levels as strings and window lengths as strings or
/// numbers; all are normalised here, culture-invariant.
/// </summary>
public static class DhmGaugeParser
{
    /// <summary>Source id used in <see cref="Provenance"/>.</summary>
    public const string SourceId = "dhm.gauges";

    // Station fields kept by Scrub. The feed's free-text fields (description, onm, stationIndex)
    // have held caretakers' names and phone numbers, so everything not listed here is dropped.
    private static readonly HashSet<string> RainFields = new(StringComparer.Ordinal) { "name", "id", "district", "basin", "latitude", "longitude", "elevation", "latest_observation", "averages", "status" };
    private static readonly HashSet<string> RiverFields = new(StringComparer.Ordinal) { "name", "id", "district", "basin", "latitude", "longitude", "elevation", "waterLevel", "status", "warning_level", "danger_level", "steady" };

    // Districts split in 2015. The feed still uses the old names for some stations; which half is
    // meant is not in the record, so these give no district rather than drift.
    private static readonly HashSet<string> PreSplitNames = new(StringComparer.Ordinal) { "rukum", "nawalparasi" };

    /// <summary>Parses the feed. Unknown districts and labels go to <see cref="DhmGaugeSnapshot.Drift"/>, never to an exception.</summary>
    /// <param name="utf8Json">Response body.</param>
    /// <param name="fetchedAt">Fetch time, UTC.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="JsonException">The body is not JSON, or lacks the <c>rainfall_watch</c> or <c>river_watch</c> array.</exception>
    public static DhmGaugeSnapshot Parse(ReadOnlyMemory<byte> utf8Json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        var drift = new SortedSet<string>(StringComparer.Ordinal);
        var rain = Array("rainfall_watch").Select(s => ReadRain(s, drift)).ToArray();
        var rivers = Array("river_watch").Select(s => ReadRiver(s, drift)).ToArray();
        return new DhmGaugeSnapshot(rain, rivers, [.. drift], new Provenance(SourceId, SourceKind.Observation, fetchedAt));

        IEnumerable<JsonElement> Array(string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToArray()
                : throw new JsonException($"DHM gauges: no {name} array.");
    }

    /// <summary>
    /// A copy of the body with only the station fields the parser reads, so free-text fields that
    /// may carry personal data never reach a snapshot store (ADR-0008).
    /// </summary>
    /// <exception cref="JsonException">The body is not a JSON object.</exception>
    internal static ReadOnlyMemory<byte> Scrub(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("DHM gauges: the body is not an object.");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var keep = property.Name switch
                {
                    "rainfall_watch" => RainFields,
                    "river_watch" => RiverFields,
                    _ => null,
                };
                if (keep is null || property.Value.ValueKind != JsonValueKind.Array)
                {
                    if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    {
                        property.WriteTo(writer); // scalars such as "type"
                    }

                    continue;
                }

                writer.WriteStartArray(property.Name);
                foreach (var station in property.Value.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object))
                {
                    writer.WriteStartObject();
                    foreach (var field in station.EnumerateObject().Where(f => keep.Contains(f.Name)))
                    {
                        field.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static RainStation ReadRain(JsonElement s, SortedSet<string> drift)
    {
        var totals = new List<RainTotal>();
        if (s.TryGetProperty("averages", out var averages) && averages.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in averages.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
            {
                if (Number(a, "interval") is not { } hours || hours != Math.Floor(hours) || hours is < 1 or > 72)
                {
                    drift.Add("interval:" + Raw(a, "interval"));
                    continue;
                }

                var flags = a.TryGetProperty("status", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
                totals.Add(new RainTotal((int)hours, Number(a, "value"), Flag(flags, "warning"), Flag(flags, "danger")));
            }
        }

        var (observedAt, value) = Observation(s, "latest_observation", drift);
        return new RainStation(Id(s), Text(s, "name") ?? string.Empty, ResolveDistrict(s, drift), Text(s, "basin"), Location(s, drift), observedAt, value, totals, Status(s, drift));
    }

    private static RiverStation ReadRiver(JsonElement s, SortedSet<string> drift)
    {
        var (observedAt, level) = Observation(s, "waterLevel", drift);
        var trend = Text(s, "steady")?.ToUpperInvariant() switch
        {
            null => RiverTrend.Unknown,
            "STEADY" => RiverTrend.Steady,
            "RISING" => RiverTrend.Rising,
            "FALLING" => RiverTrend.Falling,
            var other => Drifted(drift, "trend:" + other, RiverTrend.Unknown),
        };
        return new RiverStation(Id(s), Text(s, "name") ?? string.Empty, ResolveDistrict(s, drift), Text(s, "basin"), Location(s, drift), observedAt, level,
            Level(s, "warning_level", drift), Level(s, "danger_level", drift), Status(s, drift), trend);
    }

    private static GaugeStatus Status(JsonElement s, SortedSet<string> drift) => Text(s, "status")?.ToUpperInvariant() switch
    {
        null or "N/A" => GaugeStatus.Unknown,
        "BELOW WARNING LEVEL" => GaugeStatus.BelowWarning,
        "WARNING" => GaugeStatus.Warning,
        "DANGER" => GaugeStatus.Danger,
        var other => Drifted(drift, "status:" + other, GaugeStatus.Unknown),
    };

    private static District? ResolveDistrict(JsonElement s, SortedSet<string> drift)
    {
        var name = Text(s, "district");
        if (name is null || PreSplitNames.Contains(Gazetteer.Key(name)))
        {
            return null;
        }

        if (Gazetteer.TryResolve(name, out var district))
        {
            return district;
        }

        drift.Add("district:" + name);
        return null;
    }

    private static GeoPoint? Location(JsonElement s, SortedSet<string> drift)
    {
        if (Number(s, "latitude") is not { } lat || Number(s, "longitude") is not { } lon)
        {
            return null;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            drift.Add(string.Create(CultureInfo.InvariantCulture, $"coordinates:{Id(s)}"));
            return null;
        }

        return new GeoPoint(lat, lon);
    }

    private static (DateTimeOffset? At, double? Value) Observation(JsonElement s, string name, SortedSet<string> drift)
    {
        if (!s.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        DateTimeOffset? at = null;
        if (Text(o, "datetime") is { } text)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                at = parsed.ToUniversalTime();
            }
            else
            {
                drift.Add("datetime:" + text);
            }
        }

        return (at, Number(o, "value"));
    }

    // Warning and danger levels arrive as strings ("5.2", "10.00"), sometimes blank or null.
    private static double? Level(JsonElement s, string name, SortedSet<string> drift)
    {
        var level = Number(s, name);
        if (level is null && Text(s, name) is { } text && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            drift.Add($"{name}:{text}");
        }

        return level;
    }

    private static double? Number(JsonElement e, string name)
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

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static string Raw(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "missing";

    private static bool Flag(JsonElement flags, string name) =>
        flags.ValueKind == JsonValueKind.Object && flags.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Id(JsonElement s) => s.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var id) ? id : 0;

    private static T Drifted<T>(SortedSet<string> drift, string entry, T value)
    {
        drift.Add(entry);
        return value;
    }
}
