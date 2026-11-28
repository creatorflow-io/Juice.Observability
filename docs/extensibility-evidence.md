# Evidence contributors

A module can add read-only sections of evidence to the alert and service pages. A contributor is a scoped class that implements `IEvidenceContributor` and is added with `AddEvidenceContributor<T>()` in the module's `ConfigureServices` (at most 5 per module). The core's own `core.changes` is added the same way.

```csharp
public sealed class DeploysContributor(MyContext db) : IEvidenceContributor
{
    public string Key => "my-module.deploys";            // "<module>.<name>", unique in the host
    public EvidenceTarget Target => EvidenceTarget.Service;
    public string? Policy => "my-module.read";            // null is platform.read; else a core policy or the module's own

    public async Task<EvidenceSection?> ContributeAsync(EvidenceContext context, CancellationToken ct)
    {
        var rows = await db.Deploys.Where(d => d.ServiceId == context.ServiceId && d.At >= context.From && d.At < context.To).Take(50).ToListAsync(ct);
        return rows.Count == 0 ? null : new EvidenceSection("Deploys", rows.Select(d => new EvidenceItem(d.Name, d.Version, d.At)).ToList());
    }
}
```

## API

Both are under the tenant group and need `platform.read`:

- `GET /api/v1/tenants/{tenantId}/alerts/{alertId}/evidence-sections`. The window is from two hours before the alert fired to its resolution (or now).
- `GET /api/v1/tenants/{tenantId}/resources/{resourceId}/evidence-sections?environmentId=&from=&to=`. The window is at most 7 days (default the last hour). A resource that isn't a service returns `items: []`.

The answer is `{ items: [{ key, module, state, title, note, truncated, items }] }`, the core's sections first, then each module's in the order it was composed. `state` is `ok`, `failed`, `timeout` or `invalid`. A section that isn't `ok` has no title, note or items, and never a message: the reason is in the host's log.

## What the core guarantees

- **Tenant and subject.** The tenant is the route's tenant after the core's tenant filter (another tenant's id and an unknown one get the same 403). The alert or service is looked up in that tenant first: a subject outside it is a 404 and no contributor runs. `EvidenceContext` carries only values the core read.
- **Read-only.** The contributors of a request run one after another in one transaction that starts with `SET TRANSACTION READ ONLY` and is never committed. A module's own context (`AddModuleData`) and the core's join it, so a write fails. Each contributor runs in a savepoint that is rolled back whatever happened, so one that fails, or swallows a database error, doesn't stop the next. A context a module opens on its own connection isn't covered: don't.
- **No mutations.** The endpoint has no module metadata, so `IModuleMutations.Begin` throws inside a contributor.
- **Policy.** Each contributor has its own. A caller it denies gets no section and no hint of one. A contributor's policy is checked when the host starts: it must be `platform.read`, `platform.operate` or one of the contributor's own module's policies, or the start is refused (as is a key outside `<module>.`, or one used twice).
- **Time.** `Evidence:ContributorTimeout` (default 2 s, 0.5 to 10 s) bounds one contributor and `Evidence:Budget` (default 5 s, 1 to 60 s) the whole request. Once the budget is spent the rest read `timeout` without being called. A contributor that ignores its token is waited for another half second, and then the rest read `timeout`.
- **Text and size.** Titles and labels are cut at 64 characters, values and notes at 512, and a section at 50 items; a cut sets `truncated`. Control, line-separator and bidirectional characters (and unpaired surrogates) make the section `invalid`: it is dropped whole, never repaired. A severity is `info`, `warning` or `critical`. Text is rendered as text.
- **Links.** `EvidenceLink.Route` must be a concrete relative path that matches a route of a composed Console manifest (`slos/:id` matches `slos/abc`), with at most 8 short plain query entries. Any other link is dropped and its item kept.

## `core.changes`

The changes of an alert's related items, from the same query as `/alerts/{id}/related` without its health items. The alert page keeps its own change block, and doesn't draw the `core` sections a second time.
