export default defineNuxtConfig({
    compatibilityDate: '2026-06-01',
    devtools: { enabled: false },
    runtimeConfig: {
        apiBase: '',
    },
    nitro: {
        preset: 'node-server',
    },
});
