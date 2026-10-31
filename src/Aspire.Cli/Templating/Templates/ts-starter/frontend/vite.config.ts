import { defineConfig } from 'vite-plus';
import react from '@vitejs/plugin-react';

// https://viteplus.dev/guide/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Proxy API calls to the Express service
      '/api': {
        target: process.env.APP_HTTPS || process.env.APP_HTTP,
        changeOrigin: true
      }
    }
  }
});
