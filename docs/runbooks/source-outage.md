# Runbook: a source is failing, stale or drifting

For the maintainer and for consumers such as Batosathi.

## Symptoms

`SourceHealth.Status` is `Failing`, `Stale`, `Drifting` or `Disabled` for a source, or
`jharicast sources check` exits 1.

## Steps

1. Run `jharicast sources check` once. Note which source and the detail.
2. **Failing (network, 5xx):** wait for the breaker (10 minutes). If it persists over 2 hours,
   check the site in a browser. Do not raise polling frequency.
3. **403 or repeated 429:** the site is refusing us. Disable the source by configuration. Do not
   change the User-Agent, add proxies or use a browser. Contact the agency (use the permission
   letter thread).
4. **Drifting:** open the latest snapshot. If a new district spelling appeared, add the alias to
   the gazetteer with a test using the snapshot as a fixture (after checking it for personal data).
   If the shape changed, update the parser and its fixtures in one PR.
5. **Stale:** check the agency's website. If the website is also stale, the source is fine; the
   agency has not issued. Consumers keep showing the staleness notice.
6. Consumers must keep telling users, in plain words, that official data is unavailable and link
   to the agency (ADR-0005 rule 3). Never hide the gap.
