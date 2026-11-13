import { defineConfig } from "vite";

export default defineConfig(({ command }) => {
    const functionsBaseUrl = process.env.FUNCTIONS_BASE_URL;

    return {
        server: {
            proxy: command === "serve" && functionsBaseUrl ? {
                "/api": {
                    // Aspire injects this endpoint for local development; publish uses
                    // publishAsStaticWebsite({ apiPath: "/api", apiTarget: functions }).
                    target: functionsBaseUrl,
                    changeOrigin: true,
                    secure: false
                }
            } : undefined
        }
    };
});
