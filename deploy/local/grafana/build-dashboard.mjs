// Generates dashboards/juice-overview.json (the provisioned starter dashboard). Run: node grafana/build-dashboard.mjs
// Queries read the ClickHouse exporter's default tables. Counters arrive cumulative, so rates come from the increase
// of each series (ServiceName, Attributes, StartTimeUnix) between time buckets. A series' first bucket counts its whole
// value when the process started inside the range, and nothing otherwise (the increase before the range is unknown).
import { writeFileSync } from 'node:fs';

const ds = { type: 'grafana-clickhouse-datasource', uid: 'juice-clickhouse' };
const FORMAT = { timeseries: 0, table: 1, logs: 2 };
const service = `ServiceName IN (\${service:singlequote})`;

// Increase per bucket of a cumulative sum, grouped by `label` (a SQL expression over Attributes).
const counterRate = (metric, label, where = '1') => `
SELECT t, label, sum(delta) AS value FROM (
  SELECT t, label, v - lagInFrame(v, 1, if(StartTimeUnix >= $__fromTime, 0, v)) OVER (PARTITION BY ServiceName, Attributes, StartTimeUnix ORDER BY t) AS delta
  FROM (
    SELECT $__timeInterval(TimeUnix) AS t, ServiceName, Attributes, StartTimeUnix, ${label} AS label, max(Value) AS v
    FROM otel.otel_metrics_sum
    WHERE $__timeFilter(TimeUnix) AND MetricName = '${metric}' AND ${service} AND ${where}
    GROUP BY t, ServiceName, Attributes, StartTimeUnix, label
  )
)
GROUP BY t, label ORDER BY t`.trim();

const panels = [];
let y = 0;
const row = (title) => { panels.push({ type: 'row', title, collapsed: false, gridPos: { h: 1, w: 24, x: 0, y }, panels: [] }); y += 1; };
const panel = (title, type, sql, format, { w = 12, h = 8, x = 0, unit, description, options, newRow = false } = {}) => {
  panels.push({
    type, title, description, datasource: ds,
    gridPos: { h, w, x, y },
    fieldConfig: { defaults: unit ? { unit } : {}, overrides: [] },
    options: options ?? {},
    targets: [{ refId: 'A', datasource: ds, editorType: 'sql', format, queryType: Object.keys(FORMAT).find(k => FORMAT[k] === format), rawSql: sql }],
  });
  if (newRow || x + w >= 24) y += h;
};

row('Traffic (from traces)');
panel('Requests and messages handled', 'timeseries', `
SELECT $__timeInterval(Timestamp) AS t, concat(ServiceName, ' · ', SpanKind) AS series, count() AS value
FROM otel.otel_traces
WHERE $__timeFilter(Timestamp) AND SpanKind IN ('Server', 'Consumer') AND ${service}
GROUP BY t, series ORDER BY t`.trim(), FORMAT.timeseries,
  { description: 'Server spans (HTTP requests) and consumer spans (messages processed) per interval.' });
panel('Failed requests and messages', 'timeseries', `
SELECT $__timeInterval(Timestamp) AS t, concat(ServiceName, ' · ', SpanName) AS series, count() AS value
FROM otel.otel_traces
WHERE $__timeFilter(Timestamp) AND SpanKind IN ('Server', 'Consumer') AND StatusCode = 'Error' AND ${service}
GROUP BY t, series ORDER BY t`.trim(), FORMAT.timeseries, { x: 12 });
panel('Slowest operations (p95)', 'table', `
SELECT ServiceName AS service, SpanName AS operation, SpanKind AS kind, count() AS calls,
       countIf(StatusCode = 'Error') AS errors,
       round(quantile(0.5)(Duration) / 1e6, 2) AS p50_ms, round(quantile(0.95)(Duration) / 1e6, 2) AS p95_ms
FROM otel.otel_traces
WHERE $__timeFilter(Timestamp) AND ${service}
GROUP BY service, operation, kind ORDER BY p95_ms DESC LIMIT 50`.trim(), FORMAT.table, { w: 24 });

row('Juice metrics');
panel('Mediator requests by operation', 'timeseries',
  counterRate('juice.mediator.requests', "Attributes['juice.operation']"), FORMAT.timeseries);
panel('Mediator errors by type', 'timeseries',
  counterRate('juice.mediator.errors', "concat(Attributes['juice.operation'], ' · ', Attributes['error.type'])"), FORMAT.timeseries, { x: 12 });
panel('Messages consumed by result', 'timeseries',
  counterRate('juice.messaging.consumed', "concat(Attributes['juice.message_type'], ' · ', Attributes['juice.dispatch.result'])"), FORMAT.timeseries);
panel('Outbox delivery outcomes', 'timeseries', `
SELECT t, label, sum(delta) AS value FROM (
  SELECT t, label, v - lagInFrame(v, 1, if(StartTimeUnix >= $__fromTime, 0, v)) OVER (PARTITION BY ServiceName, MetricName, Attributes, StartTimeUnix ORDER BY t) AS delta
  FROM (
    SELECT $__timeInterval(TimeUnix) AS t, ServiceName, MetricName, Attributes, StartTimeUnix,
           concat(Attributes['publisher'], ' · ', replaceRegexpOne(MetricName, '^delivery_(.*)_total$', '\\\\1')) AS label, max(Value) AS v
    FROM otel.otel_metrics_sum
    WHERE $__timeFilter(TimeUnix) AND MetricName IN ('delivery_success_total', 'delivery_failure_total') AND ${service}
    GROUP BY t, ServiceName, MetricName, Attributes, StartTimeUnix, label
  )
)
GROUP BY t, label ORDER BY t`.trim(), FORMAT.timeseries,
  { x: 12, description: 'From AddJuiceDeliveryMetrics(): delivery attempts that succeeded or failed, per publisher.' });

row('Logs');
panel('Warnings and errors', 'logs', `
SELECT Timestamp AS timestamp, SeverityText AS level, Body AS body, ServiceName AS service, ScopeName AS category,
       LogAttributes['juice.correlation_id'] AS correlation_id, TraceId AS traceID
FROM otel.otel_logs
WHERE $__timeFilter(Timestamp) AND SeverityNumber >= 13 AND ${service}
ORDER BY Timestamp DESC LIMIT 500`.trim(), FORMAT.logs,
  { w: 24, h: 10, options: { showTime: true, wrapLogMessage: true, enableLogDetails: true, sortOrder: 'Descending' } });

row('Host (Collector host_metrics)');
panel('Memory used', 'timeseries', `
SELECT $__timeInterval(TimeUnix) AS t, ResourceAttributes['host.name'] AS host, avg(Value) AS value
FROM otel.otel_metrics_sum
WHERE $__timeFilter(TimeUnix) AND MetricName = 'system.memory.usage' AND Attributes['state'] = 'used'
GROUP BY t, host ORDER BY t`.trim(), FORMAT.timeseries, { unit: 'bytes' });
panel('Load average (1m)', 'timeseries', `
SELECT $__timeInterval(TimeUnix) AS t, ResourceAttributes['host.name'] AS host, avg(Value) AS value
FROM otel.otel_metrics_gauge
WHERE $__timeFilter(TimeUnix) AND MetricName = 'system.cpu.load_average.1m'
GROUP BY t, host ORDER BY t`.trim(), FORMAT.timeseries, { x: 12 });

const dashboard = {
  uid: 'juice-overview',
  title: 'Juice overview',
  description: 'Starter dashboard: traffic and failures from traces, Juice mediator/messaging/delivery metrics, warning logs, host metrics.',
  tags: ['juice'],
  editable: false,
  schemaVersion: 41,
  time: { from: 'now-1h', to: 'now' },
  refresh: '30s',
  templating: {
    list: [{
      name: 'service', label: 'Service', type: 'query', datasource: ds,
      query: "SELECT DISTINCT ServiceName FROM otel.otel_traces WHERE Timestamp > now() - INTERVAL 1 DAY ORDER BY 1",
      definition: "SELECT DISTINCT ServiceName FROM otel.otel_traces WHERE Timestamp > now() - INTERVAL 1 DAY ORDER BY 1",
      refresh: 2, multi: true, includeAll: true, current: { text: 'All', value: '$__all' },
    }],
  },
  panels,
};

writeFileSync(new URL('./dashboards/juice-overview.json', import.meta.url), JSON.stringify(dashboard, null, 2) + '\n');
console.log(`wrote dashboards/juice-overview.json (${panels.filter(p => p.type !== 'row').length} panels)`);
