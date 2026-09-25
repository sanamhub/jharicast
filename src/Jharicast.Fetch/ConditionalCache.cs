using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Fetch;

/// <summary>
/// Conditional GET per URL (ADR-0007): remembers <c>ETag</c>, <c>Last-Modified</c> and the body of
/// the last 200, sends them back as <c>If-None-Match</c> and <c>If-Modified-Since</c>, and turns a
/// 304 into the remembered 200, so a caller never sees the difference. A response marked
/// <c>no-store</c> is not kept; DHM sends that, and the snapshot hash covers it instead.
/// </summary>
internal sealed class ConditionalCache
{
    // Feeds are kilobytes to a few megabytes. Anything larger is not worth holding in memory.
    private const long MaxBodyBytes = 16 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Adds validators to a GET for a URL seen before, unless the caller set its own.</summary>
    public void Prepare(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get
            || request.Headers.IfNoneMatch.Count > 0
            || request.Headers.IfModifiedSince is not null
            || !_entries.TryGetValue(Key(request), out var entry))
        {
            return;
        }

        if (entry.ETag is not null)
        {
            request.Headers.IfNoneMatch.Add(entry.ETag);
        }

        if (entry.LastModified is { } lastModified)
        {
            request.Headers.IfModifiedSince = lastModified;
        }
    }

    /// <summary>Stores a cacheable 200, or answers a 304 from the store.</summary>
    public async Task<HttpResponseMessage> CompleteAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
        {
            return response;
        }

        var key = Key(request);
        if (response.StatusCode == HttpStatusCode.NotModified && _entries.TryGetValue(key, out var cached))
        {
            response.Dispose();
            return cached.ToResponse(request);
        }

        var etag = response.Headers.ETag;
        var lastModified = response.Content.Headers.LastModified;
        if (response.StatusCode != HttpStatusCode.OK
            || (etag is null && lastModified is null)
            || response.Headers.CacheControl?.NoStore == true
            || response.Content.Headers.ContentLength > MaxBodyBytes)
        {
            return response;
        }

        // Reading buffers the content, so the caller can still read the body afterwards.
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.LongLength <= MaxBodyBytes)
        {
            _entries[key] = new Entry(etag, lastModified, body, response.Content.Headers.ContentType);
        }

        return response;
    }

    private static string Key(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri;

    private sealed record Entry(EntityTagHeaderValue? ETag, DateTimeOffset? LastModified, byte[] Body, MediaTypeHeaderValue? ContentType)
    {
        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            var content = new ByteArrayContent(Body);
            content.Headers.ContentType = ContentType;
            content.Headers.LastModified = LastModified;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            response.Headers.ETag = ETag;
            return response;
        }
    }
}
