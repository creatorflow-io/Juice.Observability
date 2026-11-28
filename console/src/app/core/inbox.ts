import { EnvironmentProviders, Provider } from '@angular/core';
import { LAYOUT_TOOLBAR_ITEMS } from '@juice-js/layout';
import { NotificationBellComponent, provideNotifications } from '@juice-js/notifications';
import type { ConsoleSettings } from './settings';

/**
 * The inbox bell (M54), only when the deployment's `config.json` says `"inbox": true` (the host maps the inbox under `/notifications`).
 * The bell reads its tenant from the token's `platform_tenant` claim and calls `{apiBase}/notifications/api/v1/tenants/{tenant}/me/...`;
 * its hub is `{apiBase}/notifications/hubs/inbox`. Off, nothing is provided and the toolbar is as before.
 */
export function inboxProviders(s: Pick<ConsoleSettings, 'apiBase' | 'inbox'>): (Provider | EnvironmentProviders)[] {
  if (!s.inbox) {
    return [];
  }
  return [
    provideNotifications({ apiUrl: `${s.apiBase}/notifications` }),
    { provide: LAYOUT_TOOLBAR_ITEMS, useValue: NotificationBellComponent, multi: true },
  ];
}
