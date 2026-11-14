import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");

  return {
    plugins: [react()],
    server: {
      port: Number.parseInt(env.VITE_PORT, 10),
      proxy: {
        "/api": {
          target: process.env.WEATHERAPI_HTTPS ?? process.env.WEATHERAPI_HTTP,
          changeOrigin: true,
          secure: false,
        },
      },
    },
  };
});