using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jharicast.Fetch;

/// <summary>
/// A parsed robots.txt (RFC 9309): groups by user agent, <c>Allow</c> and <c>Disallow</c> with the
/// longest-match rule, <c>*</c> and <c>$</c> in paths, and the non-standard <c>Crawl-delay</c> and
/// <c>Request-rate</c>, which raise a host's request interval (ADR-0007).
/// </summary>
public sealed class RobotsPolicy
{
    private readonly IReadOnlyList<Group> _groups;

    private RobotsPolicy(IReadOnlyList<Group> groups) => _groups = groups;

    /// <summary>No rules: what a missing robots.txt (404) means.</summary>
    public static RobotsPolicy AllowAll { get; } = new([]);

    /// <summary>Every path disallowed: what an unreachable robots.txt (5xx) means until the next try.</summary>
    public static RobotsPolicy DisallowAll { get; } = Parse("User-agent: *\nDisallow: /");

    /// <summary>Parses robots.txt text. Never throws on content: unknown lines and bad values are skipped, as the RFC asks.</summary>
    /// <param name="content">The file, already decoded as UTF-8.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static RobotsPolicy Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var groups = new List<Group>();
        Group? current = null;
        var inAgentRun = false;
        foreach (var raw in content.Split('\n'))
        {
            var hash = raw.IndexOf('#', StringComparison.Ordinal);
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToUpperInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "USER-AGENT")
            {
                // Consecutive user-agent lines share one group; one after a rule starts a new group.
                if (current is null || !inAgentRun)
                {
                    current = new Group();
                    groups.Add(current);
                }

                current.Agents.Add(ProductToken(value));
                inAgentRun = true;
                continue;
            }

            if (key is not ("ALLOW" or "DISALLOW" or "CRAWL-DELAY" or "REQUEST-RATE"))
            {
                continue; // Sitemap and anything else sit outside groups
            }

            inAgentRun = false;
            if (current is null)
            {
                continue; // a rule before any user-agent line belongs to no group
            }

            switch (key)
            {
                case "ALLOW" or "DISALLOW" when value.Length > 0:
                    current.Rules.Add(new Rule(Normalize(value), key == "ALLOW"));
                    break;
                case "CRAWL-DELAY" when double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds):
                    current.Raise(TimeSpan.FromSeconds(seconds));
                    break;
                case "REQUEST-RATE" when TryParseRate(value, out var interval):
                    current.Raise(interval);
                    break;
            }
        }

        return new RobotsPolicy(groups);
    }

    /// <summary>Whether a path may be fetched by a crawler with this product token.</summary>
    /// <param name="productToken">For example <c>Jharicast</c>, or a whole User-Agent string, whose first token is used. Case-insensitive.</param>
    /// <param name="pathAndQuery">The path and query, for example <c>/home/getAPIData/1</c>.</param>
    /// <returns>True when allowed. <c>/robots.txt</c> itself is always allowed.</returns>
    /// <exception cref="ArgumentException">An argument is null or empty.</exception>
    public bool IsAllowed(string productToken, string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrEmpty(productToken);
        ArgumentException.ThrowIfNullOrEmpty(pathAndQuery);
        var path = Normalize(pathAndQuery);
        if (path == "/robots.txt")
        {
            return true;
        }

        Rule? best = null;
        foreach (var rule in RulesFor(productToken))
        {
            if (rule.Matches(path) && (best is null || rule.Pattern.Length > best.Pattern.Length || (rule.Pattern.Length == best.Pattern.Length && rule.Allow)))
            {
                best = rule; // the longest pattern wins; on a tie, the less restrictive rule
            }
        }

        return best?.Allow ?? true;
    }

    /// <summary>The longest of <c>Crawl-delay</c> and <c>Request-rate</c> for this product token.</summary>
    /// <param name="productToken">As for <see cref="IsAllowed"/>.</param>
    /// <returns>The interval, or null when the file sets neither.</returns>
    /// <exception cref="ArgumentException"><paramref name="productToken"/> is null or empty.</exception>
    public TimeSpan? MinInterval(string productToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(productToken);
        return GroupsFor(productToken).Select(g => g.Interval).Max();
    }

    private IEnumerable<Rule> RulesFor(string productToken) => GroupsFor(productToken).SelectMany(g => g.Rules);

    // Every group naming the token, merged (RFC 9309 section 2.2.1); otherwise the "*" groups.
    private List<Group> GroupsFor(string productToken)
    {
        var token = ProductToken(productToken);
        var named = _groups.Where(g => g.Agents.Contains(token)).ToList();
        return named.Count > 0 ? named : [.. _groups.Where(g => g.Agents.Contains("*"))];
    }

    // "Jharicast/0.1 (+https://...)" and "jharicast" both become "JHARICAST".
    private static string ProductToken(string value)
    {
        var end = value.IndexOfAny(['/', ' ', '\t', '(']);
        return (end >= 0 ? value[..end] : value).Trim().ToUpperInvariant();
    }

    // "1/60" is one request per 60 seconds; "1/5m" and "1/1h" carry a unit.
    private static bool TryParseRate(string value, out TimeSpan interval)
    {
        interval = default;
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return false;
        }

        var period = value[(slash + 1)..].Trim();
        var unit = period.Length > 0 ? char.ToUpperInvariant(period[^1]) : ' ';
        var multiplier = unit switch { 'S' => 1, 'M' => 60, 'H' => 3600, _ => 0 };
        if (multiplier > 0)
        {
            period = period[..^1];
        }

        if (!int.TryParse(value[..slash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var requests) || requests <= 0
            || !double.TryParse(period, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var span))
        {
            return false;
        }

        interval = TimeSpan.FromSeconds(span * Math.Max(multiplier, 1) / requests);
        return true;
    }

    // RFC 9309 section 2.2.2: percent-encoded unreserved characters are compared decoded, other
    // escapes with upper-case hex, and non-ASCII characters as their UTF-8 escapes.
    private static string Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                var decoded = (char)Convert.ToByte(value.Substring(i + 1, 2), 16);
                if (char.IsAsciiLetterOrDigit(decoded) || decoded is '-' or '.' or '_' or '~')
                {
                    text.Append(decoded);
                }
                else
                {
                    text.Append('%').Append(char.ToUpperInvariant(value[i + 1])).Append(char.ToUpperInvariant(value[i + 2]));
                }

                i += 2;
            }
            else if (c > 0x7f)
            {
                var length = char.IsHighSurrogate(c) && i + 1 < value.Length ? 2 : 1;
                foreach (var b in Encoding.UTF8.GetBytes(value.Substring(i, length)))
                {
                    text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }

                i += length - 1;
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private sealed class Group
    {
        public HashSet<string> Agents { get; } = new(StringComparer.Ordinal);

        public List<Rule> Rules { get; } = [];

        public TimeSpan? Interval { get; private set; }

        public void Raise(TimeSpan interval)
        {
            if (Interval is null || interval > Interval)
            {
                Interval = interval;
            }
        }
    }

    private sealed record Rule(string Pattern, bool Allow)
    {
        // "*" matches any run of characters; a final "$" anchors the end. Otherwise a prefix match.
        public bool Matches(string path)
        {
            var anchored = Pattern.EndsWith('$');
            var parts = (anchored ? Pattern[..^1] : Pattern).Split('*');
            if (!path.StartsWith(parts[0], StringComparison.Ordinal))
            {
                return false;
            }

            if (parts.Length == 1)
            {
                return !anchored || path.Length == parts[0].Length;
            }

            var position = parts[0].Length;
            for (var i = 1; i < parts.Length; i++)
            {
                var part = parts[i];
                if (i == parts.Length - 1 && anchored)
                {
                    return path.Length - position >= part.Length && path.EndsWith(part, StringComparison.Ordinal);
                }

                var found = path.IndexOf(part, position, StringComparison.Ordinal);
                if (found < 0)
                {
                    return false;
                }

                position = found + part.Length;
            }

            return true;
        }
    }
}
