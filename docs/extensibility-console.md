# Modules in the Console

A module reaches the Console through the same contract the core's own pages use. The Console builds its menu and routes from two things: what the
server says (`GET /api/v1/modules`) and the module libraries compiled into the Console's build. Module code is never fetched at runtime.

## Who owns what

| | Server manifest (data) | Console library (code) |
|---|---|---|
| Menu entry: key, label, route, icon, order, policy, group | owns | no |
| Routes and page components | lists the paths | owns |
| Slots (Overview panels, service and alert sections) | no | owns |
| Go-to shortcuts (`g x`), by menu key | no | owns (`menuExtras`) |

An entry shows only when **all** hold: its module is in `/modules`, its library is in the build and defines the entry's route, and the core policy on
the entry allows it. Module policies (`<module>.x`) are not evaluated in the Console in M3; the API still enforces them. Manifest text (labels) is
rendered as text, never as markup.

## A library

```ts
import { ObservabilityConsoleModule } from '@juice-js/observability';

export const ORDERS_CONSOLE_MODULE: ObservabilityConsoleModule = {
  name: 'orders',                                   // the module's name on the server
  routes: [{ path: 'orders', loadComponent: () => import('./orders.page').then(m => m.OrdersPage) }],
  menuExtras: { orders: { shortcut: 'g r' } },      // by menu key
  overviewPanels: [{ key: 'orders-open', order: 20, component: () => import('./panel').then(m => m.OpenOrdersPanel) }],
};
```

- A route's first segment can't be one of the core's pages, `auth`, `notifications`, a parameter, a wildcard, the empty path, or another module's. The Console
  skips such a route with a warning in the console; the server refuses the same collisions when the module is composed.
- A module's routes match only when the server lists the module (`canMatch`). The core's routes have no guard.
- Slots are lazy components. Inputs: Overview panels `{ environment, range }`; service sections `{ serviceId, environment, range }`; alert sections
  `{ alertId, environment, range }`. A slot that fails to load is left out. Slots show in `order`, then `key`.

## The build

`console/src/app/console-modules.ts` exports `CONSOLE_MODULES`, which in the core is `CORE_CONSOLE_MODULES` (core, slos, agents). A distribution's
Console build (M6) overwrites that one file and adds its npm packages:

```ts
import { CORE_CONSOLE_MODULES } from '@juice-js/observability';
import { ORDERS_CONSOLE_MODULE } from '@juice-js/observability-orders';
export const CONSOLE_MODULES = [...CORE_CONSOLE_MODULES, ORDERS_CONSOLE_MODULE];
```

The host then mounts `observabilityModuleRoutes(CONSOLE_MODULES)` (each route behind its own sign-in guard) and calls
`provideObservabilityModules(CONSOLE_MODULES, { signedIn, alertNotifications })`. That replaces juice-layout's menu service with one that asks again
when `/modules` arrives, so a shell built on a page that needs no sign-in still updates.

## `/modules` and failures

- The answer is asked once and shared. Until it arrives the menu is the core's own modules (built-in manifests). Without a session nothing is asked, and
  a module route matches so the host's sign-in guard takes over.
- When it fails, the Console falls back to `builtin-manifests.json` for `core` only, with no module entries, and asks again on the next navigation.
  That file holds the internal manifests as JSON; a .NET test pins it to the served answer (`JUICE_WRITE_SNAPSHOTS=1` rewrites it).

## Evidence sections and slots on detail pages

The service and alert pages end with the module slots and `<jc-evidence-sections>`. Evidence sections come from the server's contributors
(see [extensibility-evidence.md](extensibility-evidence.md)): the Console shows text only, makes a link only for a route of an installed module's manifest
that its library defines, and shows one generic line for a section that failed, timed out or was invalid. The alert page keeps its own blocks, so
it skips the sections of `core`. If the call fails altogether nothing is shown.
