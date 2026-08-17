import { mergeAuthConfig, DEFAULT_AUTH_CONFIG } from './runtime-config';

describe('mergeAuthConfig', () => {
  it('returns the build-time defaults when the runtime config is null/undefined', () => {
    expect(mergeAuthConfig(null)).toEqual(DEFAULT_AUTH_CONFIG);
    expect(mergeAuthConfig(undefined)).toEqual(DEFAULT_AUTH_CONFIG);
    expect(mergeAuthConfig({})).toEqual(DEFAULT_AUTH_CONFIG);
  });

  it('overrides only the fields present in the runtime config', () => {
    const merged = mergeAuthConfig({
      clientId: 'real-client-id',
      authority: 'https://login.microsoftonline.com/real-tenant',
    });

    expect(merged.clientId).toBe('real-client-id');
    expect(merged.authority).toBe('https://login.microsoftonline.com/real-tenant');
    
    expect(merged.redirectUri).toBe(DEFAULT_AUTH_CONFIG.redirectUri);
    expect(merged.postLogoutRedirectUri).toBe(DEFAULT_AUTH_CONFIG.postLogoutRedirectUri);
    expect(merged.apiScopes).toEqual(DEFAULT_AUTH_CONFIG.apiScopes);
  });

  it('replaces apiScopes when provided, keeps default array when absent', () => {
    expect(mergeAuthConfig({ apiScopes: ['api://x/access'] }).apiScopes).toEqual(['api://x/access']);
    expect(mergeAuthConfig({}).apiScopes).toEqual(DEFAULT_AUTH_CONFIG.apiScopes);
  });

  it('accepts legacy redirectUrl as redirectUri alias', () => {
    const merged = mergeAuthConfig({ redirectUrl: 'https://viewer.example.com' });
    expect(merged.redirectUri).toBe('https://viewer.example.com');
  });

  it('normalizes apiScopes by trimming and removing empty values', () => {
    const merged = mergeAuthConfig({ apiScopes: [' User.Read ', '   ', 'api://x/access_as_user'] });
    expect(merged.apiScopes).toEqual(['User.Read', 'api://x/access_as_user']);
  });

  it('does not mutate the defaults object', () => {
    const snapshot = JSON.stringify(DEFAULT_AUTH_CONFIG);
    mergeAuthConfig({ clientId: 'x', apiScopes: ['y'] });
    expect(JSON.stringify(DEFAULT_AUTH_CONFIG)).toBe(snapshot);
  });
});
