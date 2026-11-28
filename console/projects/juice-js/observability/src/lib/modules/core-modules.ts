import { ObservabilityConsoleModule } from './console-module';
import { pagesOf } from './core-pages';

/**
 * The platform's own modules as Console libraries: `core` (the investigation pages, alerts and rules), `slos` and `agents`. They live in this
 * package (decision 1), so a feature that moves to its own package (M4) takes its module with it. The go-to shortcuts are the ones the
 * Console always had.
 */
export const CORE_CONSOLE_MODULE: ObservabilityConsoleModule = {
  name: 'core',
  routes: pagesOf('core'),
  menuExtras: {
    overview: { shortcut: 'g o' }, services: { shortcut: 'g s' }, traces: { shortcut: 'g t' }, logs: { shortcut: 'g l' },
    metrics: { shortcut: 'g m' }, alerts: { shortcut: 'g a' }, incidents: { shortcut: 'g i' },
  },
};

export const SLOS_CONSOLE_MODULE: ObservabilityConsoleModule = { name: 'slos', routes: pagesOf('slos') };

export const AGENTS_CONSOLE_MODULE: ObservabilityConsoleModule = { name: 'agents', routes: pagesOf('agents') };

/** The libraries of the core's own modules, in the order the server lists them. A distribution's Console build adds its own after these. */
export const CORE_CONSOLE_MODULES: readonly ObservabilityConsoleModule[] = [CORE_CONSOLE_MODULE, SLOS_CONSOLE_MODULE, AGENTS_CONSOLE_MODULE];
