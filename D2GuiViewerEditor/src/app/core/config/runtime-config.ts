import { InjectionToken } from '@angular/core';

export interface AppAuthConfig {
  clientId: string;
  authority: string;
  redirectUri: string;
  postLogoutRedirectUri: string;
  apiScopes: string[];
  enabled?: boolean;
}

type RuntimeAuthConfigInput = Partial<AppAuthConfig> & {
  redirectUrl?: string;
};

export const DEFAULT_AUTH_CONFIG: AppAuthConfig = {
  clientId: '',
  authority: '',
  redirectUri: '/',
  postLogoutRedirectUri: '/',
  apiScopes: [],
  enabled: true,
};

export const MSAL_CUSTOM_CONFIG = new InjectionToken<AppAuthConfig>('MSAL_CUSTOM_CONFIG', {
  providedIn: 'root',
  factory: () => DEFAULT_AUTH_CONFIG,
});

export function mergeAuthConfig(raw: RuntimeAuthConfigInput | undefined | null): AppAuthConfig {
  const a = raw ?? {};
  const runtimeScopes = Array.isArray(a.apiScopes)
    ? a.apiScopes.filter((scope): scope is string => typeof scope === 'string').map((scope) => scope.trim()).filter(Boolean)
    : undefined;

  return {
    clientId: a.clientId ?? DEFAULT_AUTH_CONFIG.clientId,
    authority: a.authority ?? DEFAULT_AUTH_CONFIG.authority,
    
    redirectUri: a.redirectUri ?? a.redirectUrl ?? DEFAULT_AUTH_CONFIG.redirectUri,
    postLogoutRedirectUri: a.postLogoutRedirectUri ?? DEFAULT_AUTH_CONFIG.postLogoutRedirectUri,
    apiScopes: runtimeScopes ?? DEFAULT_AUTH_CONFIG.apiScopes,
    enabled: a.enabled ?? DEFAULT_AUTH_CONFIG.enabled ?? true,
  };
}

export function isAuthDisabled(config: AppAuthConfig): boolean {
  return config.enabled === false;
}
