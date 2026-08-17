import {
  IPublicClientApplication,
  PublicClientApplication,
  InteractionType,
  BrowserCacheLocation,
  LogLevel,
} from '@azure/msal-browser';
import {
  MsalGuardConfiguration,
  MsalInterceptorConfiguration,
} from '@azure/msal-angular';
import { environment } from '../../../environments/environment';
import { AppAuthConfig } from '../config/runtime-config';

export function msalInstanceFactory(auth: AppAuthConfig): IPublicClientApplication {
  validateAuthConfig(auth);

  return new PublicClientApplication({
    auth: {
      clientId: auth.clientId,
      authority: auth.authority,
      redirectUri: auth.redirectUri,
      postLogoutRedirectUri: auth.postLogoutRedirectUri,
      
      navigateToLoginRequestUrl: true,
    },
    cache: {
      cacheLocation: BrowserCacheLocation.LocalStorage,
    },
    system: {
      loggerOptions: {
        
        logLevel: LogLevel.Warning,
        piiLoggingEnabled: false,
        loggerCallback: (level: LogLevel, message: string) => {
          if (level === LogLevel.Error) {
            console.error('[msal]', message);
          } else {
            console.warn('[msal]', message);
          }
        },
      },
    },
  });
}

function isAuthEnabled(auth: AppAuthConfig): boolean {
  return auth.enabled !== false;
}

function validateAuthConfig(auth: AppAuthConfig): void {
  if (!isAuthEnabled(auth)) {
    return;
  }

  const missingFields = [
    ['clientId', auth.clientId],
    ['authority', auth.authority],
    ['redirectUri', auth.redirectUri],
  ].filter(([, value]) => !value || !value.toString().trim());

  if (missingFields.length > 0) {
    const names = missingFields.map(([name]) => name).join(', ');
    throw new Error(`Invalid MSAL config. Missing fields: ${names}.`);
  }

  const scopes = apiScopesFor(auth);
  const invalidScopes = scopes.filter((scope) => scope === '/.default' || scope.startsWith('/'));
  if (invalidScopes.length > 0) {
    throw new Error(`Invalid MSAL scopes: ${invalidScopes.join(', ')}.`);
  }
}

function apiScopesFor(auth: AppAuthConfig): string[] {
  const scopes = auth.apiScopes
    ?.filter((scope): scope is string => typeof scope === 'string')
    .map((scope) => scope.trim())
    .filter(Boolean);

  if (!scopes?.length) {
    throw new Error('Invalid MSAL config. Missing apiScopes.');
  }

  return scopes;
}

export function msalGuardConfigFactory(auth: AppAuthConfig): MsalGuardConfiguration {
  if (!isAuthEnabled(auth)) {
    return {
      interactionType: InteractionType.Redirect,
    };
  }

  validateAuthConfig(auth);

  return {
    interactionType: InteractionType.Redirect,
    authRequest: { scopes: apiScopesFor(auth) },
  };
}

export function msalInterceptorConfigFactory(auth: AppAuthConfig): MsalInterceptorConfiguration {
  const protectedResourceMap = new Map<string, Array<string> | null>();
  if (isAuthEnabled(auth)) {
    validateAuthConfig(auth);
    const scopes = apiScopesFor(auth);
    const apiBase = new URL(environment.apiUrl, window.location.origin).toString();
    const apiBaseWithSlash = apiBase.endsWith('/') ? apiBase : `${apiBase}/`;
    
    protectedResourceMap.set(`${apiBaseWithSlash}health`, null);
    protectedResourceMap.set(apiBase, scopes);
    protectedResourceMap.set(apiBaseWithSlash, scopes);
  }

  return {
    interactionType: InteractionType.Redirect,
    protectedResourceMap,
  };
}
