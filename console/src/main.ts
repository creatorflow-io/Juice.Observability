import { platformBrowser } from '@angular/platform-browser';
import { loadSettings } from './app/core/settings';

// Settings first: the auth module reads them when the app module is created.
loadSettings()
  .then(() => import('./app/app.module'))
  .then(({ AppModule }) => platformBrowser().bootstrapModule(AppModule))
  .catch((err) => console.error(err));
