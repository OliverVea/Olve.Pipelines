import { createServer, type Server } from 'node:http';
import { instanceFrontendConfig } from '@olve/api';
import { call } from '../api';
import {
  authorizeUrl,
  codeChallenge,
  createCodeVerifier,
  createState,
  discover,
  exchangeCode,
  pollDevice,
  startDevice,
  type Discovery,
  type TokenSet,
} from '../auth/oidc';
import { renderQr } from '../auth/terminal-qr';
import { CliError } from '../errors';
import { saveProfile } from '../profiles';
import type { Command, CommandContext } from '../registry';

// An older server sends no scopes.
const DEFAULT_SCOPES = 'openid profile email';
/** The loopback redirects the instances' OIDC clients allow. */
export const LOOPBACK_PORTS = [48650, 48651, 48652, 48653, 48654];
const BROWSER_TIMEOUT_MS = 5 * 60_000;

export const loginCommand: Command = {
  noun: 'login',
  flags: ['device', 'browser'],
  summary: 'Log in via the browser (OIDC auth-code + PKCE) and cache the token',
  detail: `pl login   Log in and cache the token in ~/.pl

Discovers the OIDC client from the target server's /api/config/frontend and stores the
resulting access/refresh tokens (0600).

By default it runs the browser flow (OIDC auth-code + PKCE) on a loopback redirect. Over
SSH / on a headless box that can't work, so it auto-switches to the device flow (RFC 8628):
scan the printed QR code (or open the URL on your phone) and approve — no local browser or
port-forward needed. Force either flow with --device or --browser.

Use --api-url (or PIPELINES_API_URL) to log in to a specific instance, and --profile to keep
several logins side by side (the profile is 'default' unless given), e.g.
  pl login --api-url https://pipelines-private.ovea.pro
  pl login --profile beta --api-url https://pipelines-beta.ovea.pro
Then \`pl pipeline list --profile beta\`; \`pl profile list\` shows them.`,
  async run(ctx) {
    const frontend = await call(instanceFrontendConfig({ client: ctx.api }));
    if (!frontend.oidcAuthority || !frontend.oidcClientId)
      throw new CliError(
        'This server exposes no frontend OIDC config (Auth:FrontendAuthority/Audience unset) — cannot log in. Use --token instead.',
      );
    const scope = `${frontend.oidcScopes || DEFAULT_SCOPES} offline_access`;
    const discovery = await discover(ctx.fetch, frontend.oidcAuthority);

    const hasDevice = !!discovery.deviceAuthorizationEndpoint;
    if (ctx.args.flag('device') && !hasDevice)
      throw new CliError(
        "This server's OIDC provider does not advertise a device-authorization endpoint.",
      );
    const useDevice =
      hasDevice && !ctx.args.flag('browser') && (ctx.args.flag('device') || ctx.headless);
    const tokens = useDevice
      ? await deviceFlow(ctx, discovery, frontend.oidcClientId, scope)
      : await browserFlow(ctx, discovery, frontend.oidcClientId, scope);

    ctx.profile.apiUrl = ctx.apiUrl;
    ctx.profile.auth = {
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken ?? null,
      tokenEndpoint: discovery.tokenEndpoint,
      clientId: frontend.oidcClientId,
      expiresAt: tokens.expiresAt ?? null,
    };
    saveProfile(ctx.configPath, ctx.profile);

    const result = {
      apiUrl: ctx.apiUrl,
      authority: frontend.oidcAuthority,
      expiresAt: tokens.expiresAt ?? null,
      hasRefreshToken: !!tokens.refreshToken,
    };
    ctx.out.emit(result, () => {
      ctx.out.line(`Logged in to ${ctx.apiUrl}.`);
      ctx.out.line(`Token saved to ${ctx.configPath} (profile '${ctx.profile.name}').`);
      if (result.expiresAt) ctx.out.line(`Access token expires ${result.expiresAt}.`);
      if (!result.hasRefreshToken)
        ctx.out.line(
          'Warning: no refresh token issued (offline_access not granted) — re-run `pl login` when it expires.',
        );
    });
  },
};

/** Authorization code + PKCE with the redirect to a loopback listener on one of the allowed ports. */
async function browserFlow(
  ctx: CommandContext,
  discovery: Discovery,
  clientId: string,
  scope: string,
): Promise<TokenSet> {
  const verifier = createCodeVerifier();
  const state = createState();

  let resolveCode!: (code: string) => void;
  let rejectCode!: (error: Error) => void;
  const code = new Promise<string>((resolve, reject) => {
    resolveCode = resolve;
    rejectCode = reject;
  });

  const server = createServer((request, response) => {
    const url = new URL(request.url ?? '/', 'http://127.0.0.1');
    const page = (status: number, message: string) => {
      response.writeHead(status, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end(
        `<!doctype html><meta charset="utf-8"><title>pl login</title>` +
          `<body style="font-family:system-ui;max-width:32rem;margin:4rem auto;text-align:center"><h2>${message}</h2>`,
      );
    };
    if (url.pathname !== '/callback') return page(404, 'Not found.');
    const error = url.searchParams.get('error');
    if (error) {
      page(200, 'Login failed. You can close this tab and return to the terminal.');
      return rejectCode(
        new CliError(
          `Authorization failed: ${error} — ${url.searchParams.get('error_description') ?? error}`,
        ),
      );
    }
    if (url.searchParams.get('state') !== state) {
      page(400, 'State mismatch. You can close this tab.');
      return rejectCode(
        new CliError(
          'OAuth state mismatch — aborting (possible CSRF or a stale redirect).',
        ),
      );
    }
    const received = url.searchParams.get('code');
    if (!received) {
      page(400, 'Missing authorization code. You can close this tab.');
      return rejectCode(new CliError('Authorization redirect carried no code.'));
    }
    page(200, 'Login successful. You can close this tab and return to the terminal.');
    resolveCode(received);
  });

  const port = await listen(server);
  const timer = setTimeout(
    () =>
      rejectCode(
        new CliError(
          `No browser redirect within ${BROWSER_TIMEOUT_MS / 60_000} minutes. Run \`pl login --device\` to log in without a local browser.`,
        ),
      ),
    BROWSER_TIMEOUT_MS,
  );
  const onAbort = () =>
    rejectCode(new CliError('Login cancelled before the browser redirect arrived.'));
  ctx.signal.addEventListener('abort', onAbort, { once: true });
  try {
    const redirectUri = `http://127.0.0.1:${port}/callback`;
    const url = authorizeUrl(
      discovery,
      clientId,
      scope,
      redirectUri,
      codeChallenge(verifier),
      state,
    );
    ctx.stderr("Opening your browser to log in. If it doesn't open, visit:");
    ctx.stderr('');
    ctx.stderr(`  ${url}`);
    ctx.stderr('');
    ctx.openBrowser(url);
    ctx.stderr('Waiting for the authentication redirect…');
    return await exchangeCode(
      ctx.fetch,
      discovery.tokenEndpoint,
      clientId,
      await code,
      redirectUri,
      verifier,
      ctx.now,
    );
  } finally {
    clearTimeout(timer);
    ctx.signal.removeEventListener('abort', onAbort);
    server.close();
  }
}

async function listen(server: Server): Promise<number> {
  for (const port of LOOPBACK_PORTS) {
    const ok = await new Promise<boolean>((resolve) => {
      const onError = () => resolve(false);
      server.once('error', onError);
      server.listen(port, '127.0.0.1', () => {
        server.removeListener('error', onError);
        resolve(true);
      });
    });
    if (ok) return port;
  }
  throw new CliError(
    `Could not start a loopback listener on any of the ports ${LOOPBACK_PORTS.join(', ')}; use --device.`,
  );
}

/** RFC 8628, for SSH and headless machines: open a URL anywhere, approve, done. */
async function deviceFlow(
  ctx: CommandContext,
  discovery: Discovery,
  clientId: string,
  scope: string,
): Promise<TokenSet> {
  const device = await startDevice(
    ctx.fetch,
    discovery.deviceAuthorizationEndpoint!,
    clientId,
    scope,
  );
  const link = device.verificationUriComplete ?? device.verificationUri;
  ctx.stderr(
    'To log in, scan this QR code or open the URL below, then approve the request:',
  );
  ctx.stderr('');
  const qr = ctx.interactive ? renderQr(link) : undefined;
  if (qr) ctx.stderr(qr);
  ctx.stderr(`  URL:  ${device.verificationUri}`);
  if (device.verificationUriComplete)
    ctx.stderr(`  Link: ${device.verificationUriComplete}`);
  ctx.stderr(`  Code: ${device.userCode}`);
  ctx.stderr('');
  ctx.stderr('Waiting for approval…');
  return pollDevice(ctx.fetch, discovery.tokenEndpoint, clientId, device, {
    now: ctx.now,
    sleep: ctx.sleep,
    signal: ctx.signal,
  });
}
