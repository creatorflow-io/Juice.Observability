import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { LAYOUT_TOOLBAR_ITEMS } from '@juice-js/layout';
import { InboxHubConnector, InboxHubHandlers, InboxPanelComponent, InboxService, NOTIFICATIONS_CONFIG, NotificationBellComponent, NotificationsConfig } from '@juice-js/notifications';
import { OAuthService } from 'angular-oauth2-oidc';
import { provideTranslateService } from '@ngx-translate/core';
import { inboxProviders } from './inbox';
import { loadSettings, settings } from './settings';

const TENANT = '0193f0aa-0000-7000-8000-0000000000a1';

/** An unsigned JWT whose payload has these claims. */
function jwt(claims: Record<string, unknown>): string {
  const encode = (o: unknown) => btoa(JSON.stringify(o)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${encode({ alg: 'none' })}.${encode(claims)}.`;
}

class FakeHub extends InboxHubConnector {
  connections: { url: string; tenant: string; handlers: InboxHubHandlers }[] = [];
  override connect(url: string, tenant: string, _token: () => string | null, handlers: InboxHubHandlers) {
    this.connections.push({ url, tenant, handlers });
    return { stop: async () => undefined };
  }
}

const configOf = (providers: ReturnType<typeof inboxProviders>): NotificationsConfig | undefined => {
  TestBed.configureTestingModule({ providers });
  return TestBed.inject(NOTIFICATIONS_CONFIG, null as never, { optional: true }) ?? undefined;
};

describe('Console bell (M54)', () => {
  it('bell: present only when config.json inbox is true', () => {
    expect(inboxProviders({ apiBase: '', inbox: false })).toEqual([]);
    expect(inboxProviders({ apiBase: '' })).toEqual([]);
    const providers = inboxProviders({ apiBase: '', inbox: true });
    TestBed.configureTestingModule({ providers });
    expect(TestBed.inject(LAYOUT_TOOLBAR_ITEMS)).toEqual([NotificationBellComponent]);
  });

  it('bell: inbox off adds no notifications configuration', () => {
    expect(configOf(inboxProviders({ apiBase: 'https://api.example.com', inbox: false }))).toBeUndefined();
  });

  it('bell: apiUrl is apiBase + /notifications, and the hub is /notifications/hubs/inbox', () => {
    const cross = configOf(inboxProviders({ apiBase: 'https://api.example.com', inbox: true }))!;
    expect(cross.apiUrl).toBe('https://api.example.com/notifications');
    expect(cross.hubUrl).toBeUndefined(); // the package's default is `${apiUrl}/hubs/inbox`
    TestBed.resetTestingModule();
    expect(configOf(inboxProviders({ apiBase: '', inbox: true }))!.apiUrl).toBe('/notifications');
  });

  it('bell: config.json inbox true reaches the settings, and the default is off', async () => {
    expect(settings.inbox ?? false).toBe(false);
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ inbox: true, apiBase: 'https://a.test' })));
    try {
      await loadSettings();
      expect(settings.inbox).toBe(true);
    } finally {
      fetchSpy.mockRestore();
      settings.inbox = false;
      settings.apiBase = '';
    }
  });

  describe('against the server', () => {
    let http: HttpTestingController;
    let hub: FakeHub;
    let inbox: InboxService;
    const API = '/notifications';
    const ME = `${API}/api/v1/tenants/${TENANT}/me`;

    const snackBar = { open: vi.fn() };

    beforeEach(() => {
      snackBar.open.mockClear();
      hub = new FakeHub();
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideTranslateService(),
          ...inboxProviders({ apiBase: '', inbox: true }),
          { provide: OAuthService, useValue: { getAccessToken: () => jwt({ platform_tenant: TENANT, sub: 'ann' }) } },
          { provide: InboxHubConnector, useValue: hub },
          { provide: MatSnackBar, useValue: snackBar },
        ],
      });
      http = TestBed.inject(HttpTestingController);
      inbox = TestBed.inject(InboxService);
    });

    it('bell: the inbox calls /notifications/api/v1/tenants/{platform_tenant}/me and connects the hub under /notifications', () => {
      inbox.start();
      http.expectOne(`${ME}/inbox/counts`).flush({ groups: [], unread: 0, actionableOpen: 0 });
      http.expectOne((r) => r.url === `${ME}/inbox`).flush([]);
      expect(hub.connections.length).toBe(1);
      expect(hub.connections[0].url).toBe(`${API}/hubs/inbox`);
      expect(hub.connections[0].tenant).toBe(TENANT);
      http.verify();
    });

    for (const status of [403, 404]) {
      it(`bell: a ${status} from the server shows no error loop: one request each, the error kept, no retry`, async () => {
        inbox.start();
        http.expectOne(`${ME}/inbox/counts`).flush(null, { status, statusText: 'refused' });
        http.expectOne((r) => r.url === `${ME}/inbox`).flush(null, { status, statusText: 'refused' });
        await new Promise((resolve) => setTimeout(resolve, 50));
        expect(inbox.error()).toBeTruthy();
        // No toast: the failure stays in the panel's own error line, never a snackbar (let alone one per retry).
        expect(snackBar.open).not.toHaveBeenCalled();
        expect(inbox.unread()).toBe(0);
        // Nothing asks again on its own; only the hub's own back-off reconnects.
        http.verify();
      });
    }

    it('bell: the item link with a mixed-case scheme opens the alert page', () => {
      // The real panel, as the bell shows it: a link whose scheme is in any case is followed, and any other scheme is not.
      const assign = vi.fn();
      vi.stubGlobal('location', { ...window.location, assign });
      try {
        const panel = TestBed.runInInjectionContext(() => new InboxPanelComponent());
        const open = (url: string) => {
          panel.open({ id: 'a', type: 'alert.firing', group: 'alerts', title: 't', body: null, url, actions: null, actionable: false,
            priority: 'normal', createdAt: '2026-11-28T08:00:00Z', readAt: '2026-11-28T08:00:01Z', archivedAt: null } as never);
        };
        open('HTTPS://Console.Example.COM/alerts/abc');
        open('HtTp://console.example.com/alerts/abc');
        open('javascript:alert(1)');
        expect(assign.mock.calls.map((c) => c[0])).toEqual(['HTTPS://Console.Example.COM/alerts/abc', 'HtTp://console.example.com/alerts/abc']);
      } finally {
        vi.unstubAllGlobals();
      }
    });
  });
});
