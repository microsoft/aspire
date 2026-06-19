import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

// Vite stamps `crossorigin` on the entry <script>/<link> tags. Under Tauri's custom
// asset protocol (notably macOS WKWebView), a `crossorigin` request is treated as a
// CORS fetch and the protocol response has no `Access-Control-Allow-Origin`, so the
// JS and CSS silently fail to load — producing a blank white window. Strip the
// attribute from the built HTML so assets load over the custom protocol.
function stripCrossorigin(): Plugin {
  return {
    name: "deck-strip-crossorigin",
    enforce: "post",
    transformIndexHtml(html) {
      return html.replace(/\s+crossorigin(?:="[^"]*")?/g, "");
    },
  };
}

// Tauri loads the built UI from the local filesystem, so assets must be referenced
// relatively. `base: "./"` guarantees relative asset URLs in index.html.
export default defineConfig({
  plugins: [react(), stripCrossorigin()],
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
    // Avoid emitting <link rel="modulepreload" crossorigin> preload tags as well.
    modulePreload: { polyfill: false },
  },
});
