using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.OpenMeteo;

/// <summary>
/// Calls Open-Meteo's forecast, ensemble and elevation APIs (ADR-0012). Give it an
/// <see cref="HttpClient"/> built on <c>Jharicast.Fetch.PoliteHttpHandler</c>, so the calls are
/// identified and spaced. Results come back in the order of the points asked for: Open-Meteo snaps
/// coordinates to its grid, so match by position, not by latitude and longitude. Model results are
/// kept per point until the model's next run is expected, and elevations for the life of the
/// client, so asking again costs no call.
/// </summary>
public sealed class OpenMeteoClient
{
    // ADR-0012: up to 50 locations per request. Each still counts against the daily limit.
    internal const int MaxPointsPerRequest = 50;

    // ADR-0010: the elevation API takes 100 points per call.
    internal const int MaxElevationPointsPerRequest = 100;

    private readonly HttpClient _http;
    private readonly OpenMeteoOptions _options;
    private readonly ModelRunCache _cache;
    private readonly ConcurrentDictionary<(long, long), double> _elevations = new();

    /// <summary>Creates the client.</summary>
    /// <param name="httpClient">The client that sends requests; not disposed by this class.</param>
    /// <param name="options">Hosts, key, time zone and clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public OpenMeteoClient(HttpClient httpClient, OpenMeteoOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _http = httpClient;
        _options = options;
        _cache = new ModelRunCache(options.TimeProvider, options.ModelRunLag);
    }

    /// <summary>
    /// Daily ensemble members for one model, for example <c>ecmwf_ifs025</c> or <c>gfs025</c>. One
    /// model per request, because several models rename every member key (ADR-0012).
    /// </summary>
    /// <param name="points">Locations, at least one. Batched 50 per request.</param>
    /// <param name="model">One ensemble model id.</param>
    /// <param name="variables">Daily variables, for example <c>precipitation_sum</c> and <c>wind_gusts_10m_max</c>.</param>
    /// <param name="days">Forecast days, 1 to 16.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Per variable, one entry per point in the order given, each with provenance <c>open-meteo.ensemble.&lt;model&gt;</c>.</returns>
    /// <exception cref="ArgumentException">No point, no variable, or an id that is not lower-case letters, digits and underscores (a comma, for several models, is refused).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="days"/> is outside 1 to 16.</exception>
    /// <exception cref="HttpRequestException">Open-Meteo answered with an error status; the message carries its reason.</exception>
    /// <exception cref="JsonException">The response is not the expected shape.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<EnsembleDaily>>> GetEnsembleDailyAsync(
        IReadOnlyList<GeoPoint> points, string model, IReadOnlyList<string> variables, int days, CancellationToken cancellationToken)
    {
        Validate(points, [model], variables, days);
        var result = variables.ToDictionary(v => v, _ => new EnsembleDaily[points.Count], StringComparer.Ordinal);
        var missing = Enumerable.Range(0, points.Count).Where(i => !variables.All(v =>
        {
            var hit = _cache.TryGet(ModelRunCache.Key.For("ensemble", model, points[i], v), days, out EnsembleDaily cached);
            result[v][i] = hit ? Trim(cached, days) : null!;
            return hit;
        })).ToArray();

        foreach (var batch in missing.Chunk(MaxPointsPerRequest))
        {
            var uri = BuildUri(_options.EnsembleBaseAddress, "v1/ensemble", [.. batch.Select(i => points[i])], [model], variables, days);
            var (body, fetchedAt) = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var offsets = UtcOffsets(body);
            CheckCount(offsets.Count, batch.Length);
            var provenance = new Provenance($"open-meteo.ensemble.{model}", SourceKind.Model, fetchedAt);
            foreach (var variable in variables)
            {
                var locations = EnsembleResponseReader.ReadDaily(body, variable);
                CheckCount(locations.Count, batch.Length);
                for (var j = 0; j < batch.Length; j++)
                {
                    var located = locations[j] with { Provenance = provenance };
                    _cache.Set(ModelRunCache.Key.For("ensemble", model, points[batch[j]], variable), located, located.Days.Count, fetchedAt, offsets[j]);
                    result[variable][batch[j]] = Trim(located, days);
                }
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EnsembleDaily>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Daily values from deterministic models, for example <c>ecmwf_ifs025</c>, <c>gfs_seamless</c>
    /// and <c>icon_seamless</c>, in one request per batch of points.
    /// </summary>
    /// <param name="points">Locations, at least one. Batched 50 per request.</param>
    /// <param name="models">Deterministic model ids, at least one.</param>
    /// <param name="variables">Daily variables, for example <c>precipitation_sum</c> and <c>wind_gusts_10m_max</c>.</param>
    /// <param name="days">Forecast days, 1 to 16.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Per model, one entry per point in the order given, each with provenance <c>open-meteo.forecast.&lt;model&gt;</c>.</returns>
    /// <exception cref="ArgumentException">No point, model or variable, or an id that is not lower-case letters, digits and underscores.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="days"/> is outside 1 to 16.</exception>
    /// <exception cref="HttpRequestException">Open-Meteo answered with an error status; the message carries its reason.</exception>
    /// <exception cref="JsonException">The response is not the expected shape, or lacks a requested model and variable.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ForecastDaily>>> GetDailyAsync(
        IReadOnlyList<GeoPoint> points, IReadOnlyList<string> models, IReadOnlyList<string> variables, int days, CancellationToken cancellationToken)
    {
        Validate(points, models, variables, days);
        var result = models.ToDictionary(m => m, _ => new ForecastDaily[points.Count], StringComparer.Ordinal);
        var missing = Enumerable.Range(0, points.Count).Where(i => !models.All(m =>
        {
            var hit = _cache.TryGet(ModelRunCache.Key.For("forecast", m, points[i], string.Join(',', variables)), days, out ForecastDaily cached);
            result[m][i] = hit ? Trim(cached, days) : null!;
            return hit;
        })).ToArray();

        foreach (var batch in missing.Chunk(MaxPointsPerRequest))
        {
            var uri = BuildUri(_options.BaseAddress, "v1/forecast", [.. batch.Select(i => points[i])], models, variables, days);
            var (body, fetchedAt) = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var offsets = UtcOffsets(body);
            CheckCount(offsets.Count, batch.Length);
            foreach (var (model, locations) in ForecastResponseReader.ReadDaily(body, models, variables))
            {
                CheckCount(locations.Count, batch.Length);
                var provenance = new Provenance($"open-meteo.forecast.{model}", SourceKind.Model, fetchedAt);
                for (var j = 0; j < batch.Length; j++)
                {
                    var located = locations[j] with { Provenance = provenance };
                    _cache.Set(ModelRunCache.Key.For("forecast", model, points[batch[j]], string.Join(',', variables)), located, located.Days.Count, fetchedAt, offsets[j]);
                    result[model][batch[j]] = Trim(located, days);
                }
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ForecastDaily>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Ground elevation from Open-Meteo's elevation API (Copernicus DEM, 90 m), 100 points per
    /// call. Elevations do not change, so each rounded point is asked for once per client.
    /// </summary>
    /// <param name="points">Locations, at least one.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Metres above sea level, one per point in the order given.</returns>
    /// <exception cref="ArgumentException"><paramref name="points"/> is empty.</exception>
    /// <exception cref="HttpRequestException">Open-Meteo answered with an error status; the message carries its reason.</exception>
    /// <exception cref="JsonException">The response is not an elevation array of the right length.</exception>
    public async Task<IReadOnlyList<double>> GetElevationAsync(IReadOnlyList<GeoPoint> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is needed.", nameof(points));
        }

        static (long, long) Key(GeoPoint p) => ((long)Math.Round(p.Latitude * 1000), (long)Math.Round(p.Longitude * 1000));
        var missing = points.Where(p => !_elevations.ContainsKey(Key(p))).DistinctBy(Key).ToArray();
        foreach (var batch in missing.Chunk(MaxElevationPointsPerRequest))
        {
            var query = new StringBuilder()
                .Append("latitude=").AppendJoin(',', batch.Select(p => Coordinate(p.Latitude)))
                .Append("&longitude=").AppendJoin(',', batch.Select(p => Coordinate(p.Longitude)));
            AppendApiKey(query);
            var (body, _) = await GetAsync(new Uri(_options.BaseAddress, $"v1/elevation?{query}"), cancellationToken).ConfigureAwait(false);
            var elevations = ReadElevations(body);
            CheckCount(elevations.Count, batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                _elevations[Key(batch[i])] = elevations[i];
            }
        }

        return [.. points.Select(p => _elevations[Key(p)])];
    }

    internal Uri BuildUri(Uri baseAddress, string path, IReadOnlyCollection<GeoPoint> points, IReadOnlyList<string> models, IReadOnlyList<string> variables, int days)
    {
        var query = new StringBuilder()
            .Append("latitude=").AppendJoin(',', points.Select(p => Coordinate(p.Latitude)))
            .Append("&longitude=").AppendJoin(',', points.Select(p => Coordinate(p.Longitude)))
            .Append("&daily=").AppendJoin(',', variables)
            .Append("&models=").AppendJoin(',', models)
            .Append("&forecast_days=").Append(days.ToString(CultureInfo.InvariantCulture))
            .Append("&timezone=").Append(Uri.EscapeDataString(_options.TimeZone))
            .Append("&wind_speed_unit=kmh");
        AppendApiKey(query);
        return new Uri(baseAddress, $"{path}?{query}");
    }

    private void AppendApiKey(StringBuilder query)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            query.Append("&apikey=").Append(Uri.EscapeDataString(_options.ApiKey));
        }
    }

    private static EnsembleDaily Trim(EnsembleDaily daily, int days) =>
        daily.Days.Count == days ? daily : daily with { Days = [.. daily.Days.Take(days)], Members = [.. daily.Members.Take(days)] };

    private static ForecastDaily Trim(ForecastDaily daily, int days) =>
        daily.Days.Count == days
            ? daily
            : daily with
            {
                Days = [.. daily.Days.Take(days)],
                Variables = daily.Variables.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double?>)[.. kv.Value.Take(days)], StringComparer.Ordinal),
            };

    // Each location's offset from UTC, so a cached day can be matched to the local calendar.
    private static List<TimeSpan> UtcOffsets(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var locations = root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray()] : new[] { root };
        return [.. locations.Select(l => l.TryGetProperty("utc_offset_seconds", out var s) && s.TryGetInt32(out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero)];
    }

    private static List<double> ReadElevations(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("elevation", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("No elevation array: not an elevation response.");
        }

        return [.. array.EnumerateArray().Select(e => e.GetDouble())];
    }

    // Invariant, always: under ne-NP or de-DE, 27.5 can format as "27,5" and move the point.
    private static string Coordinate(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private async Task<(byte[] Body, DateTimeOffset FetchedAt)> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Open-Meteo answered {(int)response.StatusCode}: {Reason(body) ?? "no reason given"}.", null, response.StatusCode);
        }

        return (body, _options.TimeProvider.GetUtcNow());
    }

    private static string? Reason(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CheckCount(int received, int requested)
    {
        if (received != requested)
        {
            throw new JsonException($"Open-Meteo returned {received} locations for {requested} requested.");
        }
    }

    private static void Validate(IReadOnlyList<GeoPoint> points, IReadOnlyList<string> models, IReadOnlyList<string> variables, int days)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(variables);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is needed.", nameof(points));
        }

        if (models.Count == 0 || variables.Count == 0)
        {
            throw new ArgumentException("At least one model and one variable are needed.", models.Count == 0 ? nameof(models) : nameof(variables));
        }

        foreach (var id in models.Concat(variables))
        {
            if (string.IsNullOrEmpty(id) || !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                throw new ArgumentException($"'{id}' is not an Open-Meteo id: use lower-case letters, digits and underscores, one model per entry.", models.Contains(id) ? nameof(models) : nameof(variables));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, 16);
    }
}
