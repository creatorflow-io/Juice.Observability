import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PageComponent, PageNotfoundComponent } from '@juice-js/layout';
import { MenuModule } from '@juice-js/core';
import { AuthGuard } from '@juice-js/auth';
import { OBSERVABILITY_ROUTES, observabilityMenus } from '@juice-js/observability';
import { NOTIFICATIONS_ADMIN_PAGES, NOTIFICATIONS_ADMIN_ROUTES, notificationsAdminMenus } from '@juice-js/notifications-admin';

/** Where the notification pages (`@juice-js/notifications-admin`, phase 5 M48) are mounted: Alerts › Notifications. */
export const NOTIFICATIONS_BASE_PATH = '/alerts/notifications';

/**
 * Juice Console is the observability pages (`@juice-js/observability`) mounted at the root of a juice-layout shell,
 * each behind sign-in. An app of the customer's own mounts the same routes and menus under a path of its choosing.
 * The notification pages sit under Alerts, before the observability routes (whose `alerts/:id` would take them), with the pages an
 * embedded pipeline has.
 */
export const routes: Routes = [
  {
    path: '',
    component: PageComponent,
    children: [
      {
        path: NOTIFICATIONS_BASE_PATH.slice(1),
        children: NOTIFICATIONS_ADMIN_ROUTES.map(route => ({ ...route, canActivate: [AuthGuard, ...(route.canActivate ?? [])] })),
      },
      ...OBSERVABILITY_ROUTES.map(route => ({ ...route, canActivate: [AuthGuard] })),
      { path: 'auth', loadChildren: () => import('@juice-js/auth').then(m => m.AuthRoutingModule) },
      { path: '**', component: PageNotfoundComponent },
    ],
  },
];

@NgModule({
  imports: [
    // The notification pages read the tenant id from their parent route (paramsInheritanceStrategy) as an input.
    RouterModule.forRoot(routes, { bindToComponentInputs: true, paramsInheritanceStrategy: 'always' }),
    MenuModule.forRoot(observabilityMenus('/', 1, notificationsAdminMenus(NOTIFICATIONS_BASE_PATH, 1, NOTIFICATIONS_ADMIN_PAGES.embedded))),
  ],
  exports: [RouterModule, MenuModule],
})
export class AppRoutingModule { }
