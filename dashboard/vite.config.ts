import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Production: `npm run build` writes dist/, which is uploaded to Cloudflare
// Pages (sivacom.sivayaantechnologies.com) and calls the API at VITE_API_URL
// (.env.production). Development: the dev server proxies /api to the local
// API, so everything is same-origin and no CORS is involved.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5291,
    proxy: { '/api': 'http://localhost:5290', '/health': 'http://localhost:5290' },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
});
