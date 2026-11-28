import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConsoleManifestDto, ConsoleMenuEntryDto, ObservabilityConsoleModule, ServerModule } from '@juice-js/observability';

/** A page that renders nothing: the tests look at which route matched, not at what it shows. */
@Component({ selector: 'app-stub', template: '' })
export class StubPage { }

@Component({ selector: 'app-not-found', template: '' })
export class NotFoundPage { }

/** A slot component: it writes the inputs a page hands its slots into the DOM, as text. */
@Component({ selector: 'app-ping-slot', template: '<span data-testid="ping-slot">{{ id() }}|{{ environment() }}|{{ range() }}</span>' })
export class PingSlot {
  readonly environment = input<string>();
  readonly range = input<string>();
  readonly serviceId = input<string>();
  readonly alertId = input<string>();
  protected id() { return this.serviceId() ?? this.alertId() ?? '-'; }
}

/** The test module `ping`: a library with two routes and a slot of each kind, and its server manifest. */
export const PING_LIBRARY: ObservabilityConsoleModule = {
  name: 'ping',
  routes: [{ path: 'ping', component: StubPage }, { path: 'ping/:id', component: StubPage }],
  menuExtras: { ping: { shortcut: 'g p' } },
  overviewPanels: [{ key: 'panel', order: 10, component: async () => PingSlot }],
  serviceSections: [{ key: 'section', order: 10, component: async () => PingSlot }],
  alertSections: [{ key: 'section', order: 10, component: async () => PingSlot }],
};

export function entry(key: string, route: string, order: number, group?: string, extra: Partial<ConsoleMenuEntryDto> = {}): ConsoleMenuEntryDto {
  return { key, label: key, route, icon: 'wifi', policy: null, order, ...(group ? { group } : {}), ...extra };
}

export function server(name: string, menu: ConsoleMenuEntryDto[], routes: string[] = menu.map(m => m.route)): ServerModule {
  const console_: ConsoleManifestDto = { package: `@juice-js/observability-${name}`, routes: routes.map(path => ({ path, policy: null })), menu };
  return { name, displayName: name, kind: 'module', console: console_ };
}

export const PING_SERVER: ServerModule = server('ping', [entry('ping', 'ping', 150, 'explore')], ['ping', 'ping/:id']);

/** The libraries' routes with every lazy page replaced by a stub, guards kept: what the router matches is unchanged. */
export function stubbed(modules: readonly ObservabilityConsoleModule[]): ObservabilityConsoleModule[] {
  return modules.map(m => ({ ...m, routes: m.routes.map(r => (r.children ? r : { path: r.path, canMatch: r.canMatch, component: StubPage })) }));
}

/** Lets effects, promises and timers settle: a guard awaits a request, the menu service awaits a tenant, effects need a tick. */
export async function settle(times = 4) {
  for (let i = 0; i < times; i++) {
    TestBed.tick();
    await new Promise(resolve => setTimeout(resolve, 0));
  }
}

export const flat = <T extends { children?: T[] }>(menus: readonly T[]): T[] => menus.flatMap(m => [m, ...flat(m.children ?? [])]);
