import { inject } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';

/** Whether the signed-in user's access token carries one of the roles (the API's own `roles` claim), read on each call. */
function hasRole(oauth: OAuthService, wanted: readonly string[]): boolean {
  try {
    const payload = oauth.getAccessToken().split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const roles = (JSON.parse(atob(payload.padEnd(Math.ceil(payload.length / 4) * 4, '='))) as { roles?: unknown }).roles;
    return (Array.isArray(roles) ? roles : [roles]).some(r => wanted.includes(String(r).toLowerCase()));
  } catch {
    return false;
  }
}

/** Whether the signed-in user's access token carries the `admin` role. */
export function canAdmin(): () => boolean {
  const oauth = inject(OAuthService);
  return () => hasRole(oauth, ['admin']);
}

/** Whether the token carries the `admin` or the `operator` role: who may run a rule preview. */
export function canOperate(): () => boolean {
  const oauth = inject(OAuthService);
  return () => hasRole(oauth, ['admin', 'operator']);
}
