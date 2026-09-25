# Jharicast.Fetch

Polite HTTP for public data feeds, from [Jharicast](https://github.com/sanamhub/jharicast). `PoliteHttpHandler` is a
`DelegatingHandler` that makes an `HttpClient` a client nobody needs to block:

| Behaviour | Default |
| --- | --- |
| User-Agent | required at construction, must name a project URL and a contact mailbox; no default |
| robots.txt | fetched and cached 24 h per host; disallowed paths are refused before sending |
| Per host | one request in flight, 1 s minimum interval (set 5 s for government hosts), raised by `Crawl-delay` |
| Retries | 408, 429, 5xx and connection errors only, at most 3, `Retry-After` honoured; never 401 or 403 |
| Circuit breaker | 5 consecutive failures open it for 10 minutes |
| Kill switch | `DisabledHosts` |

It also has conditional GET, `Snapshot` and `FileSnapshotStore` for keeping what was fetched,
and `SourceHealth` (fresh, stale, drifting, failing, disabled) for reporting it. It never
evades a block: no proxy rotation, no browser fingerprints, no CAPTCHA solving. A refusal is
reported, not worked around.

Use a project mailbox in the User-Agent, never a personal address.
