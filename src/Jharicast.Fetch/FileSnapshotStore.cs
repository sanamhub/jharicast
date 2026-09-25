using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Fetch;

/// <summary>Stores snapshots append-only (ADR-0008). Retention is the consumer's policy.</summary>
public interface ISnapshotStore
{
    /// <summary>Saves a snapshot. Never replaces an earlier one.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the snapshot is stored.</returns>
    Task SaveAsync(Snapshot snapshot, CancellationToken cancellationToken);

    /// <summary>The most recent snapshot of a source, by fetch time.</summary>
    /// <param name="sourceId">Source id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The snapshot, or null when none is stored.</returns>
    Task<Snapshot?> LatestAsync(string sourceId, CancellationToken cancellationToken);
}

/// <summary>
/// Writes each snapshot as <c>yyyy/MM/dd/&lt;source&gt;/&lt;HHmmss&gt;-&lt;hash8&gt;.json.gz</c> under a root
/// directory, UTC. Meant for the CLI and development; services provide blob storage.
/// </summary>
/// <param name="rootDirectory">Where to write. Created when missing.</param>
public sealed class FileSnapshotStore(string rootDirectory) : ISnapshotStore
{
    private readonly string _root = Path.GetFullPath(rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory)));

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async Task SaveAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var at = snapshot.FetchedAt;
        var directory = Path.Combine(_root, at.ToString("yyyy", CultureInfo.InvariantCulture), at.ToString("MM", CultureInfo.InvariantCulture), at.ToString("dd", CultureInfo.InvariantCulture), snapshot.SourceId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{at.ToString("HHmmss", CultureInfo.InvariantCulture)}-{snapshot.Sha256[..8]}.json.gz");
        if (File.Exists(path))
        {
            return; // same source, same second, same content: already stored
        }

        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            var gzip = new GZipStream(file, CompressionLevel.Optimal);
            await using (gzip.ConfigureAwait(false))
            {
                var writer = new Utf8JsonWriter(gzip);
                await using (writer.ConfigureAwait(false))
                {
                    Write(snapshot, writer);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="sourceId"/> is blank.</exception>
    /// <exception cref="JsonException">The newest file for the source is not a snapshot.</exception>
    public async Task<Snapshot?> LatestAsync(string sourceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var newest = Descending(_root)
            .SelectMany(Descending)
            .SelectMany(Descending)
            .Select(day => Path.Combine(day, sourceId))
            .Where(Directory.Exists)
            .Select(dir => Directory.EnumerateFiles(dir, "*.json.gz").Order(StringComparer.Ordinal).LastOrDefault())
            .FirstOrDefault(file => file is not null);
        if (newest is null)
        {
            return null;
        }

        var file = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using (gzip.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(gzip, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Read(document.RootElement);
            }
        }
    }

    private static IEnumerable<string> Descending(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).Reverse() : [];

    // Written by hand rather than through a serializer, so the store stays free of reflection and
    // AOT-clean without a source-generated context.
    private static void Write(Snapshot snapshot, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("sourceId", snapshot.SourceId);
        writer.WriteString("url", snapshot.Url.AbsoluteUri);
        writer.WriteString("fetchedAt", snapshot.FetchedAt);
        writer.WriteNumber("status", (int)snapshot.Status);
        writer.WriteStartObject("headers");
        foreach (var (name, value) in snapshot.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteString(name, value);
        }

        writer.WriteEndObject();
        writer.WriteString("sha256", snapshot.Sha256);
        writer.WriteBase64String("body", snapshot.Body.Span);
        writer.WriteEndObject();
    }

    private static Snapshot Read(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in root.GetProperty("headers").EnumerateObject())
        {
            headers[header.Name] = header.Value.GetString() ?? string.Empty;
        }

        return new Snapshot(
            root.GetProperty("sourceId").GetString()!,
            new Uri(root.GetProperty("url").GetString()!),
            root.GetProperty("fetchedAt").GetDateTimeOffset(),
            (HttpStatusCode)root.GetProperty("status").GetInt32(),
            headers,
            root.GetProperty("body").GetBytesFromBase64());
    }
}
