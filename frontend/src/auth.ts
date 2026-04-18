import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';

type FrontendConfig = {
  oidcAuthority: string;
  oidcClientId: string;
};

let userManagerPromise: Promise<UserManager> | null = null;

async function fetchFrontendConfig(): Promise<FrontendConfig> {
  const response = await fetch('/api/config/frontend');
  if (!response.ok) {
    throw new Error(`Failed to load frontend config: ${response.status}`);
  }
  const raw = (await response.json()) as {
    oidcAuthority?: string | null;
    oidcClientId?: string | null;
  };
  if (!raw.oidcAuthority || !raw.oidcClientId) {
    throw new Error('Frontend config missing oidcAuthority or oidcClientId');
  }
  return { oidcAuthority: raw.oidcAuthority, oidcClientId: raw.oidcClientId };
}

function getUserManager(): Promise<UserManager> {
  if (!userManagerPromise) {
    userManagerPromise = fetchFrontendConfig().then(
      (config) =>
        new UserManager({
          authority: config.oidcAuthority,
          client_id: config.oidcClientId,
          redirect_uri: `${window.location.origin}/callback`,
          post_logout_redirect_uri: window.location.origin,
          response_type: 'code',
          scope: 'openid profile email',
          automaticSilentRenew: true,
          userStore: new WebStorageStateStore({ store: sessionStorage }),
        }),
    );
  }
  return userManagerPromise;
}

export type AuthUser = User;

export async function getUser(): Promise<User | null> {
  const userManager = await getUserManager();
  return userManager.getUser();
}

export async function getAccessToken(): Promise<string | null> {
  const userManager = await getUserManager();
  const user = await userManager.getUser();
  if (!user || user.expired) return null;
  return user.access_token;
}

export async function login(): Promise<void> {
  const userManager = await getUserManager();
  await userManager.signinRedirect({ state: window.location.pathname });
}

export async function handleCallback(): Promise<string> {
  const userManager = await getUserManager();
  const user = await userManager.signinRedirectCallback();
  return (user.state as string) || '/';
}

export async function logout(): Promise<void> {
  const userManager = await getUserManager();
  await userManager.signoutRedirect();
}

export function onUserChanged(callback: (user: User | null) => void): () => void {
  const loaded = (user: User) => callback(user);
  const unloaded = () => callback(null);
  const expired = () => callback(null);

  let unsubscribe = () => {};
  let cancelled = false;

  getUserManager().then((userManager) => {
    if (cancelled) return;
    userManager.events.addUserLoaded(loaded);
    userManager.events.addUserUnloaded(unloaded);
    userManager.events.addAccessTokenExpired(expired);
    unsubscribe = () => {
      userManager.events.removeUserLoaded(loaded);
      userManager.events.removeUserUnloaded(unloaded);
      userManager.events.removeAccessTokenExpired(expired);
    };
  });

  return () => {
    cancelled = true;
    unsubscribe();
  };
}
