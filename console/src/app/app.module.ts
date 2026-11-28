import { ChangeDetectionStrategy, Component, NgModule } from '@angular/core';
import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { BrowserModule } from '@angular/platform-browser';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { RouterOutlet } from '@angular/router';
import { MissingTranslationHandler, TranslatePipe, provideTranslateService } from '@ngx-translate/core';
import { OAuthModule } from 'angular-oauth2-oidc';
import { LayoutModule, UserProfileDialogModule } from '@juice-js/layout';
import { LocalizeModule } from '@juice-js/localize';
import { AuthModule } from '@juice-js/auth';
import { AppRoutingModule } from './app-routing.module';
import { OBSERVABILITY_CAN_ADMIN, provideJuiceObservability } from '@juice-js/observability';
import { provideNotificationsAdmin } from '@juice-js/notifications-admin';
import { authOptions, settings } from './core/settings';
import { ShowKeyHandler } from './core/show-key.handler';
import { signedIn } from './core/signed-in';
import { canAdmin } from './core/can-admin';
import { NOTIFICATIONS_BASE_PATH } from './app-routing.module';

@Component({
  selector: 'app-root',
  template: '<router-outlet />',
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent { }

/**
 * Juice Console: the juice-layout shell (toolbar, menu, theme, user profile) with sign-in through the deployment's
 * identity provider. No tenant segment in URLs: one deployment serves one tenant (requirements §2.1).
 */
@NgModule({
  imports: [
    BrowserModule,
    BrowserAnimationsModule,
    LayoutModule.forRoot(true, { brand: 'Juice Observability', brandLogo: 'logo.svg', defaultMenuOpen: true }),
    AuthModule.forRoot(authOptions as never),
    UserProfileDialogModule,
    OAuthModule.forRoot(),
    // No remote translations: the Console's text is English as written.
    LocalizeModule.forRoot({ localizeApi: '', cultureApi: '' }),
    TranslatePipe,
    AppRoutingModule,
  ],
  providers: [
    provideHttpClient(withInterceptorsFromDi()),
    provideJuiceObservability({ apiBase: settings.apiBase, basePath: '/', notificationsLink: NOTIFICATIONS_BASE_PATH }),
    // The notification pages: the host maps their API under /notifications (Juice.Notifications.Admin), on the API's origin.
    { provide: OBSERVABILITY_CAN_ADMIN, useFactory: canAdmin },
    provideNotificationsAdmin({ apiBase: `${settings.apiBase}/notifications`, basePath: NOTIFICATIONS_BASE_PATH, signedIn }),
    provideTranslateService({ missingTranslationHandler: { provide: MissingTranslationHandler, useClass: ShowKeyHandler } }),
  ],
  bootstrap: [AppComponent],
})
export class AppModule { }
