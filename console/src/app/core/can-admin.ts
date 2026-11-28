import { inject } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';

/** Whether the signed-in user's access token carries the `admin` role (the API's own `roles` claim), read on each call. */
export function canAdmin(): () => boolean {
  const oauth = inject(OAuthService);
  return () => {
    try {
      const payload = oauth.getAccessToken().split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
      const roles = (JSON.parse(atob(payload.padEnd(Math.ceil(payload.length / 4) * 4, '='))) as { roles?: unknown }).roles;
      return (Array.isArray(roles) ? roles : [roles]).some(r => String(r).toLowerCase() === 'admin');
    } catch {
      return false;
    }
  };
}
