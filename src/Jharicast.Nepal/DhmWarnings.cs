using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jharicast.Nepal;

/// <summary>Hazards in DHM's warning feed (<c>real_result</c> keys).</summary>
public enum Hazard
{
    /// <summary><c>rain_fall</c>.</summary>
    Rainfall = 0,

    /// <summary><c>wind_gust</c>.</summary>
    WindGust = 1,

    /// <summary><c>thunder_storm</c>.</summary>
    ThunderStorm = 2,

    /// <summary><c>snow_fall</c>.</summary>
    SnowFall = 3,

    /// <summary><c>landslide</c>.</summary>
    Landslide = 4,

    /// <summary><c>heat_wave</c>.</summary>
    HeatWave = 5,

    /// <summary><c>cold_weather</c>.</summary>
    ColdWeather = 6,

    /// <summary><c>avalanche</c>.</summary>
    Avalanche = 7,

    /// <summary><c>forest_fire</c>.</summary>
    ForestFire = 8,

    /// <summary><c>tornado</c>.</summary>
    Tornado = 9,
}

/// <summary>One current official warning.</summary>
/// <param name="District">District.</param>
/// <param name="Hazard">Hazard.</param>
/// <param name="Level">Level. Never Green: the feed lists only districts under a warning.</param>
public sealed record DistrictWarning(District District, Hazard Hazard, AlertLevel Level);

/// <summary>Parsed <c>dhm.gov.np/home/getAPIData/1</c>, current warnings only.</summary>
/// <param name="Warnings">Current warnings.</param>
/// <param name="Drift">Names, hazards or levels we did not recognise. Non-empty means the feed changed: alert the maintainer.</param>
/// <param name="Provenance">Where and when.</param>
public sealed record DhmWarningSnapshot(IReadOnlyList<DistrictWarning> Warnings, IReadOnlyList<string> Drift, Provenance Provenance)
{
    /// <summary>Highest current level for a district across hazards, or for one hazard.</summary>
    /// <param name="district">District.</param>
    /// <param name="hazard">One hazard, or null for all.</param>
    /// <returns>The level; Green when none.</returns>
    public AlertLevel LevelFor(District district, Hazard? hazard = null) =>
        AlertLevels.Max(Warnings.Where(w => w.District == district && (hazard is null || w.Hazard == hazard)).Select(w => w.Level));
}

/// <summary>
/// Parses DHM's public warning JSON. The level mapping (1 none, 2 yellow, 3 orange, 4 red) is
/// taken from the site's own <c>getWarningIcon</c> script, read on 2026-09-24 (research note 1.2).
/// </summary>
public static class DhmWarningsParser
{
    /// <summary>Source id used in <see cref="Provenance"/>.</summary>
    public const string SourceId = "dhm.warnings";

    private static readonly Dictionary<string, Hazard> Hazards = new(StringComparer.Ordinal)
    {
        ["rain_fall"] = Hazard.Rainfall,
        ["wind_gust"] = Hazard.WindGust,
        ["thunder_storm"] = Hazard.ThunderStorm,
        ["snow_fall"] = Hazard.SnowFall,
        ["landslide"] = Hazard.Landslide,
        ["heat_wave"] = Hazard.HeatWave,
        ["cold_weather"] = Hazard.ColdWeather,
        ["avalanche"] = Hazard.Avalanche,
        ["forest_fire"] = Hazard.ForestFire,
        ["tornado"] = Hazard.Tornado,
    };

    /// <summary>Parses the feed. Unknown names, hazards and levels go to <see cref="DhmWarningSnapshot.Drift"/>, never to an exception.</summary>
    /// <param name="utf8Json">Response body.</param>
    /// <param name="fetchedAt">Fetch time, UTC.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="JsonException">The body is not JSON, or has no <c>real_result</c> object.</exception>
    public static DhmWarningSnapshot Parse(ReadOnlyMemory<byte> utf8Json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(utf8Json);
        if (!document.RootElement.TryGetProperty("real_result", out var real) || real.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("DHM warnings: no real_result object.");
        }

        var warnings = new List<DistrictWarning>();
        var drift = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var hazardProperty in real.EnumerateObject())
        {
            if (!Hazards.TryGetValue(hazardProperty.Name, out var hazard))
            {
                drift.Add("hazard:" + hazardProperty.Name);
                continue;
            }

            if (hazardProperty.Value.ValueKind != JsonValueKind.Array)
            {
                drift.Add("shape:" + hazardProperty.Name);
                continue;
            }

            foreach (var item in hazardProperty.Value.EnumerateArray())
            {
                var name = item.TryGetProperty("area_name", out var n) ? n.GetString() : null;
                var level = ParseLevel(item.TryGetProperty("level_id", out var l) ? l : default);
                if (!Gazetteer.TryResolve(name, out var district))
                {
                    drift.Add("district:" + name);
                }
                else if (level is null)
                {
                    drift.Add("level:" + (l.ValueKind == JsonValueKind.Undefined ? "missing" : l.ToString()));
                }
                else if (level != AlertLevel.Green)
                {
                    warnings.Add(new DistrictWarning(district, hazard, level.Value));
                }
            }
        }

        return new DhmWarningSnapshot(warnings, [.. drift], new Provenance(SourceId, SourceKind.Official, fetchedAt));
    }

    private static AlertLevel? ParseLevel(JsonElement element)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id switch
            {
                1 => AlertLevel.Green,
                2 => AlertLevel.Yellow,
                3 => AlertLevel.Orange,
                4 => AlertLevel.Red,
                _ => null,
            }
            : null;
    }
}
