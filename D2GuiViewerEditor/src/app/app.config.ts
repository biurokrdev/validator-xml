import {
  ApplicationConfig,
  ErrorHandler,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import {
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import {
  MSAL_INSTANCE,
  MSAL_GUARD_CONFIG,
  MsalService,
  MsalGuard,
  MsalBroadcastService,
} from '@azure/msal-angular';

import { routes } from './app.routes';
import { apiTokenInterceptor } from './core/interceptors/api-token.interceptor';
import { httpErrorInterceptor } from './core/interceptors/http-error.interceptor';
import { GlobalErrorHandler } from './core/error-handling/global-error-handler';
import { ConnectionStatusService } from './core/services/connection-status.service';
import { MSAL_CUSTOM_CONFIG } from './core/config/runtime-config';
import {
  msalInstanceFactory,
  msalGuardConfigFactory,
} from './core/auth/msal.config';
import { primeApiToken } from './core/auth/api-token-primer';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),

    provideHttpClient(withInterceptors([apiTokenInterceptor, httpErrorInterceptor])),

    { provide: MSAL_INSTANCE, useFactory: msalInstanceFactory, deps: [MSAL_CUSTOM_CONFIG] },
    { provide: MSAL_GUARD_CONFIG, useFactory: msalGuardConfigFactory, deps: [MSAL_CUSTOM_CONFIG] },
    MsalService,
    MsalGuard,
    MsalBroadcastService,

    provideAppInitializer(async () => {
      const msal = inject(MsalService);
      
      const authConfig = inject(MSAL_CUSTOM_CONFIG);
      await msal.instance.initialize();

      if (!msal.instance.getActiveAccount()) {
        const firstAccount = msal.instance.getAllAccounts()[0] ?? null;
        if (firstAccount) {
          msal.instance.setActiveAccount(firstAccount);
        }
      }

      void primeApiToken(msal.instance, authConfig);
    }),

    { provide: ErrorHandler, useClass: GlobalErrorHandler },

    provideAppInitializer(() => {
      inject(ConnectionStatusService).checkNow();
    }),
  ],
};
