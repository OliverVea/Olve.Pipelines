import { defineConfig } from 'vite'

export default defineConfig({
  server: {
    host: '0.0.0.0',
    allowedHosts: ['bulwark-m2'],
    proxy: {
      '/api': 'http://localhost:5000',
    },
  },
})
