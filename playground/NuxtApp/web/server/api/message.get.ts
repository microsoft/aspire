export default defineEventHandler(async (event) => {
    const config = useRuntimeConfig(event);
    if (!config.apiBase) {
        throw createError({ statusCode: 503, statusMessage: 'Nuxt runtimeConfig.apiBase is not configured.' });
    }

    return await $fetch<{ message: string }>('/message', {
        baseURL: config.apiBase,
        timeout: 5000,
    });
});
