/*
 * @juice-js/observability: the Juice Console pages (services, traces, topology, logs, metrics, resources, alerts,
 * rules) for an Angular app built on juice-layout.
 */
export * from './lib/core/options';
export * from './lib/core/models';
export * from './lib/core/api.service';
export * from './lib/core/context.service';
export * from './lib/core/approval-count';
export * from './lib/observability.routes';
// Console modules (core 2.0 M3): the libraries of a build, the routes and menu they make, the slots they fill.
export * from './lib/modules/console-module';
export { CORE_CONSOLE_MODULE, SLOS_CONSOLE_MODULE, AGENTS_CONSOLE_MODULE, CORE_CONSOLE_MODULES } from './lib/modules/core-modules';
export { InstalledModules, BUILTIN_MODULES, FALLBACK_MODULES, OBSERVABILITY_MODULES, OBSERVABILITY_MODULES_OPTIONS, type ObservabilityModulesOptions } from './lib/modules/installed-modules';
export { composeModules, firstSegment, type ComposedModules } from './lib/modules/module-compose';
export { menusFromManifests, ModuleMenuService, type ModuleMenuInput } from './lib/modules/module-menu';
export { ModuleSlots } from './lib/modules/module-slots';
export { installed, observabilityModuleRoutes, provideObservabilityModules } from './lib/modules/module-routes';
export { EvidenceSections, evidenceLink, routeMatches } from './lib/shared/evidence-sections';
