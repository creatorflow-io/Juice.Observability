import { EnvironmentProviders, Injectable, InjectionToken, Pipe, PipeTransform, inject, makeEnvironmentProviders, provideEnvironmentInitializer } from '@angular/core';
import { ApprovalMenuBadge } from './approval-count';

/** How the host app mounts Juice Observability. */
export interface ObservabilityOptions {
  /** The Juice API origin; empty when the API serves the app (same origin). */
  apiBase: string;
  /** Where the host mounts {@link OBSERVABILITY_ROUTES}: `/` for the whole app, or a path such as `/observability`. */
  basePath: string;
  /**
   * A link to the audit records of a trace, shown on the trace page: for the Juice Audit pages mounted at `/audit`,
   * `id => ['/audit/traces', id]`. A string is an external URL. Without it, no link.
   */
  auditLink?: ((traceId: string) => unknown[] | string | null) | null;
  /** Show on the Approvals menu entry how many action requests wait for the signed-in user. On unless false. */
  approvalCount?: boolean;
}

export const OBSERVABILITY_OPTIONS = new InjectionToken<ObservabilityOptions>('OBSERVABILITY_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ apiBase: '', basePath: '/', auditLink: null }),
});

/**
 * Configures the pages: where the API is, where the host mounted the routes, and where a trace's audit records open. Also shows the count
 * of requests waiting for approval on the Approvals menu entry, unless `approvalCount: false`.
 */
export function provideJuiceObservability(options: Partial<ObservabilityOptions> = {}): EnvironmentProviders {
  return makeEnvironmentProviders([
    {
      provide: OBSERVABILITY_OPTIONS,
      useValue: {
        apiBase: options.apiBase ?? '',
        basePath: options.basePath ?? '/',
        auditLink: options.auditLink ?? null,
      } satisfies ObservabilityOptions,
    },
    ...(options.approvalCount === false ? [] : [provideEnvironmentInitializer(() => { inject(ApprovalMenuBadge); })]),
  ]);
}

/** `/observability/` → `/observability`; `` and `/` → `/`. */
export function normalizeBasePath(basePath: string): string {
  const trimmed = basePath.trim().replace(/^\/+|\/+$/g, '');
  return trimmed ? `/${trimmed}` : '/';
}

/** Router links to the pages, under the host's mount path. */
@Injectable({ providedIn: 'root' })
export class ObservabilityLinks {
  private readonly options = inject(OBSERVABILITY_OPTIONS);
  readonly base = normalizeBasePath(this.options.basePath);

  /** `to('services', id)` → `['/observability', 'services', id]`. */
  to(...segments: unknown[]): unknown[] { return [this.base, ...segments]; }

  /** The host's link to the audit records of a trace, if it gave one. */
  audit(traceId: string | null | undefined): { route: unknown[] } | { href: string } | null {
    if (!traceId || !this.options.auditLink) return null;
    const link = this.options.auditLink(traceId);
    if (!link) return null;
    return typeof link === 'string' ? { href: link } : { route: link };
  }
}

/** `['services', id] | jcLink`: the same as {@link ObservabilityLinks.to} in a template. */
@Pipe({ name: 'jcLink' })
export class LinkPipe implements PipeTransform {
  private readonly links = inject(ObservabilityLinks);
  transform(segments: unknown[]): unknown[] { return this.links.to(...segments); }
}
