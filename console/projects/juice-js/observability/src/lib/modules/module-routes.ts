import { EnvironmentProviders, Provider, inject, makeEnvironmentProviders, provideEnvironmentInitializer } from '@angular/core';
import { CanMatchFn, Route, Routes } from '@angular/router';
import { MENUS, MenuService } from '@juice-js/core';
import { ObservabilityConsoleModule } from './console-module';
import { InstalledModules, OBSERVABILITY_MODULES, OBSERVABILITY_MODULES_OPTIONS, ObservabilityModulesOptions } from './installed-modules';
import { composeModules } from './module-compose';
import { MODULE_MENU, ModuleMenuService } from './module-menu';

/** Matches a module route only when the server says the module is installed (and the user is signed in: without a session the host's guard decides). */
export function installed(name: string): CanMatchFn {
  return async () => {
    const modules = inject(InstalledModules);
    // Without a session nothing is asked: the route matches, and the host's guard sends the user to sign in.
    // Without a `signedIn` option the check is whether a page has fetched the tenant; a 401 from /modules also defers to sign-in.
    if (!modules.signedIn()) return true;
    await modules.load();
    return modules.state() === 'loading' || modules.names().has(name);
  };
}

/**
 * The routes of the libraries in a build, relative to the host's base path. The core's have no guard; every other module's route matches only when
 * that module is installed. The host adds its own guards (sign-in) as before.
 */
export function observabilityModuleRoutes(modules: readonly ObservabilityConsoleModule[]): Routes {
  const { modules: composed, warnings } = composeModules(modules);
  for (const warning of warnings) console.warn(`[observability] ${warning}`);
  return composed.flatMap(module => module.name === 'core'
    ? module.routes
    : module.routes.map((route): Route => ({ ...route, canMatch: [...(route.canMatch ?? []), installed(module.name)] })));
}

/**
 * Provides the libraries of this build to the pages (slots), the menu and the guards: the Console's menu is built from `/modules` and these
 * libraries, and updates when `/modules` arrives. Mount the routes with {@link observabilityModuleRoutes}. `signedIn` may call `inject`.
 */
export function provideObservabilityModules(modules: readonly ObservabilityConsoleModule[], options: ObservabilityModulesOptions = {}): EnvironmentProviders {
  const providers: Provider[] = [
    { provide: OBSERVABILITY_MODULES, useValue: composeModules(modules).modules },
    { provide: OBSERVABILITY_MODULES_OPTIONS, useValue: options },
    { provide: MenuService, useClass: ModuleMenuService },
    // The side menu reads MENUS each time it asks: this list is rebuilt from the manifests (see ModuleMenuService).
    { provide: MENUS, multi: true, useFactory: () => inject(MODULE_MENU) },
  ];
  return makeEnvironmentProviders([
    ...providers,
    // The shared answer starts to be asked for as soon as a page knows the user is signed in.
    provideEnvironmentInitializer(() => { inject(InstalledModules); }),
  ]);
}
