import { Pipe, PipeTransform } from '@angular/core';

/** Milliseconds since the epoch of an ISO 8601 time, keeping the sub-millisecond digits that Date.parse drops. */
export function preciseTime(iso: string): number {
  const fraction = /\.\d{3}(\d+)/.exec(iso)?.[1];
  return Date.parse(iso) + (fraction ? Number(`0.${fraction}`) : 0);
}

/** 0.8 ms, 120 ms, 1.42 s, 2 m 05 s. */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return '—';
  if (ms < 1) return `${ms.toFixed(2)} ms`;
  if (ms < 1000) return `${Math.round(ms * 10) / 10} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(2)} s`;
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)} m ${String(s % 60).padStart(2, '0')} s`;
}

export function formatPercent(rate: number | null | undefined): string {
  if (rate === null || rate === undefined) return '—';
  const p = rate * 100;
  return `${p === 0 ? '0' : p < 0.1 ? '<0.1' : p.toFixed(p < 10 ? 1 : 0)} %`;
}

export function formatAgo(iso: string | null | undefined): string {
  if (!iso) return '—';
  const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return `${Math.max(s, 0)} s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86_400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86_400)} d ago`;
}

@Pipe({ name: 'duration' })
export class DurationPipe implements PipeTransform {
  transform(ms: number | null | undefined) { return formatDuration(ms); }
}

@Pipe({ name: 'percent2' })
export class PercentPipe implements PipeTransform {
  transform(rate: number | null | undefined) { return formatPercent(rate); }
}

@Pipe({ name: 'ago' })
export class AgoPipe implements PipeTransform {
  transform(iso: string | null | undefined) { return formatAgo(iso); }
}

/** A reason code such as `dependency_failed:shop/payments` as words. */
@Pipe({ name: 'reason' })
export class ReasonPipe implements PipeTransform {
  transform(code: string) {
    const [what, subject] = code.split(/:(.*)/s);
    const text = what.replace(/_/g, ' ');
    return subject ? `${text}: ${subject}` : text;
  }
}
