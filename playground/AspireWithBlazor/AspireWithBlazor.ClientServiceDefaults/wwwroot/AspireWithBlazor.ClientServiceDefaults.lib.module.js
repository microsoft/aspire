let aspireConfig = null;

export async function onRuntimeConfigLoaded(config) {
    try {
        const response = await fetch('/_blazor/_configuration');
        if (response.ok) {
            const serverConfig = await response.json();
            
            aspireConfig = serverConfig;
            
            const envVars = serverConfig?.webAssembly?.environment;            
            if (envVars && Object.keys(envVars).length > 0) {

                config.environmentVariables ??= {};
                
                for (const [key, value] of Object.entries(envVars)) {
                    const envKey = key.replaceAll(':', '__');
                    config.environmentVariables[envKey] = value;
                }
            }
        }
    } catch (error) {
    }
}