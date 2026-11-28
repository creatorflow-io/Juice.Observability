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
  /**
   * Where the host mounted the notification pages (`@juice-js/notifications-admin`, phase 5 M48), such as `/alerts/notifications`: the
   * escalation page and the alert page link to them (an absolute URL is used as written). Without it, no link.
   */
  notificationsLink?: string | null;
  /** Show on the Approvals menu entry how many action requests wait for the signed-in user. On unless false. */
  approvalCount?: boolean;
}

export const OBSERVABILITY_OPTIONS = new InjectionToken<ObservabilityOptions>('OBSERVABILITY_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ apiBase: '', basePath: '/', auditLink: null, notificationsLink: null }),
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
        notificationsLink: options.notificationsLink ?? null,
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

  /**
   * A Notifications admin page (`<notificationsLink>/go/<segments>`; `go` picks the tenant the signed-in user may use). Without
   * `notificationsLink`, no link. A configured absolute URL is used as written, as text with its host case and port kept, never
   * re-serialised; a path becomes a router link.
   */
  admin(...segments: string[]): { route: unknown[] } | { href: string } | null {
    const base = this.options.notificationsLink;
    if (!base) return null;
    if (/^https?:\/\//i.test(base)) return { href: `${base.replace(/\/+$/, '')}/go/${segments.map(encodeURIComponent).join('/')}` };
    return { route: [normalizeBasePath(base), 'go', ...segments] };
  }

  /** The admin page of a correlation of this platform's alerts (`alert:{id}`). */
  correlation(key: string | null | undefined) { return key ? this.admin('correlations', 'observability', key) : null; }


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

/**
 * Whether the signed-in user has the admin role, which the API requires to change an escalation selection. The host answers it from its
 * sign-in (Console: the access token's roles); the default is true, which hides nothing, and the API still refuses a non-admin with 403.
 */
export const OBSERVABILITY_CAN_ADMIN = new InjectionToken<() => boolean>('OBSERVABILITY_CAN_ADMIN', { providedIn: 'root', factory: () => () => true });

/**
 * Whether the signed-in user is an admin or an operator, who may run a rule preview (a viewer may not). The host answers it from its sign-in
 * as {@link OBSERVABILITY_CAN_ADMIN} is; the default is true, which hides nothing, and the API still refuses a viewer with 403.
 */
export const OBSERVABILITY_CAN_OPERATE = new InjectionToken<() => boolean>('OBSERVABILITY_CAN_OPERATE', { providedIn: 'root', factory: () => () => true });
