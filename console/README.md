# Juice Console

The product UI of Juice Observability (docs/juice-observability-ui-architecture.md,
docs/juice-observability-console-ui-requirements.md): Angular 22 on `juice-layout`. It reads everything through the
Juice API, never ClickHouse or PostgreSQL.

```bash
npm ci
npm start                 # http://localhost:4200, /api proxied to the API on :5180 (deploy/local/scripts/run-api.sh)
npm run build             # dist/juice-console/browser; the API image serves it (Dockerfile)
BASE=http://localhost:5180 node e2e/journey.mjs   # the §21 investigation journey in headless Edge (BROWSER=<path>)
```

- **Settings** come from `public/config.json` at runtime (identity provider, client id, API origin), so one build serves
  every deployment.
- **Investigation context** (environment, time range, selected operation, span, edge) lives in the URL: every page is
  a deep link.
