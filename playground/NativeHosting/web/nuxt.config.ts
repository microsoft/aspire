export default defineNuxtConfig({
    compatibilityDate: '2026-06-01',
    devtools: { enabled: false },
    runtimeConfig: {
        redisUri: '',
    },
    nitro: {
        preset: 'node-server',
    },
});
