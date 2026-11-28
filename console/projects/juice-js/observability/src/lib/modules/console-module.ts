import { Type } from '@angular/core';
import { Routes } from '@angular/router';

/**
 * A module's part of the Console (core 2.0 M3, E7): compiled into the Console's build, never fetched at runtime (W-11). The server's module
 * manifest (`GET /api/v1/modules`) is data that shows, hides, labels and orders what the library defines; it never names code to load.
 *
 * The server owns the menu entries (key, label, route, icon, order, policy and group). The library owns the routes, the components and
 * `menuExtras`, by menu key. An entry shows only when its module is installed, this library defines its route, and the core policy allows it.
 */
export interface ObservabilityConsoleModule {
  /** The module's name on the server (`/modules`). `core` is the platform itself. */
  readonly name: string;
  /** Routes relative to the Console's base path. Their first segment can't be the core's, another module's, or `auth`. */
  readonly routes: Routes;
  /** Panels on the Overview page. Inputs: `environment`, `range`. */
  readonly overviewPanels?: readonly ConsoleSlot[];
  /** Sections on the service page. Inputs: `serviceId`, `environment`, `range`. */
  readonly serviceSections?: readonly ConsoleSlot[];
  /** Sections on the alert page. Inputs: `alertId`, `environment`, `range`. */
  readonly alertSections?: readonly ConsoleSlot[];
  /** Go-to shortcuts (`g x`) for menu entries, by the entry's key. */
  readonly menuExtras?: Readonly<Record<string, { shortcut?: string }>>;
}

/** One slot's content, lazily loaded. Shown in `order`, then `key`, when its module is installed. */
export interface ConsoleSlot {
  readonly key: string;
  readonly order: number;
  readonly component: () => Promise<Type<unknown>>;
}

/** The slot kinds a page hosts. */
export type SlotKind = 'overview' | 'service' | 'alert';

export function slotsOf(module: ObservabilityConsoleModule, kind: SlotKind): readonly ConsoleSlot[] {
  return (kind === 'overview' ? module.overviewPanels : kind === 'service' ? module.serviceSections : module.alertSections) ?? [];
}
