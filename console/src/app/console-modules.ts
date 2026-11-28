import { CORE_CONSOLE_MODULES, ObservabilityConsoleModule } from '@juice-js/observability';

/**
 * The libraries compiled into this Console: the core's own (core, slos, agents). A distribution's Console build overwrites this one file and adds its
 * npm packages' modules after these (docs/extensibility-console.md). Module code enters the Console here, at build time, and nowhere else.
 */
export const CONSOLE_MODULES: readonly ObservabilityConsoleModule[] = CORE_CONSOLE_MODULES;
