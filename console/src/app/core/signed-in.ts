import { inject } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { Observable, defer, filter, map, of, take } from 'rxjs';

/**
 * Who is signed in, for the admin pages: the subject once there is a valid access token, checked on each subscription, else when sign-in
 * or a refresh brings one.
 */
export function signedIn(): Observable<string> {
  const oauth = inject(OAuthService);
  const user = () => String((oauth.getIdentityClaims() as { sub?: string } | null)?.sub ?? tokenSubject(oauth.getAccessToken()));
  return defer(() => oauth.hasValidAccessToken()
    ? of(user())
    : oauth.events.pipe(filter(() => oauth.hasValidAccessToken()), take(1), map(user)));
}

/** The access token's own subject (never the token itself: it changes on each refresh, and isn't kept as an id); '' if unreadable. */
function tokenSubject(token: string): string {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return String((JSON.parse(atob(payload.padEnd(Math.ceil(payload.length / 4) * 4, '='))) as { sub?: unknown }).sub ?? '');
  } catch {
    return '';
  }
}

