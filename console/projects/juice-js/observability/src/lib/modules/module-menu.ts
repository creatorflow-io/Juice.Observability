import { Injectable, InjectionToken, Injector, inject } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { Menus, MenuService, TenantService } from '@juice-js/core';
import { switchMap } from 'rxjs';
import { OBSERVABILITY_CAN_OPERATE, OBSERVABILITY_OPTIONS, normalizeBasePath } from '../core/options';
import { ConsoleMenuEntryDto, ServerModule } from '../core/models';
import { alertNotificationsGroup } from '../observability.routes';
import { ObservabilityConsoleModule } from './console-module';
import { InstalledModules, OBSERVABILITY_MODULES, OBSERVABILITY_MODULES_OPTIONS } from './installed-modules';
import { composeModules } from './module-compose';

/** The core's groups: the server names one by id, the Console owns its label and icon. Their menu ids carry an `observability-` prefix. */
const GROUPS: readonly { id: string; label: string; icon: string }[] = [
  { id: 'explore', label: 'Explore', icon: 'explore' },
  { id: 'automation', label: 'Automation', icon: 'precision_manufacturing' },
  { id: 'configure', label: 'Configure', icon: 'tune' },
];

const DEFAULT_ICON = 'extension';

export interface ModuleMenuInput {
  /** What `/modules` said (or the built-in answer). */
  readonly servers: readonly ServerModule[];
  /** The libraries of this build. */
  readonly libraries: readonly ObservabilityConsoleModule[];
  readonly basePath?: string;
  /** The `order` of the first entry; the others follow in sequence. */
  readonly order?: number;
  readonly alertNotifications?: Menus;
  /** Whether the user may use `platform.operate` entries. Module policies aren't evaluated here: the API enforces them. */
  readonly canOperate?: () => boolean;
  readonly warn?: (message: string) => void;
}

/**
 * The side menu from the server's manifests and the libraries in the build. The server owns each entry (key, label, route, icon, order, group); the
 * library owns the routes and the go-to shortcuts. An entry shows only when its module is installed, its library is in the build and defines the
 * route, and a core policy allows it. Manifest text is data: it ends up as text in the menu, never as markup.
 *
 * With the core's own manifests this is the menu the Console always had, entry for entry (a test pins it to a snapshot taken before manifests).
 */
export function menusFromManifests(input: ModuleMenuInput): Menus {
  const base = normalizeBasePath(input.basePath ?? '/');
  const link = (path: string) => (base === '/' ? `/${path}` : path ? `${base}/${path}` : base);
  const warn = input.warn ?? (() => undefined);
  const libraries = new Map(composeModules(input.libraries).modules.map(m => [m.name, m]));

  const entries: { entry: ConsoleMenuEntryDto; module: ObservabilityConsoleModule }[] = [];
  const keys = new Set<string>();
  for (const server of input.servers) {
    const library = libraries.get(server.name);
    if (!library || !server.console) continue;
    const routes = new Set(library.routes.map(r => r.path ?? ''));
    for (const entry of server.console.menu ?? []) {
      if (!routes.has(entry.route)) { warn(`menu entry '${entry.key}' of '${server.name}' is dropped: the library has no route '${entry.route}'`); continue; }
      if (entry.policy === 'platform.operate' && input.canOperate && !input.canOperate()) continue;
      if (keys.has(entry.key)) { warn(`menu entry '${entry.key}' of '${server.name}' is dropped: the key is taken`); continue; }
      if (entry.group && !GROUPS.some(g => g.id === entry.group)) { warn(`menu entry '${entry.key}' of '${server.name}' is dropped: unknown group '${entry.group}'`); continue; }
      keys.add(entry.key);
      entries.push({ entry, module: library });
    }
  }
  entries.sort((a, b) => a.entry.order - b.entry.order || a.entry.key.localeCompare(b.entry.key));

  const item = ({ entry, module }: (typeof entries)[number]): Menus[number] => {
    const shortcut = module.menuExtras?.[entry.key]?.shortcut;
    return { id: entry.key, label: entry.label, icon: entry.icon ?? DEFAULT_ICON, routerLink: link(entry.route), ...(shortcut ? { shortcut } : {}) };
  };
  const top = entries.filter(e => !e.entry.group).map(item);
  const groups = GROUPS.flatMap(g => {
    const children = entries.filter(e => e.entry.group === g.id).map(item);
    return children.length
      ? [{ id: `observability-${g.id}`, label: g.label, icon: g.icon, routerLink: children[0].routerLink, replaceChildRouterLink: false, children } as Menus[number]]
      : [];
  });
  // The host's Alerts › Notifications group sits right after Alerts.
  const notifications = input.alertNotifications?.length ? [alertNotificationsGroup(input.alertNotifications)] : [];
  const alerts = top.findIndex(m => m.id === 'alerts');
  const ordered = alerts < 0 ? [...top, ...notifications, ...groups] : [...top.slice(0, alerts + 1), ...notifications, ...top.slice(alerts + 1), ...groups];
  const first = input.order ?? 1;
  return ordered.map((menu, index) => ({ ...menu, order: first + index }));
}

/** The list the side menu reads (`MENUS`): rewritten in place from the manifests, so a menu built early follows `/modules`. */
export const MODULE_MENU = new InjectionToken<Menus>('MODULE_MENU', { providedIn: 'root', factory: () => [] });

/**
 * juice-layout's menu service, asked again whenever the installed modules change: a shell built on a page that needs no sign-in (the sign-in
 * callback, the not-found page) still shows the modules once `/modules` arrives. No juice-layout change: the base service reads `MENUS` on
 * every call, and this refills that list first.
 */
@Injectable()
export class ModuleMenuService extends MenuService {
  private readonly installed = inject(InstalledModules);
  private readonly libraries = inject(OBSERVABILITY_MODULES);
  private readonly options = inject(OBSERVABILITY_MODULES_OPTIONS);
  private readonly links = inject(OBSERVABILITY_OPTIONS);
  private readonly operate = inject(OBSERVABILITY_CAN_OPERATE);
  private readonly menu = inject(MODULE_MENU);
  private readonly self = inject(Injector);

  constructor() {
    super(inject(Injector), inject(TenantService));
  }

  override getMenu() {
    return toObservable(this.installed.modules, { injector: this.self }).pipe(switchMap(servers => {
      this.menu.splice(0, this.menu.length, ...menusFromManifests({
        servers, libraries: this.libraries, basePath: this.links.basePath, order: this.options.menuOrder,
        alertNotifications: this.options.alertNotifications, canOperate: this.operate,
        warn: message => console.warn(`[observability] ${message}`),
      }));
      return super.getMenu();
    }));
  }
}
