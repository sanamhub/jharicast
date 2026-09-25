# Working in this repository

Instructions for anyone, human or AI agent, who changes code or docs here. `CLAUDE.md` imports
this file.

## What this is

Jharicast, an MIT-licensed .NET 10 library and CLI that turns weather model output, official Nepali
warnings and road status into per-route, per-day assessments and reports material changes. The
design is [docs/PLAN.md](docs/PLAN.md) and [docs/adr](docs/adr). The build order is
[docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md): take the lowest task that is not done, finish
it, and stop.

The design documents (`docs/PLAN.md`, `docs/IMPLEMENTATION.md`, `docs/adr`, `docs/research`,
`docs/reference`) are kept by the maintainer and are not in the public repository. If they are
missing from your checkout, ask the maintainer rather than guessing.

## Commands

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

```bash
dotnet pack -c Release -o artifacts/packages
```

All three pass with zero warnings before a PR. Warnings are errors.

## Hard rules

1. **No evasion, ever** (ADR-0007): no CAPTCHA solving, fingerprint spoofing, proxy rotation,
   embedded keys, admin routes, or retrying past a 401 or 403. If a source refuses us, stop and
   report.
2. **Tests never call a live endpoint.** Fixtures only. The nightly source check is the only live
   traffic, and it goes through `PoliteHttpHandler`.
3. **Official warnings are never lowered by model output** (ADR-0005). No output says "safe".
4. **No personal data in fixtures, snapshots or logs.** DoR contact fields are dropped at parse.
5. **Never put a personal email address in a User-Agent or request.** Use the project mailbox.
6. **The core (`src/Jharicast`) has no package references and no I/O.**
7. **The public API is PLAN.md section 3.** Ask before adding to it.
8. **Rules change only under a new rule id** (ADR-0006). A failing golden test is a finding to
   report, not a number to edit.
9. Culture-invariant formatting and parsing everywhere (`CultureInfo.InvariantCulture`).
10. Follow the writing rules below for docs, comments, commits and PRs.

## Commits and pull requests

Conventional Commits, one task per PR, named after the task id (`feat(nepal): N03 dhm warnings
source`). No AI attribution trailers (PLAN.md, same rule as Hulaki). The PR ticks each "Done when"
item and pastes the last lines of the three commands.

## Writing

The full rules are in [.claude/skills/writing-style/SKILL.md](.claude/skills/writing-style/SKILL.md).
Agents that do not load skills follow this short form:

- No em dashes. None of the banned filler words listed in the skill.
- Numbers over adjectives. Say what was verified and how; label a guess as a guess.
- Commits: `type(scope): summary`, imperative, lower case, under 72 characters, body says why.
- **No AI attribution** (`Co-Authored-By`, `Generated with`) in commits, PRs or files.
- PR description: an opening paragraph, a bullet per area, then **Not in this PR** and
  **Verifying it** when they apply.

## Project standards

A personal project of [@sanamhub](https://github.com/sanamhub), not company work. These are the
defaults; a deviation needs an ADR.

| Area | Standard |
| --- | --- |
| Decisions | Significant decisions are ADRs in `docs/adr`. Accepted ADRs are superseded, never edited. |
| Requirements | A task starts only when its acceptance criteria are written and testable (PLAN.md). Legal, security and privacy questions are asked, never assumed. |
| Tests | Unit over integration over end to end; a test per acceptance criterion; 80 percent line coverage target; no real personal data. |
| Security | OWASP ASVS where it applies; TLS 1.2 or later; secrets only in environment variables or GitHub environment secrets; CI blocks high and critical advisories. |
| Release | SemVer and `CHANGELOG.md`; green CI; the `production` environment approval; rollback is unlist plus a patch release. |
| Review | One maintainer merges their own PRs after full CI. PRs touching rules, acquisition policy or source parsers wait 24 hours and get a `/code-review` pass first. This ends when a second maintainer joins. |
| Layering | Domain and Application share the `Jharicast` assembly; source packages are Infrastructure; the CLI is Presentation (ADR-0003). |
| Permission | `Jharicast.Nepal` is developed, and may feed a closed beta, before DHM, DoR and BIPAD answer the permission request. It is not published to nuget.org until they answer or the maintainer records a decision in ADR-0014 (ADR-0007). |