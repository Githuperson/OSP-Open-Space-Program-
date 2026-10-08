import { defineConfig } from 'vite';

export default defineConfig({
  // Bind to all interfaces so the sandbox preview proxy can reach the dev server.
  server: {
    host: '0.0.0.0',
    port: 5173,
    strictPort: true,
    // The preview is served from a proxied *.e2b.app host - allow any Host header.
    allowedHosts: true,
  },
  preview: {
    host: '0.0.0.0',
    port: 5173,
    strictPort: true,
    allowedHosts: true,
  },
  build: {
    target: 'es2022',
  },
});
