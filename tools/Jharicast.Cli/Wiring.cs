using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;
using Jharicast.OpenMeteo;
using Jharicast.Routing;

namespace Jharicast.Cli;

/// <summary>Options every command shares, as parsed.</summary>
internal sealed record CommonSettings(string? Fixtures, DateTimeOffset? Now, string? Contact, string? Snapshots, IReadOnlyList<string> DisabledHosts, Uri Osrm);

/// <summary>
/// Builds the HTTP client and the sources for one run. Live runs go through one
/// <see cref="PoliteHttpHandler"/>, so every host sees one request at a time at ADR-0007's
/// interval. <c>--fixtures</c> runs answer from files and never open a socket.
/// </summary>
internal sealed class Wiring : IDisposable
{
    internal const string ContactVariable = "JHARICAST_CONTACT";
    internal const string RepositoryUrl = "https://github.com/sanamhub/jharicast";

    // ADR-0007: 5 s between requests to a government host.
    private static readonly string[] GovernmentHosts = ["dhm.gov.np", "navigate.dor.gov.np", "bipadportal.gov.np"];

    private readonly HttpClient _http;

    private Wiring(HttpClient http, TimeProvider time, ISnapshotStore store, Uri osrm, bool replay)
    {
        _http = http;
        Time = time;
        Store = store;
        Osrm = osrm;
        IsReplay = replay;
    }

    public TimeProvider Time { get; }

    public ISnapshotStore Store { get; }

    public Uri Osrm { get; }

    /// <summary>True when answers come from <c>--fixtures</c>.</summary>
    public bool IsReplay { get; }

    public HttpClient Http => _http;

    /// <exception cref="CliException">A live run with no contact mailbox, a bad one, or <c>--now</c> without <c>--fixtures</c>.</exception>
    // Each HttpClient owns and disposes its handler chain, and Dispose below disposes the client.
#pragma warning disable CA2000
    public static Wiring Create(CommonSettings settings, CliHost host)
    {
        if (settings.Fixtures is { } dir)
        {
            if (!Directory.Exists(dir))
            {
                throw new CliException($"--fixtures: '{dir}' is not a directory.");
            }

            var time = settings.Now is { } now ? new FixedTime(now) : host.Time;
            return new Wiring(new HttpClient(new FixtureHandler(dir)), time, new MemoryStore(), settings.Osrm, replay: true);
        }

        if (settings.Now is not null)
        {
            throw new CliException("--now replays recorded data and needs --fixtures. A live run uses the real clock.");
        }

        var options = new PoliteHttpOptions
        {
            UserAgent = UserAgent(settings.Contact ?? host.Environment(ContactVariable)),
            TimeProvider = host.Time,
            DefaultMinInterval = host.MinIntervalOverride ?? TimeSpan.FromSeconds(1),
        };
        if (host.MinIntervalOverride is null)
        {
            foreach (var government in GovernmentHosts)
            {
                options.HostOverrides[government] = TimeSpan.FromSeconds(5);
            }
        }

        foreach (var disabled in settings.DisabledHosts)
        {
            options.DisabledHosts.Add(disabled);
        }

        // A floor, not a pin: TLS 1.2 or later (AGENTS.md, Security). mfd.gov.np's TLS 1.0 is
        // refused here by design (ADR-0011). CA5398 prefers None, which lets the OS allow less.
#pragma warning disable CA5398
        var inner = host.Network ?? new SocketsHttpHandler { SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 } };
#pragma warning restore CA5398
        var store = new FileSnapshotStore(settings.Snapshots ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jharicast", "snapshots"));
        return new Wiring(new HttpClient(new PoliteHttpHandler(options, inner)) { Timeout = TimeSpan.FromMinutes(5) }, host.Time, store, settings.Osrm, replay: false);
    }
#pragma warning restore CA2000

    /// <summary>
    /// <c>Jharicast/&lt;version&gt; (+&lt;repository&gt;; &lt;contact&gt;)</c>. The contact is the
    /// operator's project mailbox; there is no default, so a site owner can always reach whoever
    /// runs it (ADR-0007).
    /// </summary>
    /// <exception cref="CliException">Missing, not a mailbox, or holding characters that would break the header.</exception>
    internal static string UserAgent(string? contact)
    {
        if (string.IsNullOrWhiteSpace(contact))
        {
            throw new CliException($"A contact mailbox is required for live requests, so site owners can reach whoever runs this. Pass --contact <mailbox> or set {ContactVariable}. Use a project mailbox, not a personal address. To run offline, use --fixtures <dir>.");
        }

        contact = contact.Trim();
        if (!contact.Contains('@', StringComparison.Ordinal) || contact.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ';' or '(' or ')'))
        {
            throw new CliException($"'{contact}' is not a mailbox. Pass --contact <mailbox> or set {ContactVariable}.");
        }

        var version = typeof(Wiring).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        return $"Jharicast/{version} (+{RepositoryUrl}; {contact})";
    }

    public OpenMeteoClient OpenMeteo() => new(_http, new OpenMeteoOptions { TimeProvider = Time });

    public DhmWarningsSource Warnings() => new(_http, Store, Time);

    public DorClosureSource Closures() => new(_http, Store, Time);

    public OsrmRouteProvider Routing() => new(_http, Osrm);

    public void Dispose() => _http.Dispose();

    /// <summary>A clock stopped at the replayed time.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    /// <summary>Replays keep nothing: a stored snapshot would make the next replay look unchanged.</summary>
    private sealed class MemoryStore : ISnapshotStore
    {
        private readonly ConcurrentDictionary<string, Snapshot> _latest = new(StringComparer.Ordinal);

        public Task SaveAsync(Snapshot snapshot, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _latest[snapshot.SourceId] = snapshot;
            return Task.CompletedTask;
        }

        public Task<Snapshot?> LatestAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(_latest.TryGetValue(sourceId, out var s) ? s : null);
    }
}
