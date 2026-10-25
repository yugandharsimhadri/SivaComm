import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The dashboard is served by the API itself (same origin), so the admin
// cookie needs no CORS and no cross-site allowances. In development Vite
// proxies /api to the API; `npm run build` writes straight into its wwwroot.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5291,
    proxy: { '/api': 'http://localhost:5290', '/health': 'http://localhost:5290' },
  },
  build: {
    outDir: '../backend/src/SivaComm.Api/wwwroot',
    emptyOutDir: true,
  },
});
