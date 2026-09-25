using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Fetch;

/// <summary>
/// One host's traffic state: a single slot, so one request is in flight at a time; the earliest
/// time the next request may start; and the circuit breaker count. Every field is read and
/// written only while the slot is held.
/// </summary>
internal sealed class HostGate(string host, TimeSpan interval, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
    private int _failures;

    public string Host { get; } = host;

    /// <summary>Minimum gap after a request ends.</summary>
    public TimeSpan Interval { get; } = interval;

    /// <summary>Waits for the slot and then for the host's interval. The caller must call <see cref="Exit"/>.</summary>
    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        await _slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _notBefore - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _slot.Release();
            throw;
        }
    }

    /// <summary>True when the breaker is open. Call while holding the slot.</summary>
    public bool IsOpen(int threshold) => _failures >= threshold && time.GetUtcNow() < _openUntil;

    /// <summary>Records the attempt's outcome, sets the earliest next start, and frees the slot.</summary>
    /// <param name="outcome">Whether the attempt resets or adds to the breaker count.</param>
    /// <param name="notBefore">An extra delay the host asked for (Retry-After) or backoff chose, or null.</param>
    /// <param name="threshold">Failures that open the breaker.</param>
    /// <param name="breakFor">How long the breaker stays open.</param>
    public void Exit(AttemptOutcome outcome, DateTimeOffset? notBefore, int threshold, TimeSpan breakFor)
    {
        var now = time.GetUtcNow();
        switch (outcome)
        {
            case AttemptOutcome.Success:
                _failures = 0;
                break;
            case AttemptOutcome.Failure:
                _failures++;
                if (_failures >= threshold)
                {
                    _openUntil = now + breakFor;
                }

                break;
        }

        var next = now + Interval;
        _notBefore = notBefore is { } extra && extra > next ? extra : next;
        _slot.Release();
    }

    /// <summary>Frees the slot when no request was sent, leaving the times and the count as they were.</summary>
    public void Release() => _slot.Release();

    public void Dispose() => _slot.Dispose();
}

/// <summary>How one attempt counts toward a host's circuit breaker.</summary>
internal enum AttemptOutcome
{
    /// <summary>A response that shows the host is healthy; resets the count.</summary>
    Success = 0,

    /// <summary>408, 429, 5xx, 401, 403, a timeout or a connection error.</summary>
    Failure = 1,
}
