import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // API_HTTP is injected by frontend.WithReference(api) in apphost.go.
      '/api': {
        target: process.env.API_HTTPS || process.env.API_HTTP,
        changeOrigin: true
      }
    }
  }
});
