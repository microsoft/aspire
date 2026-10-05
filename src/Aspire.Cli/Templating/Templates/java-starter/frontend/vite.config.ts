import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Proxy API calls to the Spring Boot service. API_HTTP is injected by
      // frontend.withReference(api) in AppHost.java, which names the variable after
      // the resource and its endpoint. Spring Boot serves plain HTTP here, so
      // API_HTTPS is only set if the AppHost adds an HTTPS endpoint to the service.
      '/api': {
        target: process.env.API_HTTPS || process.env.API_HTTP,
        changeOrigin: true
      }
    }
  }
});
