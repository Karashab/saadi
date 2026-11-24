import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';

interface PublicConfig { authority: string | null; clientId: string }

let manager: UserManager | null = null;
let current: User | null = null;

/** Development/test builds without an identity server read a bearer token from sessionStorage instead. */
export async function initAuth(): Promise<'ready' | 'dev'> {
  const config: PublicConfig = await fetch('/config.json').then(r => (r.ok ? r.json() : { authority: null, clientId: '' })).catch(() => ({ authority: null, clientId: '' }));
  if (!config.authority) return 'dev';
  manager = new UserManager({
    authority: config.authority,
    client_id: config.clientId,
    redirect_uri: `${location.origin}/`,
    post_logout_redirect_uri: `${location.origin}/`,
    response_type: 'code',
    scope: 'openid profile email',
    automaticSilentRenew: true,
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  });
  manager.events.addUserLoaded(user => { current = user; });
  manager.events.addUserUnloaded(() => { current = null; });
  manager.events.addSilentRenewError(() => { current = null; });

  const params = new URLSearchParams(location.search);
  if (params.has('code') && params.has('state')) {
    current = await manager.signinRedirectCallback();
    const target = (current.state as string | undefined) ?? '#overview';
    history.replaceState(null, '', `${location.pathname}${target}`);
  } else {
    current = await manager.getUser();
    if (current?.expired) current = await manager.signinSilent().catch(() => null);
  }
  return 'ready';
}

export const signIn = () => manager?.signinRedirect({ state: location.hash || '#overview' });
export const signOut = async () => {
  if (manager) await manager.signoutRedirect();
  else { sessionStorage.removeItem('noor-token'); location.reload(); }
};

export function getToken(): string | null {
  if (manager) return current && !current.expired ? current.access_token : null;
  try { return sessionStorage.getItem('noor-token'); } catch { return null; }
}
