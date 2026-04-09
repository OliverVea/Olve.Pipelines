import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';

const authority =
  import.meta.env.VITE_OIDC_AUTHORITY ??
  'https://auth.ovea.pro/application/o/olve-pipelines-frontend/';
const clientId =
  import.meta.env.VITE_OIDC_CLIENT_ID ?? 'olve-pipelines-frontend';

const userManager = new UserManager({
  authority,
  client_id: clientId,
  redirect_uri: `${window.location.origin}/callback`,
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',
  scope: 'openid profile email',
  automaticSilentRenew: true,
  userStore: new WebStorageStateStore({ store: sessionStorage }),
});

export type AuthUser = User;

export async function getUser(): Promise<User | null> {
  return userManager.getUser();
}

export async function getAccessToken(): Promise<string | null> {
  const user = await userManager.getUser();
  if (!user || user.expired) return null;
  return user.access_token;
}

export async function login(): Promise<void> {
  await userManager.signinRedirect({ state: window.location.pathname });
}

export async function handleCallback(): Promise<string> {
  const user = await userManager.signinRedirectCallback();
  return (user.state as string) || '/';
}

export async function logout(): Promise<void> {
  await userManager.signoutRedirect();
}

export function onUserChanged(callback: (user: User | null) => void): () => void {
  const loaded = (user: User) => callback(user);
  const unloaded = () => callback(null);
  const expired = () => callback(null);

  userManager.events.addUserLoaded(loaded);
  userManager.events.addUserUnloaded(unloaded);
  userManager.events.addAccessTokenExpired(expired);

  return () => {
    userManager.events.removeUserLoaded(loaded);
    userManager.events.removeUserUnloaded(unloaded);
    userManager.events.removeAccessTokenExpired(expired);
  };
}
