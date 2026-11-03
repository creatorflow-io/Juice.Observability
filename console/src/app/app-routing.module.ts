import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PageComponent, PageNotfoundComponent } from '@juice-js/layout';
import { MenuModule } from '@juice-js/core';
import { AuthGuard } from '@juice-js/auth';
import { OBSERVABILITY_ROUTES, observabilityMenus } from '@juice-js/observability';

/**
 * Juice Console is the observability pages (`@juice-js/observability`) mounted at the root of a juice-layout shell,
 * each behind sign-in. An app of the customer's own mounts the same routes and menus under a path of its choosing.
 */
const routes: Routes = [
  {
    path: '',
    component: PageComponent,
    children: [
      ...OBSERVABILITY_ROUTES.map(route => ({ ...route, canActivate: [AuthGuard] })),
      { path: 'auth', loadChildren: () => import('@juice-js/auth').then(m => m.AuthRoutingModule) },
      { path: '**', component: PageNotfoundComponent },
    ],
  },
];

@NgModule({
  imports: [RouterModule.forRoot(routes, { bindToComponentInputs: true }), MenuModule.forRoot(observabilityMenus('/'))],
  exports: [RouterModule, MenuModule],
})
export class AppRoutingModule { }
