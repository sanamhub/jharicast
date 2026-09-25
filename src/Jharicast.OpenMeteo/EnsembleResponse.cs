using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jharicast.OpenMeteo;

/// <summary>Daily values of one variable for one location: members per day.</summary>
/// <param name="Latitude">Grid latitude Open-Meteo snapped to (not the requested one).</param>
/// <param name="Longitude">Grid longitude.</param>
/// <param name="Days">Local dates, in the request's time zone.</param>
/// <param name="Members">Members[day][member]; member 0 is the control run. Null where the model has no value.</param>
public sealed record EnsembleDaily(double Latitude, double Longitude, IReadOnlyList<DateOnly> Days, IReadOnlyList<IReadOnlyList<double?>> Members)
{
    /// <summary>Source, kind and fetch time. Set by <see cref="OpenMeteoClient"/>; null when the reader is used on its own, since it cannot know when the bytes were fetched.</summary>
    public Provenance? Provenance { get; init; }
}

/// <summary>
/// Reads <c>ensemble-api.open-meteo.com/v1/ensemble</c> daily responses. Assumes one model per
/// request: with several models, member keys gain a model suffix and this reader rejects them
/// (IMPLEMENTATION trap O1).
/// </summary>
public static class EnsembleResponseReader
{
    /// <summary>Parses a response for one or many locations (Open-Meteo returns an array for many).</summary>
    /// <param name="utf8Json">Body.</param>
    /// <param name="variable">Daily variable, for example <c>precipitation_sum</c>.</param>
    /// <returns>One entry per location, in request order.</returns>
    /// <exception cref="JsonException">The body is not an Open-Meteo daily response for <paramref name="variable"/>, or has keys from more than one model.</exception>
    public static IReadOnlyList<EnsembleDaily> ReadDaily(ReadOnlyMemory<byte> utf8Json, string variable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        var result = new List<EnsembleDaily>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in root.EnumerateArray())
            {
                result.Add(ReadLocation(location, variable));
            }
        }
        else
        {
            result.Add(ReadLocation(root, variable));
        }

        return result;
    }

    private static EnsembleDaily ReadLocation(JsonElement location, string variable)
    {
        if (location.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            throw new JsonException("Open-Meteo returned an error object.");
        }

        var daily = location.GetProperty("daily");
        var days = new List<DateOnly>();
        foreach (var t in daily.GetProperty("time").EnumerateArray())
        {
            days.Add(DateOnly.ParseExact(t.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        // Column 0 is the control (no suffix), then _member01, _member02, ...
        var columns = new SortedDictionary<int, JsonElement>();
        var prefix = variable + "_member";
        foreach (var property in daily.EnumerateObject())
        {
            if (property.NameEquals(variable))
            {
                columns[0] = property.Value;
            }
            else if (property.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (!int.TryParse(property.Name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var member))
                {
                    throw new JsonException($"Unexpected member key '{property.Name}'. Request one model at a time.");
                }

                columns[member] = property.Value;
            }
        }

        if (!columns.ContainsKey(0))
        {
            throw new JsonException($"No '{variable}' column.");
        }

        var members = new List<IReadOnlyList<double?>>(days.Count);
        for (var day = 0; day < days.Count; day++)
        {
            var row = new List<double?>(columns.Count);
            foreach (var column in columns.Values)
            {
                var value = column[day];
                row.Add(value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
            }

            members.Add(row);
        }

        return new EnsembleDaily(location.GetProperty("latitude").GetDouble(), location.GetProperty("longitude").GetDouble(), days, members);
    }
}
