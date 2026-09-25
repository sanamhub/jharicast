# Jharicast

The rules of [Jharicast](https://github.com/sanamhub/jharicast): alert levels, ensemble statistics (R-7 quantiles, strict
exceedance), the four trip rules for a leg on a day, and material change detection between two
assessments. Pure logic: no I/O, no clock, no package dependencies.

> **Not an official forecast.** Follow the official warning service of the country you are in.
> An official level is never lowered by a model (`AlertLevels.Combine`), and the best result,
> `Pass`, means only that no rule is broken.

```csharp
using Jharicast;

double?[] members = [70, 90, 110, 130, 150, 40, 60, 80, 100, 120];
AlertLevel model = RainAlertRule.V1.Evaluate(members);          // our own level
AlertLevel shown = AlertLevels.Combine(AlertLevel.Red, model);   // Red: official wins
```

Rules carry versioned ids (`jharicast.gust.v1`); a changed threshold gets a new id, so stored
results stay explainable. Data sources live in the other Jharicast packages; see the
[repository README](https://github.com/sanamhub/jharicast#readme) for the data terms.
