using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Tests.Nepal;

/// <summary>
/// Answers Open-Meteo's forecast, ensemble and elevation requests in their documented shape, for
/// whatever points were asked, from functions. Nothing leaves the process (AGENTS.md rule 2).
/// </summary>
/// <param name="start">First forecast day, Nepal time.</param>
/// <param name="deterministic">Value for (model, variable, point, day).</param>
/// <param name="member">Value for (model, variable, point, day, member), member 0 being the control.</param>
/// <param name="elevation">Metres for a point.</param>
internal sealed class FakeOpenMeteo(
    DateOnly start,
    Func<string, string, GeoPoint, DateOnly, double?> deterministic,
    Func<string, string, GeoPoint, DateOnly, int, double?> member,
    Func<GeoPoint, double> elevation) : HttpMessageHandler
{
    public static readonly Dictionary<string, int> MemberCounts = new(StringComparer.Ordinal) { ["ecmwf_ifs025"] = 51, ["gfs025"] = 31 };

    public ConcurrentQueue<Uri> Seen { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Seen.Enqueue(uri);
        var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        var points = query["latitude"].Split(',').Zip(query["longitude"].Split(','))
            .Select(p => new GeoPoint(double.Parse(p.First, CultureInfo.InvariantCulture), double.Parse(p.Second, CultureInfo.InvariantCulture)))
            .ToArray();

        string body = uri.AbsolutePath switch
        {
            "/v1/elevation" => JsonSerializer.Serialize(new Dictionary<string, double[]> { ["elevation"] = [.. points.Select(elevation)] }),
            "/v1/forecast" => Locations(points, query, (point, days, daily) =>
            {
                var models = query["models"].Split(',');
                foreach (var model in models)
                {
                    foreach (var variable in query["daily"].Split(','))
                    {
                        daily[models.Length == 1 ? variable : variable + "_" + model] = [.. days.Select(d => deterministic(model, variable, point, d))];
                    }
                }
            }),
            "/v1/ensemble" => Locations(points, query, (point, days, daily) =>
            {
                var model = query["models"];
                foreach (var variable in query["daily"].Split(','))
                {
                    for (var m = 0; m < MemberCounts[model]; m++)
                    {
                        daily[m == 0 ? variable : string.Create(CultureInfo.InvariantCulture, $"{variable}_member{m:00}")] = [.. days.Select(d => member(model, variable, point, d, m))];
                    }
                }
            }),
            _ => throw new InvalidOperationException("Unexpected request " + uri),
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private string Locations(GeoPoint[] points, Dictionary<string, string> query, Action<GeoPoint, DateOnly[], Dictionary<string, double?[]>> fill)
    {
        var count = int.Parse(query["forecast_days"], CultureInfo.InvariantCulture);
        DateOnly[] days = [.. Enumerable.Range(0, count).Select(start.AddDays)];
        var locations = points.Select(p =>
        {
            var daily = new Dictionary<string, object>(StringComparer.Ordinal) { ["time"] = days.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray() };
            var values = new Dictionary<string, double?[]>(StringComparer.Ordinal);
            fill(p, days, values);
            foreach (var (key, list) in values)
            {
                daily[key] = list;
            }

            return new Dictionary<string, object> { ["latitude"] = p.Latitude, ["longitude"] = p.Longitude, ["utc_offset_seconds"] = 20700, ["daily"] = daily };
        }).ToArray();
        return locations.Length == 1 ? JsonSerializer.Serialize(locations[0]) : JsonSerializer.Serialize(locations);
    }
}
