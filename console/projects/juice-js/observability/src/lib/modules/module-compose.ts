import { ObservabilityConsoleModule } from './console-module';

/** The text before the first `/` of a route path: `alerts` for `alerts/:id`, empty for the Overview. */
export function firstSegment(path: string | undefined): string {
  const p = path ?? '';
  const slash = p.indexOf('/');
  return slash < 0 ? p : p.slice(0, slash);
}

/** First segments that belong to the Console itself, whatever module is in the build: the sign-in callback and the old notification pages' redirect. */
const HOST_SEGMENTS: ReadonlySet<string> = new Set(['auth', 'notifications']);

export interface ComposedModules {
  /** The libraries that remain: a module name taken twice keeps its first, and a route that collides is left out. */
  readonly modules: readonly ObservabilityConsoleModule[];
  readonly warnings: readonly string[];
}

/**
 * Checks the libraries of a build against each other: a route's first segment can't be the core's (or `auth`, `notifications`), another
 * module's, a parameter or a wildcard; only `core` takes the empty path. A route that breaks a rule is skipped with a warning, so one bad
 * library can't take a page from another. The server refuses the same collisions in a module's manifest.
 */
export function composeModules(modules: readonly ObservabilityConsoleModule[]): ComposedModules {
  const warnings: string[] = [];
  const owners = new Map<string, string>();
  const core = modules.find(m => m.name === 'core');
  const coreSegments = new Set((core?.routes ?? []).map(r => firstSegment(r.path)).filter(s => s.length > 0));
  const names = new Set<string>();
  const kept: ObservabilityConsoleModule[] = [];
  for (const module of modules) {
    if (names.has(module.name)) { warnings.push(`module '${module.name}' is in the build twice: the second is ignored`); continue; }
    names.add(module.name);
    const routes = module.routes.filter(route => {
      const path = route.path ?? '';
      const segment = firstSegment(path);
      const bad = module.name === 'core' ? null
        : path === '' ? 'the empty path is the Overview'
        : segment.startsWith(':') || segment.includes('*') ? 'a parameter or a wildcard can\'t start a module route'
        : HOST_SEGMENTS.has(segment) || coreSegments.has(segment) ? `'${segment}' is a page of the core`
        : owners.has(segment) && owners.get(segment) !== module.name ? `'${segment}' belongs to module '${owners.get(segment)}'`
        : null;
      if (bad) { warnings.push(`module '${module.name}' route '${path}' skipped: ${bad}`); return false; }
      if (segment) owners.set(segment, module.name);
      return true;
    });
    kept.push(routes.length === module.routes.length ? module : { ...module, routes });
  }
  return { modules: kept, warnings };
}
