import { defineConfig } from 'vite-plus';
import react from '@vitejs/plugin-react';

// https://viteplus.dev/guide/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Proxy API calls to the app service
      '/api': {
        target: process.env.SERVER_HTTPS || process.env.SERVER_HTTP,
        changeOrigin: true
      }
    }
  }
});
