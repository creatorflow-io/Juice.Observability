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
// styles.scss: the design tokens (light and dark) and jc-* classes the pages use
@use '@juice-js/observability';
```

- `apiBase`: the API origin; empty when the API serves the app.
- `basePath`: where the routes are mounted; every link between pages goes under it.
- The router needs `bindToComponentInputs: true`. The pages take their route and query parameters as inputs.
- The host's bearer-token interceptor (`@juice-js/auth`) must cover `apiBase`. The token carries the `platform_tenant`
  claim and a platform role (`viewer`, `operator` or `admin`).
