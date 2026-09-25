using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jharicast.OpenMeteo;

/// <summary>Daily values of one deterministic model for one location.</summary>
/// <param name="Model">Model id, for example <c>ecmwf_ifs025</c>.</param>
/// <param name="Latitude">Grid latitude Open-Meteo snapped to (not the requested one).</param>
/// <param name="Longitude">Grid longitude.</param>
/// <param name="Days">Local dates, in the request's time zone.</param>
/// <param name="Variables">Values per variable, one per day; null where the model has no value.</param>
public sealed record ForecastDaily(string Model, double Latitude, double Longitude, IReadOnlyList<DateOnly> Days, IReadOnlyDictionary<string, IReadOnlyList<double?>> Variables)
{
    /// <summary>Source, kind and fetch time. Set by <see cref="OpenMeteoClient"/>.</summary>
    public Provenance? Provenance { get; init; }
}

/// <summary>
/// Reads <c>api.open-meteo.com/v1/forecast</c> daily responses for one or several deterministic
/// models. With several models every key is <c>&lt;variable&gt;_&lt;model&gt;</c>; with one, the key
/// may be the bare variable.
/// </summary>
internal static class ForecastResponseReader
{
    /// <summary>One list per model, each with one entry per location in request order.</summary>
    /// <exception cref="JsonException">Not a daily forecast response, an error object, or a requested model and variable pair is missing.</exception>
    public static IReadOnlyDictionary<string, IReadOnlyList<ForecastDaily>> ReadDaily(ReadOnlyMemory<byte> utf8Json, IReadOnlyList<string> models, IReadOnlyList<string> variables)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        var locations = root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray()] : new[] { root };
        var result = new Dictionary<string, IReadOnlyList<ForecastDaily>>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var perLocation = new List<ForecastDaily>(locations.Length);
            foreach (var location in locations)
            {
                perLocation.Add(ReadLocation(location, model, models.Count == 1, variables));
            }

            result[model] = perLocation;
        }

        return result;
    }

    private static ForecastDaily ReadLocation(JsonElement location, string model, bool onlyModel, IReadOnlyList<string> variables)
    {
        if (location.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            var reason = location.TryGetProperty("reason", out var r) ? r.GetString() : null;
            throw new JsonException($"Open-Meteo returned an error: {reason ?? "no reason given"}.");
        }

        if (!location.TryGetProperty("daily", out var daily) || !daily.TryGetProperty("time", out var time))
        {
            throw new JsonException("No daily.time array: not a daily forecast response.");
        }

        var days = new List<DateOnly>();
        foreach (var t in time.EnumerateArray())
        {
            days.Add(DateOnly.ParseExact(t.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var values = new Dictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            if (!daily.TryGetProperty($"{variable}_{model}", out var column) && !(onlyModel && daily.TryGetProperty(variable, out column)))
            {
                throw new JsonException($"No '{variable}_{model}' column. Check the model id and the variable name.");
            }

            var series = new List<double?>(days.Count);
            foreach (var value in column.EnumerateArray())
            {
                series.Add(value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
            }

            if (series.Count != days.Count)
            {
                throw new JsonException($"'{variable}_{model}' has {series.Count} values for {days.Count} days.");
            }

            values[variable] = series;
        }

        return new ForecastDaily(model, location.GetProperty("latitude").GetDouble(), location.GetProperty("longitude").GetDouble(), days, values);
    }
}
