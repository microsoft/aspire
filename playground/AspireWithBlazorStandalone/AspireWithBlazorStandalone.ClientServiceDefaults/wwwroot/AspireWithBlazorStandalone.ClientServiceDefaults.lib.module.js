let aspireConfig = null;

export async function onRuntimeConfigLoaded(config) {
    try {
        // Resolve relative to <base href> so it works under path prefixes (e.g., /app/_blazor/_configuration)
        const configUrl = new URL('_blazor/_configuration', document.baseURI).href;
        const response = await fetch(configUrl);
        if (response.ok) {
            const serverConfig = await response.json();

            aspireConfig = serverConfig;

            const envVars = serverConfig?.webAssembly?.environment;
            if (envVars && Object.keys(envVars).length > 0) {

                config.environmentVariables ??= {};

                for (const [key, value] of Object.entries(envVars)) {
                    config.environmentVariables[key] = value;
                }
            }
        }
    } catch (error) {
    }
}
