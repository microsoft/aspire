import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const proxyTarget = process.env.APP_HTTPS || process.env.APP_HTTP || 'http://localhost:5000';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Proxy API calls to the Express service
      '/api': {
        target: proxyTarget,
        changeOrigin: true
      }
    }
  }
});
