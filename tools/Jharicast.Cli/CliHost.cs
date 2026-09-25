using System;
using System.IO;
using System.Net.Http;

namespace Jharicast.Cli;

/// <summary>
/// What the commands touch outside themselves: output, environment, the network and the clock.
/// Tests replace each one, so no test reaches a real host (AGENTS.md rule 2).
/// </summary>
internal sealed class CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required Func<string, string?> Environment { get; init; }

    public required TimeProvider Time { get; init; }

    /// <summary>The handler under <c>PoliteHttpHandler</c> for live runs; null for a real socket handler.</summary>
    public HttpMessageHandler? Network { get; init; }

    /// <summary>Replaces every per-host interval in live runs; null keeps ADR-0007's. Tests only.</summary>
    public TimeSpan? MinIntervalOverride { get; init; }

    public static CliHost Console() => new()
    {
        Out = System.Console.Out,
        Error = System.Console.Error,
        Environment = System.Environment.GetEnvironmentVariable,
        Time = TimeProvider.System,
    };
}
