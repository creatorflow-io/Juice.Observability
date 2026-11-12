// A minimal OTLP protobuf encoder (opentelemetry-proto v1: traces, logs, metrics), for the scale generator. Real SDKs send protobuf, so the
// Collector does the parsing work it does in production; JSON would cost it more. Field numbers are the proto's own.
// Only what the generator uses is here: string/int/double/bool attributes, spans with status, log records, gauges, sums, histograms.

const LEN = 2, VARINT = 0, I64 = 1;

/** Bytes being written: chunks are joined once at the end. */
class Writer {
  constructor() { this.parts = []; this.length = 0; }
  push(buf) { this.parts.push(buf); this.length += buf.length; }
  finish() { return Buffer.concat(this.parts, this.length); }
}

function varint(n) {
  // n < 2^53; BigInt only for the rare large value.
  const out = [];
  if (n < 0) throw new Error('negative varint');
  while (n > 0x7f) { out.push((n % 128) | 0x80); n = Math.floor(n / 128); }
  out.push(n);
  return Buffer.from(out);
}
/** A varint of an unsigned 64-bit BigInt (a negative int64 in two's complement). */
function varint64(n) {
  const out = [];
  while (n > 0x7fn) { out.push(Number(n & 0x7fn) | 0x80); n >>= 7n; }
  out.push(Number(n));
  return Buffer.from(out);
}
const tag = (field, wire) => varint(field * 8 + wire);
const tags = new Map();
const cachedTag = (field, wire) => { const k = field * 8 + wire; let t = tags.get(k); if (!t) tags.set(k, t = tag(field, wire)); return t; };

function bytesField(w, field, buf) {
  if (!buf || buf.length === 0) return;
  w.push(cachedTag(field, LEN)); w.push(varint(buf.length)); w.push(buf);
}
const strField = (w, field, s) => { if (s) bytesField(w, field, Buffer.from(s, 'utf8')); };
function varintField(w, field, n) { if (n) { w.push(cachedTag(field, VARINT)); w.push(varint(n)); } }
function fixed64Field(w, field, big) {
  // big: a BigInt (nanoseconds) or a non-negative integer.
  const b = Buffer.allocUnsafe(8); b.writeBigUInt64LE(BigInt(big)); w.push(cachedTag(field, I64)); w.push(b);
}
function doubleField(w, field, x) { const b = Buffer.allocUnsafe(8); b.writeDoubleLE(x); w.push(cachedTag(field, I64)); w.push(b); }
function sfixed64Field(w, field, n) { const b = Buffer.allocUnsafe(8); b.writeBigInt64LE(BigInt(n)); w.push(cachedTag(field, I64)); w.push(b); }
const message = (w, field, encode) => { const inner = new Writer(); encode(inner); bytesField(w, field, inner.finish()); };

// ─── common ───

/** An AnyValue: string, integer, double or boolean. */
function anyValue(v) {
  const w = new Writer();
  if (typeof v === 'string') { w.push(cachedTag(1, LEN)); const b = Buffer.from(v, 'utf8'); w.push(varint(b.length)); w.push(b); }
  else if (typeof v === 'boolean') { w.push(cachedTag(2, VARINT)); w.push(varint(v ? 1 : 0)); }
  else if (Number.isInteger(v)) { w.push(cachedTag(3, VARINT)); w.push(v >= 0 ? varint(v) : varint64(BigInt.asUintN(64, BigInt(v)))); }
  else { w.push(cachedTag(4, I64)); const b = Buffer.allocUnsafe(8); b.writeDoubleLE(v); w.push(b); }
  return w.finish();
}

// Attributes repeat (a route, a status, a host): each key = value pair is encoded once.
const kvCache = new Map();
/** One KeyValue, encoded (cached). */
export function kv(key, value) {
  const k = `${key}\u0000${typeof value}\u0000${value}`;
  let buf = kvCache.get(k);
  if (!buf) {
    const w = new Writer(); strField(w, 1, key); bytesField(w, 2, anyValue(value));
    buf = w.finish();
    if (kvCache.size < 200000) kvCache.set(k, buf);
  }
  return buf;
}
/** Attributes as encoded KeyValues, from an object or a list of encoded ones. */
export const attrs = obj => Object.entries(obj).map(([k, v]) => kv(k, v));
const attrFields = (w, field, list) => { for (const a of list ?? []) bytesField(w, field, a); };

/** An encoded Resource with these attributes; reuse it for every request of that resource. */
export function resource(obj) { const w = new Writer(); attrFields(w, 1, attrs(obj)); return w.finish(); }
/** An encoded InstrumentationScope. */
export function scope(name, version = '') { const w = new Writer(); strField(w, 1, name); strField(w, 2, version); return w.finish(); }

// ─── traces ───

/**
 * Spans: { traceId: Buffer(16), spanId: Buffer(8), parentSpanId?: Buffer(8), name, kind (2 server, 3 client, 4 producer, 5 consumer),
 * start, end (BigInt ns), attributes: [kv], error?: string }.
 */
function span(s) {
  const w = new Writer();
  bytesField(w, 1, s.traceId); bytesField(w, 2, s.spanId); bytesField(w, 4, s.parentSpanId);
  strField(w, 5, s.name); varintField(w, 6, s.kind);
  fixed64Field(w, 7, s.start); fixed64Field(w, 8, s.end);
  attrFields(w, 9, s.attributes);
  message(w, 15, sw => { if (s.error) strField(sw, 2, s.error); varintField(sw, 3, s.error ? 2 : 0); });
  return w.finish();
}
/** ExportTraceServiceRequest from [{ resource: Buffer, scope: Buffer, spans: [...] }]. */
export function traces(groups) {
  const w = new Writer();
  for (const g of groups) message(w, 1, rw => {
    bytesField(rw, 1, g.resource);
    message(rw, 2, sw => { bytesField(sw, 1, g.scope); for (const s of g.spans) bytesField(sw, 2, span(s)); });
  });
  return w.finish();
}

// ─── logs ───

/** Log records: { time (BigInt ns), severity (number), severityText, body, attributes: [kv], traceId?, spanId? }. */
function logRecord(r) {
  const w = new Writer();
  fixed64Field(w, 1, r.time); varintField(w, 2, r.severity); strField(w, 3, r.severityText);
  bytesField(w, 5, anyValue(r.body)); attrFields(w, 6, r.attributes);
  bytesField(w, 9, r.traceId); bytesField(w, 10, r.spanId); fixed64Field(w, 11, r.time);
  return w.finish();
}
/** ExportLogsServiceRequest from [{ resource, scope, records: [...] }]. */
export function logs(groups) {
  const w = new Writer();
  for (const g of groups) message(w, 1, rw => {
    bytesField(rw, 1, g.resource);
    message(rw, 2, sw => { bytesField(sw, 1, g.scope); for (const r of g.records) bytesField(sw, 2, logRecord(r)); });
  });
  return w.finish();
}

// ─── metrics ───

function numberPoint(p, start, time) {
  const w = new Writer();
  fixed64Field(w, 2, start); fixed64Field(w, 3, time);
  if (Number.isInteger(p.value) && p.int) sfixed64Field(w, 6, p.value); else doubleField(w, 4, p.value);
  attrFields(w, 7, p.attributes);
  return w.finish();
}
function histogramPoint(p, start, time) {
  const w = new Writer();
  fixed64Field(w, 2, start); fixed64Field(w, 3, time); fixed64Field(w, 4, p.count); doubleField(w, 5, p.sum);
  const counts = Buffer.allocUnsafe(8 * p.buckets.length); p.buckets.forEach((c, i) => counts.writeBigUInt64LE(BigInt(c), i * 8));
  bytesField(w, 6, counts);
  const bounds = Buffer.allocUnsafe(8 * p.bounds.length); p.bounds.forEach((b, i) => bounds.writeDoubleLE(b, i * 8));
  bytesField(w, 7, bounds);
  attrFields(w, 9, p.attributes);
  doubleField(w, 11, p.min); doubleField(w, 12, p.max);
  return w.finish();
}
/**
 * Metrics: { name, unit, type: 'gauge' | 'sum' | 'histogram', monotonic?, points: [{ attributes, value, int? } | { attributes, count, sum,
 * buckets, bounds, min, max }] }; sums and histograms are cumulative.
 */
function metric(m, start, time) {
  const w = new Writer();
  strField(w, 1, m.name); strField(w, 3, m.unit);
  if (m.type === 'gauge') message(w, 5, gw => { for (const p of m.points) bytesField(gw, 1, numberPoint(p, start, time)); });
  else if (m.type === 'sum') message(w, 7, sw => { for (const p of m.points) bytesField(sw, 1, numberPoint(p, start, time)); varintField(sw, 2, 2); varintField(sw, 3, m.monotonic ? 1 : 0); });
  else message(w, 9, hw => { for (const p of m.points) bytesField(hw, 1, histogramPoint(p, start, time)); varintField(hw, 2, 2); });
  return w.finish();
}
/** ExportMetricsServiceRequest from [{ resource, scope, start, time (BigInt ns), metrics: [...] }]. */
export function metrics(groups) {
  const w = new Writer();
  for (const g of groups) message(w, 1, rw => {
    bytesField(rw, 1, g.resource);
    message(rw, 2, sw => { bytesField(sw, 1, g.scope); for (const m of g.metrics) bytesField(sw, 2, metric(m, g.start, g.time)); });
  });
  return w.finish();
}
