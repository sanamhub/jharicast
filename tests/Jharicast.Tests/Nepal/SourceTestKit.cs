using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Tests.Nepal;

/// <summary>Answers every request from a function. Nothing leaves the process (AGENTS.md rule 2).</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<Uri> Seen { get; } = new();

    public static StubHandler Json(Func<int, byte[]> body)
    {
        var count = 0;
        return new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body(count++)) });
    }

    public static StubHandler Status(HttpStatusCode status) => new(_ => new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Enqueue(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}

/// <summary>Keeps snapshots in memory, in the order saved.</summary>
internal sealed class MemorySnapshotStore : ISnapshotStore
{
    public List<Snapshot> Saved { get; } = [];

    public Task SaveAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        Saved.Add(snapshot);
        return Task.CompletedTask;
    }

    public Task<Snapshot?> LatestAsync(string sourceId, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.Where(s => s.SourceId == sourceId).OrderBy(s => s.FetchedAt).LastOrDefault());
}

internal static class Fixtures
{
    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
