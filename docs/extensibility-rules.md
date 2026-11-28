# Extending rules: templates and thresholds

Core 2.0 M2. A module can add a **rule template** (the query that observes one value per subject) and a **threshold provider** (where a
rule's numbers come from). Both are registered from `IObservabilityModule.ConfigureServices` and checked when the host is composed.

```csharp
public void ConfigureServices(IModuleServicesBuilder module)
    => module.AddRuleTemplate<SplitTemplate>().AddThresholdProvider<PercentileThresholds>();
```

The types are created once, as singletons, with the host's services.

## Keys

- A template's `Info.Key` and a provider's `Key` are `<module>.<name>`: your module name, a dot, then lower case letters, digits and `-` (at
  most 40 characters in all). An alert source's template follows the same rules, and is never a rule template's key. The core's templates use their `RuleTemplate` names (`ErrorRate`, `HostCpu`, ...) and the core's provider is `fixed`, so
  neither can clash with yours.
- A key outside your prefix, a key held twice, an abstract type, or the same type registered twice throws `ModuleCompositionException`
  and the host does not start.
- `SloBurn` is not a registry template. Its alerts come from an alert source, not a query.

Storing a rule that names a module template needs a `rules` column and an API field. They arrive with the first module template, so a
module template is registered, listed and observed now, but cannot be the template of a stored rule yet.

## A template

```csharp
public sealed class SplitTemplate : IRuleTemplate
{
    private static readonly string Sql = $"""
        SELECT ServiceName, count() FROM otel.otel_traces
        WHERE {TemplateSql.Tenant} AND Timestamp >= {TemplateSql.From} AND Timestamp < {TemplateSql.To}
        GROUP BY ServiceName LIMIT {TemplateSql.Limit}
        """;

    public RuleTemplateInfo Info => new("pro.split", "Split", TemplateSubjectKind.Service);

    public async Task<IReadOnlyList<TemplateObservation>> ObserveAsync(RuleSpec rule, ITemplateTelemetry telemetry, TemplateWindow window, CancellationToken ct)
        => await telemetry.QueryAsync(Sql, new Dictionary<string, object>(), r => /* map a row */, ct);
}
```

- The SQL is your module's own constant. Put the rule's scope in as typed parameters (`{svc:String}`), never as text.
- `ITemplateTelemetry` refuses text that lacks the tenant, from, to or limit placeholder, and refuses a parameter named `tenant`, `from`,
  `to` or `limit`: those are the host's. It reads as the platform's ClickHouse reader, whose profile caps time and memory (W-20, W-23).
- The host keeps at most `window.RowLimit` observations.
- A `Value` of `null` means no data, never zero. With `UsesSamples`, fewer samples than the rule's `MinimumSamples` is no data too.
- `Validate(RuleSpec)` returns template-specific errors only. The generic checks (key, schedule, window, durations, scope) stay in the core.

## A threshold provider

A provider supplies numbers only: a threshold and a recovery threshold per subject. It never chooses the comparison, the template or the scope.

- A rule opts in with `"ThresholdSource": { "Provider": "pro.percentile", "Settings": { ... } }`. `Settings` is at most 4096 bytes of JSON as stored.
  A rule without one has its own numbers, its JSON and its hash are unchanged, and no provider is asked.
- The provider is asked once per evaluation for the subjects observed and those with an open alert.
- **A subject the provider leaves out, a non-finite number, or a pair on the wrong side (recovery above the threshold for a rule that fires above,
  below it for one that fires below) is no data for that subject.** It neither fires, nor resolves, nor resets recovery.
- **A provider that throws fails the evaluation** (counted in `rules.evaluations` as a failure). Nothing is written, so open alerts stay as they are.
- An alert's evidence records `threshold` and `recoveryThreshold` as used, and `thresholdSource` when the provider is not `fixed`. When the
  provider gave none for a subject, the two numbers are left out and the condition is `NoData`.

`IRuleThresholdProvider.Validate(RuleSpec, JsonElement?)` runs when the rule is validated (configuration at startup, and the rules API).

## For core code

The evaluator reads thresholds only through `RuleThresholds` and classifies with `AlertLifecycle.Classify(rule, threshold, value, samples)`.
`RuleDefinition.Validate(packs, registry)` takes the registry, so each template validates its own fields, with the same messages as before.
