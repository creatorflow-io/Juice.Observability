/**
 * Runtime settings, read from `config.json` before the app starts, so one build serves every deployment: the
 * deployment mounts its own `config.json` with its identity provider and API address.
 */
export interface ConsoleSettings {
  /** The Juice API origin; empty when the Console is served by the API itself (same origin). */
  apiBase: string;
  auth: {
    issuer: string;
    clientId: string;
    scope: string;
  };
}

/** Filled by `main.ts` before bootstrap; module configuration reads it then. */
export const settings: ConsoleSettings = {
  apiBase: '',
  auth: { issuer: '', clientId: 'juice-console', scope: 'openid profile email' },
};

/** The options `AuthModule.forRoot` reads, derived from {@link settings} once they are loaded. */
export const authOptions: Record<string, unknown> = {};

export async function loadSettings(): Promise<void> {
  const response = await fetch('config.json', { cache: 'no-store' });
  if (response.ok) {
    const loaded = (await response.json()) as Partial<ConsoleSettings>;
    Object.assign(settings, loaded, { auth: { ...settings.auth, ...loaded.auth } });
  }
  const origin = window.location.origin;
  Object.assign(authOptions, {
    issuer: settings.auth.issuer,
    clientId: settings.auth.clientId,
    scope: settings.auth.scope,
    responseType: 'code',
    redirectUri: `${origin}/auth/login-completed`,
    postLogoutRedirectUri: `${origin}/auth/logout-completed`,
    basePath: `${origin}/auth`,
    // The bearer token goes only to the Juice API, never to another host.
    allowedUrls: settings.apiBase ? ['', settings.apiBase] : [''],
    // Renew before expiry with the session's refresh token. The library's default requireHttps
    // ("remoteOnly") allows plain HTTP only for a localhost issuer.
    automaticRefresh: true,
  });
}
