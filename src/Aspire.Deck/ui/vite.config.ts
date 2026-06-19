import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Tauri loads the built UI from the local filesystem (file://), so assets must be
// referenced relatively. `base: "./"` guarantees relative asset URLs in index.html.
export default defineConfig({
  plugins: [react()],
  base: "./",
  clearScreen: false,
  server: {
    port: 1430,
    strictPort: true,
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022",
    sourcemap: false,
  },
});
