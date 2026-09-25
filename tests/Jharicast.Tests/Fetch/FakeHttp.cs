using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace Jharicast.Tests.Fetch;

/// <summary>One request as the fake server saw it.</summary>
internal sealed record SeenRequest(Uri Uri, DateTimeOffset At, string UserAgent);

/// <summary>
/// Stands in for the network. Every test answers from this handler; nothing leaves the process
/// (AGENTS.md rule 2).
/// </summary>
internal sealed class FakeServer(TimeProvider time, Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _count;
    private int _inFlight;

    public ConcurrentQueue<SeenRequest> Seen { get; } = new();

    public int MaxInFlight { get; private set; }

    public int Count => Volatile.Read(ref _count);

    public static FakeServer Always(TimeProvider time, HttpStatusCode status) =>
        new(time, (_, _) => Task.FromResult(new HttpResponseMessage(status)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _count) - 1;
        var inFlight = Interlocked.Increment(ref _inFlight);
        MaxInFlight = Math.Max(MaxInFlight, inFlight);
        Seen.Enqueue(new SeenRequest(request.RequestUri!, time.GetUtcNow(), string.Join(' ', request.Headers.GetValues("User-Agent"))));
        try
        {
            return await respond(request, index);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

internal static class FakeClock
{
    /// <summary>
    /// Advances fake time in small steps, yielding real time between them so continuations run,
    /// until the task finishes. A step size bounds the error of any time a test reads back.
    /// </summary>
    public static async Task<T> Drive<T>(this FakeTimeProvider time, Task<T> task, TimeSpan? step = null, TimeSpan? limit = null)
    {
        var by = step ?? TimeSpan.FromMilliseconds(100);
        var end = time.GetUtcNow() + (limit ?? TimeSpan.FromMinutes(1));
        while (!task.IsCompleted)
        {
            await Task.Delay(2);
            if (task.IsCompleted)
            {
                break;
            }

            if (time.GetUtcNow() >= end)
            {
                throw new TimeoutException("The task did not finish within the fake time limit.");
            }

            time.Advance(by);
        }

        return await task;
    }

    public static Task Drive(this FakeTimeProvider time, Task task, TimeSpan? step = null, TimeSpan? limit = null) =>
        time.Drive(Wrap(task), step, limit);

    public static IReadOnlyList<SeenRequest> List(this ConcurrentQueue<SeenRequest> seen) => [.. seen];

    private static async Task<bool> Wrap(Task task)
    {
        await task;
        return true;
    }
}
