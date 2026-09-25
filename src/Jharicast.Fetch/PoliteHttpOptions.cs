using System;
using System.Collections.Generic;

namespace Jharicast.Fetch;

/// <summary>
/// Settings for <see cref="PoliteHttpHandler"/> (ADR-0007). The defaults are the ADR's, and
/// <see cref="MaxRetries"/> cannot go above its limit of 3.
/// </summary>
public sealed class PoliteHttpOptions
{
    /// <summary>
    /// Sent on every request, for example
    /// <c>Jharicast/0.1 (+https://github.com/sanamhub/jharicast; ops@example.org)</c>. Must name a
    /// project URL (<c>+http</c>) and a contact mailbox (<c>@</c>). Use the project mailbox, never
    /// a personal address. There is no default, so a site owner can always reach whoever runs it.
    /// </summary>
    /// <exception cref="ArgumentException">Blank, or missing <c>+http</c> or <c>@</c>.</exception>
    public required string UserAgent
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (!value.Contains("+http", StringComparison.Ordinal) || !value.Contains('@', StringComparison.Ordinal))
            {
                throw new ArgumentException("The User-Agent must contain a project URL (+http...) and a contact mailbox (@).", nameof(value));
            }

            field = value;
        }
    }

    /// <summary>Minimum time between the end of one request to a host and the start of the next. Default 1 second.</summary>
    public TimeSpan DefaultMinInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-host intervals that replace <see cref="DefaultMinInterval"/>, keyed by host name, for example <c>dhm.gov.np</c> 5 seconds. Case-insensitive.</summary>
    public Dictionary<string, TimeSpan> HostOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Retries after the first attempt, on 408, 429, 5xx and connection errors only. Default 3, the ADR's maximum.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Below 0 or above 3.</exception>
    public int MaxRetries
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 3);
            field = value;
        }
    } = 3;

    /// <summary>Consecutive failed attempts to one host that open its circuit breaker. Default 5.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Below 1.</exception>
    public int BreakerFailures
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 5;

    /// <summary>How long an open breaker refuses requests to its host. Default 10 minutes.</summary>
    public TimeSpan BreakerDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Hosts that are never requested: the kill switch, settable from configuration without a deploy. Case-insensitive.</summary>
    public HashSet<string> DisabledHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clock for intervals, backoff and the breaker. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal TimeSpan IntervalFor(string host) =>
        HostOverrides.TryGetValue(host, out var interval) ? interval : DefaultMinInterval;
}
