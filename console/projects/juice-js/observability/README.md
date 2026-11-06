# @juice-js/observability

The Juice Console pages for an Angular app built on `@juice-js/layout`: services, traces, dependencies, logs, metrics,
resources, alerts and rules. The pages read only the Juice Observability API (the `Juice.Observability.Api` NuGet
package, or the platform image).

## Use

```ts
// app module or app config
providers: [
  provideHttpClient(withInterceptorsFromDi()),
  provideJuiceObservability({ apiBase: '', basePath: '/observability' }),
],

// routes: mount the pages under basePath, behind your own sign-in guard
{ path: 'observability', children: OBSERVABILITY_ROUTES.map(r => ({ ...r, canActivate: [AuthGuard] })) },

// menu (juice-layout)
MenuModule.forRoot([...yourMenus, ...observabilityMenus('/observability', 100)]),
```

```scss
// styles.scss (the app's global styles, once): the pages' layout, in the Juice Console's teal theme, light and dark (.dark).
@use '@juice-js/observability';

// Optional: colours from your own Angular Material (M2) themes instead.
:root { @include observability.colors($light-theme); }
.dark { @include observability.colors($dark-theme); }     // under your dark-mode selector
```

The default colours sit under zero-specificity `:where()` selectors, so your `colors()` replaces them wherever it
comes in the file. `colors($theme, $status: (), $series: null)` sets the `--jc-*` custom properties the pages, charts
and dependency graph read:

| From your theme | Used for |
|---|---|
| background, card, divider, text, secondary text | page, cards and tables, borders, text, labels |
| primary | links, selection, the selected graph node or edge |
| warn | unhealthy and critical, errors |

A Material theme has no colour for healthy, degraded or offline, nor a categorical palette, so they have defaults
(Material green, deep orange and greys; ten Material hues for services in a trace and lines in a chart). Set your own:

```scss
:root {
  @include observability.colors($light-theme,
    $status: (ok: #1b5e20, warn: #e65100, bad: #c62828, crit: #8e0000, off: #546e7a, none: #90a4ae),
    $series: (#1565c0, #6a1b9a, #00838f, #ef6c00));
}
```

The pages use the properties but don't style the page itself; give `body` your colours, for example
`background: var(--jc-bg); color: var(--jc-text);`. A theme switch at runtime (a class change on `<html>` or `<body>`,
like juice-layout's dark-mode toggle) repaints the charts and the graph.

- `apiBase`: the API origin; empty when the API serves the app.
- `basePath`: where the routes are mounted; every link between pages goes under it.
- `auditLink` (optional): a link from a trace to its audit records, shown on the trace page as "Audit records ↗".
  With `@juice-js/audit` mounted at `/audit`: `auditLink: id => ['/audit/traces', id]`. A string is an external URL.
  Without it, the trace page shows no link.
- The trace page's resource usage and a service's Capacity tab read .NET's `System.Runtime` meter (.NET 9+): add
  `AddMeter("System.Runtime")` to the host's `WithMetrics`. If traces are sampled, also add `AddAspNetCoreInstrumentation()`
  there: the Capacity tab then counts requests from `http.server.request.duration`, not from spans, which undercount.
- The router needs `bindToComponentInputs: true`. The pages take their route and query parameters as inputs.
- The host's bearer-token interceptor (`@juice-js/auth`) must cover `apiBase`. The token carries the `platform_tenant`
  claim and a platform role (`viewer`, `operator` or `admin`).
