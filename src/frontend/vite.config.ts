import { fileURLToPath } from 'node:url';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = env.VITE_API_TARGET ?? 'http://localhost:5000';

  return {
    // `@olve/api` is the Hey API client generated from src/spec into the repo-root artifacts/
    // (never committed; `cd src/spec && bun run build` rebuilds it); `@olve/client` the shared
    // setup over it in src/client. Mirrored by `paths` in tsconfig.json.
    resolve: {
      alias: {
        '@olve/api': fileURLToPath(new URL('../../artifacts/clients/ts', import.meta.url)),
        '@olve/client': fileURLToPath(new URL('../client/src/index.ts', import.meta.url)),
      },
    },
    build: {
      rollupOptions: {
        input: {
          main: fileURLToPath(new URL('index.html', import.meta.url)),
          'api-reference': fileURLToPath(new URL('api-reference.html', import.meta.url)),
        },
      },
    },
    server: {
      host: '0.0.0.0',
      allowedHosts: ['bulwark-m2'],
      proxy: {
        '/docs/openapi.json': {
          target: apiTarget,
          changeOrigin: true,
          secure: false,
        },
        '/api/': {
          target: apiTarget,
          changeOrigin: true,
          secure: false,
        },
      },
    },
  };
});
