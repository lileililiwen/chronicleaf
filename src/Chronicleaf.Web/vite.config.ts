import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The API origin is a loopback address chosen for local development only. It can be
// overridden with CHRONICLEAF_API_ORIGIN; no machine-specific or remote path is committed.
const apiOrigin = process.env.CHRONICLEAF_API_ORIGIN ?? 'http://127.0.0.1:5080';

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    assetsDir: 'assets',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    port: 5183,
    strictPort: true,
    // Development-only proxy to the .NET 10 API so the shell can call /api on one origin.
    proxy: {
      '/api': { target: apiOrigin, changeOrigin: false },
    },
  },
  preview: {
    port: 5184,
    strictPort: true,
  },
});
