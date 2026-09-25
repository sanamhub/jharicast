using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Cli;

/// <summary>
/// Answers every request from files under a directory, for <c>--fixtures</c>. Nothing leaves the
/// process. A request for <c>https://host/a/b/c</c> is answered by the first file that exists of
/// <c>host/a/b/c.&lt;models&gt;.json</c> (when the query names one model), <c>host/a/b/c.json</c>,
/// <c>host/a/b.json</c>, <c>host/a.json</c>; with none, 404.
/// </summary>
/// <remarks>
/// Open-Meteo answers one entry per requested location, and a recording cannot know the sample
/// points of a later route. So for a request with several <c>latitude</c> values, a recorded
/// object or array is repeated, entry by entry, to that many locations, and an
/// <c>elevation</c> array is cycled to that length. Every point gets recorded values; the output
/// says the run was a replay.
/// </remarks>
internal sealed class FixtureHandler(string root) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var file = Candidates(uri).FirstOrDefault(File.Exists);
        if (file is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }

        var body = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var query = Query(uri);
        if (query.TryGetValue("latitude", out var latitudes))
        {
            body = Repeat(body, latitudes.Split(',').Length);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request };
    }

    internal IEnumerable<string> Candidates(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var models = Query(uri).TryGetValue("models", out var m) && !m.Contains(',', StringComparison.Ordinal) ? m : null;
        for (var n = segments.Length; n > 0; n--)
        {
            var path = Path.Combine([root, uri.Host, .. segments.Take(n)]);
            if (n == segments.Length && models is not null)
            {
                yield return path + "." + models + ".json";
            }

            yield return path + ".json";
        }
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty, StringComparer.Ordinal);

    private static byte[] Repeat(byte[] body, int count)
    {
        var node = JsonNode.Parse(body);
        if (node is JsonObject { } obj && obj["elevation"] is JsonArray elevations && elevations.Count > 0)
        {
            obj["elevation"] = new JsonArray([.. Enumerable.Range(0, count).Select(i => elevations[i % elevations.Count]!.DeepClone())]);
            return System.Text.Encoding.UTF8.GetBytes(obj.ToJsonString());
        }

        JsonNode[] entries = node switch
        {
            JsonArray array when array.Count > 0 => [.. array.Select(e => e!)],
            JsonObject single => [single],
            _ => [],
        };
        if (entries.Length == 0)
        {
            return body;
        }

        if (count == 1)
        {
            return System.Text.Encoding.UTF8.GetBytes(entries[0].ToJsonString());
        }

        var repeated = new JsonArray([.. Enumerable.Range(0, count).Select(i => entries[i % entries.Length].DeepClone())]);
        return System.Text.Encoding.UTF8.GetBytes(repeated.ToJsonString());
    }
}
