import { EnvironmentProviders, Injectable, InjectionToken, Pipe, PipeTransform, inject, makeEnvironmentProviders } from '@angular/core';

/** How the host app mounts Juice Observability. */
export interface ObservabilityOptions {
  /** The Juice API origin; empty when the API serves the app (same origin). */
  apiBase: string;
  /** Where the host mounts {@link OBSERVABILITY_ROUTES}: `/` for the whole app, or a path such as `/observability`. */
  basePath: string;
}

export const OBSERVABILITY_OPTIONS = new InjectionToken<ObservabilityOptions>('OBSERVABILITY_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ apiBase: '', basePath: '/' }),
});

/** Configures the pages: where the API is and where the host mounted the routes. */
export function provideJuiceObservability(options: Partial<ObservabilityOptions> = {}): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: options.apiBase ?? '', basePath: options.basePath ?? '/' } },
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
  readonly base = normalizeBasePath(inject(OBSERVABILITY_OPTIONS).basePath);

  /** `to('services', id)` → `['/observability', 'services', id]`. */
  to(...segments: unknown[]): unknown[] { return [this.base, ...segments]; }
}

/** `['services', id] | jcLink`: the same as {@link ObservabilityLinks.to} in a template. */
@Pipe({ name: 'jcLink' })
export class LinkPipe implements PipeTransform {
  private readonly links = inject(ObservabilityLinks);
  transform(segments: unknown[]): unknown[] { return this.links.to(...segments); }
}
