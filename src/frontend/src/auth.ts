import { User, UserManager, WebStorageStateStore } from 'oidc-client-ts';
import { instanceFrontendConfig, type FrontendConfig } from '@olve/api';
import { createClient, createConfig } from '@olve/api/client';

/**
 * Dev-only auth bypass (`VITE_DEV_AUTH=bypass`, ignored in production builds):
 * no OIDC redirect, a placeholder user, and `VITE_DEV_TOKEN` (if set) as the
 * bearer token. Enough against v2 beta, whose reads are anonymous (its OIDC client has
 * no localhost redirect URI); mutations need `VITE_DEV_TOKEN`.
 */
export const devAuthBypass: boolean =
  import.meta.env.DEV && import.meta.env.VITE_DEV_AUTH === 'bypass';

const devUser: User | null = devAuthBypass
  ? new User({
      access_token: import.meta.env.VITE_DEV_TOKEN ?? '',
      token_type: 'Bearer',
      profile: {
        sub: 'dev',
        iss: 'dev',
        aud: 'dev',
        exp: 0,
        iat: 0,
        preferred_username: import.meta.env.VITE_DEV_TOKEN
          ? 'dev (token)'
          : 'dev (anonymous)',
      },
      expires_at: Math.floor(Date.now() / 1000) + 365 * 24 * 3600,
    })
  : null;

// Anonymous: signing in needs these settings first.
const anonymous = createClient(createConfig({ baseUrl: '', throwOnError: true }));
let frontendConfigPromise: Promise<FrontendConfig> | null = null;

/** The instance's settings for the web UI, fetched once. */
export function frontendConfig(): Promise<FrontendConfig> {
  const config = (frontendConfigPromise ??= instanceFrontendConfig({
    client: anonymous,
    throwOnError: true,
  }).then(
    (r) => r.data,
    (e: unknown) => {
      frontendConfigPromise = null;
      throw e;
    },
  ));
  return config;
}

let userManagerPromise: Promise<UserManager> | null = null;

async function fetchOidcConfig(): Promise<{
  authority: string;
  clientId: string;
  scopes: string;
}> {
  const config = await frontendConfig();
  if (!config.oidcAuthority || !config.oidcClientId) {
    throw new Error('Frontend config missing oidcAuthority or oidcClientId');
  }
  return {
    authority: config.oidcAuthority,
    clientId: config.oidcClientId,
    scopes: config.oidcScopes || 'openid profile email',
  };
}

function getUserManager(): Promise<UserManager> {
  if (!userManagerPromise) {
    userManagerPromise = fetchOidcConfig().then(
      (config) =>
        new UserManager({
          authority: config.authority,
          client_id: config.clientId,
          redirect_uri: `${window.location.origin}/callback`,
          post_logout_redirect_uri: window.location.origin,
          response_type: 'code',
          scope: config.scopes,
          automaticSilentRenew: true,
          userStore: new WebStorageStateStore({ store: sessionStorage }),
        }),
    );
  }
  return userManagerPromise;
}

export type AuthUser = User;

export async function getUser(): Promise<User | null> {
  if (devUser) return devUser;
  const userManager = await getUserManager();
  return userManager.getUser();
}

export async function getAccessToken(): Promise<string | null> {
  if (devAuthBypass) return import.meta.env.VITE_DEV_TOKEN || null;
  const userManager = await getUserManager();
  const user = await userManager.getUser();
  if (!user || user.expired) return null;
  return user.access_token;
}

export async function login(): Promise<void> {
  if (devAuthBypass) return;
  const userManager = await getUserManager();
  await userManager.signinRedirect({ state: window.location.pathname });
}

export async function handleCallback(): Promise<string> {
  const userManager = await getUserManager();
  const user = await userManager.signinRedirectCallback();
  return (user.state as string) || '/';
}

export async function logout(): Promise<void> {
  if (devAuthBypass) return;
  const userManager = await getUserManager();
  await userManager.signoutRedirect();
}

export function onUserChanged(callback: (user: User | null) => void): () => void {
  const loaded = (user: User) => callback(user);
  const unloaded = () => callback(null);
  const expired = () => callback(null);

  let unsubscribe = () => {};
  let cancelled = false;
  if (devAuthBypass) return () => {};

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
