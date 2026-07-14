import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

const dashboardUrl = process.env.ASPIRE_DASHBOARD_URL;
const aotDashboardUrl = process.env.ASPIRE_DASHBOARD_AOT_URL;

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
    proxy: dashboardUrl
      ? {
          // The versioned AOT backend is independently selectable. All capabilities
          // it has not advertised remain on the existing dashboard proxy below.
          "/api/dashboard": {
            target: aotDashboardUrl ?? dashboardUrl,
            changeOrigin: true,
            secure: false,
            ws: true,
            rewriteWsOrigin: true,
          },
          "/api/deck": {
            target: dashboardUrl,
            changeOrigin: true,
            secure: false,
          },
          "/api/terminal": {
            target: dashboardUrl,
            changeOrigin: true,
            secure: false,
            ws: true,
            rewriteWsOrigin: true,
          },
          "/Components/Controls/TerminalView.razor.js": {
            target: dashboardUrl,
            changeOrigin: true,
            secure: false,
          },
          "/js": {
            target: dashboardUrl,
            changeOrigin: true,
            secure: false,
          },
          // Live dashboard runs commonly use browser-token auth. Proxying the
          // login handshake lets the test browser receive a cookie for the Vite
          // origin without reading or copying authentication cookie material.
          "/login": {
            target: dashboardUrl,
            changeOrigin: true,
            secure: false,
          },
          "/api/set-language": {
            target: dashboardUrl,
            changeOrigin: true,
            secure: false,
          },
        }
      : undefined,
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022",
    sourcemap: false,
    // Avoid emitting <link rel="modulepreload" crossorigin> preload tags as well.
    modulePreload: { polyfill: false },
    rollupOptions: {
      output: {
        // The registry is intentionally static so AppHost-provided icon names work without
        // runtime code loading. Keep those glyphs in their own cacheable chunk.
        manualChunks(id) {
          return id.includes("/@fluentui/react-icons/") ? "fluent-icons" : undefined;
        },
      },
    },
  },
});
