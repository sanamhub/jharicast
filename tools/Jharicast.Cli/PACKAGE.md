# Jharicast.Cli

The `jharicast` command of [Jharicast](https://github.com/sanamhub/jharicast): which of four trip rules a leg in Nepal breaks on
a day, and why, from global weather models, DHM warnings and DoR closures.

> **Not an official forecast.** Follow the Department of Hydrology and Meteorology (DHM) and the
> District Administration Office. `Pass` means only that no rule is broken.

```bash
jharicast route --from Birtamod --to Lumbini --date 2026-09-28 --contact ops@example.org
jharicast alerts --date 2026-09-28 --contact ops@example.org
jharicast sources check --contact ops@example.org
```

Live requests need a contact mailbox for the User-Agent, from `--contact` or the
`JHARICAST_CONTACT` environment variable; use a project mailbox, not a personal address.
`--fixtures <dir>` replays recorded files with no network at all. Every output ends with data
times, source health, the disclaimer and the data credits. The options and exit codes are in
the [repository README](https://github.com/sanamhub/jharicast#the-jharicast-command).

This package contains `Jharicast.Nepal` and is not published to nuget.org until DHM, DoR and
NDRRMA grant permission to use their data. Data terms and embedded data licences:
[README](https://github.com/sanamhub/jharicast#data-terms-first) and [THIRD-PARTY-NOTICES.txt](https://github.com/sanamhub/jharicast/blob/main/THIRD-PARTY-NOTICES.txt).
