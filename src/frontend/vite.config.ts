import { fileURLToPath } from 'node:url';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = env.VITE_API_TARGET ?? 'http://localhost:5000';

  return {
    // `@olve/api` is the Hey API client generated from src/spec into the repo-root artifacts/
    // (never committed; `cd src/spec && bun run build` rebuilds it). Mirrored by `paths` in tsconfig.json.
    resolve: {
      alias: {
        '@olve/api': fileURLToPath(new URL('../../artifacts/clients/ts', import.meta.url)),
      },
    },
    server: {
      host: '0.0.0.0',
      allowedHosts: ['bulwark-m2'],
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
          secure: false,
        },
      },
    },
  };
});
