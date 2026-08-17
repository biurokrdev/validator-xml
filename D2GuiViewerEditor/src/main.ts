import 'zone.js';
import { bootstrapApplication } from '@angular/platform-browser';
import { MsalRedirectComponent } from '@azure/msal-angular';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { AppAuthConfig, MSAL_CUSTOM_CONFIG, mergeAuthConfig } from './app/core/config/runtime-config';

fetch('assets/configs/config.json', { cache: 'no-store' })
  .then((response) => (response.ok ? response.json() : {}))
  .catch(() => ({}))
  .then((config: { auth?: Partial<AppAuthConfig> } | null) => {
    const auth = mergeAuthConfig(config?.auth);
    bootstrapApplication(App, {
      ...appConfig,
      providers: [{ provide: MSAL_CUSTOM_CONFIG, useValue: auth }, ...appConfig.providers],
    })
      
      .then((appRef) => appRef.bootstrap(MsalRedirectComponent))
      .catch((err) => console.error(err));
  });
