# Security policy

## Supported versions

Only the latest release on nuget.org receives fixes. While Jharicast is 0.x, that is the latest
prerelease.

## Reporting a vulnerability

Report privately through
[GitHub security advisories](https://github.com/sanamhub/jharicast/security/advisories/new). Do not open a public issue.

Include the smallest code that shows it, the package versions and the .NET version. Expect a
first reply within a week. This is a one-person project, so a fix can take longer; the advisory
says when one is ready.

Security bugs here include: a way to make Jharicast request a host other than the configured source;
a parser that crashes, hangs or allocates without bound on hostile input; personal data from a
source (for example DoR officer contact fields) surviving into a snapshot, log or output.

A government website changing its format is a normal bug. Use the "Source changed" form.

Jharicast does not bypass access controls and will not accept changes that do (ADR-0007). A report
that a source can be scraped past its protections is not a vulnerability in Jharicast.
