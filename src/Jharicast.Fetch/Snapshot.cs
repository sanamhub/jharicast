using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;

namespace Jharicast.Fetch;

/// <summary>
/// The exact bytes one fetch returned, with where and when, so any later result can be traced to
/// them (ADR-0008). Bodies with personal data must be scrubbed before a snapshot is made.
/// </summary>
public sealed record Snapshot
{
    /// <summary>Creates a snapshot and computes <see cref="Sha256"/>.</summary>
    /// <param name="sourceId">Stable source id, for example <c>dhm.warnings</c>: lower-case letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, starting with a letter or digit.</param>
    /// <param name="url">The URL fetched.</param>
    /// <param name="fetchedAt">Fetch time; stored as UTC.</param>
    /// <param name="status">HTTP status.</param>
    /// <param name="headers">Selected response headers (<c>ETag</c>, <c>Last-Modified</c>, <c>Content-Type</c>). Keys are case-insensitive.</param>
    /// <param name="body">The body as stored.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceId"/> is blank or has other characters.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> or <paramref name="headers"/> is null.</exception>
    public Snapshot(string sourceId, Uri url, DateTimeOffset fetchedAt, HttpStatusCode status, IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);
        // The id names a directory in FileSnapshotStore, so ".." or "a/b" must never get through.
        if (!char.IsAsciiLetterLower(sourceId[0]) && !char.IsAsciiDigit(sourceId[0])
            || sourceId.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_')))
        {
            throw new ArgumentException($"Source id '{sourceId}' must start with a to z or 0 to 9 and contain only those, '.', '-' and '_'.", nameof(sourceId));
        }

        SourceId = sourceId;
        Url = url;
        FetchedAt = fetchedAt.ToUniversalTime();
        Status = status;
        Headers = headers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Body = body;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData((JsonCanonicalizer.TryCanonicalize(body) ?? body.ToArray()).AsSpan()));
    }

    /// <summary>Source id.</summary>
    public string SourceId { get; }

    /// <summary>The URL fetched.</summary>
    public Uri Url { get; }

    /// <summary>Fetch time, UTC.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>HTTP status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Selected response headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The body as stored.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>
    /// Lower-case hex SHA-256 of the canonical body: JSON with sorted keys and no whitespace, any
    /// other body as is. Equal hashes mean the content did not change.
    /// </summary>
    public string Sha256 { get; }
}
