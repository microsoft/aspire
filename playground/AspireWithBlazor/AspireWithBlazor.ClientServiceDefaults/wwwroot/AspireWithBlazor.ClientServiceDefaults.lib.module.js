// Blazor WebAssembly JavaScript initializer for Aspire service defaults
// This initializer fetches configuration from the host and injects environment variables
// into the .NET WebAssembly runtime via the MonoConfig.environmentVariables property.

let aspireConfig = null;

// onRuntimeConfigLoaded - called when the boot configuration is downloaded
// This is where we can modify the MonoConfig to inject environment variables
// The config parameter is MonoConfig from dotnet.d.ts
// This callback can return a Promise that will be awaited before startup continues
export async function onRuntimeConfigLoaded(config) {
    console.log('[Aspire.JS] onRuntimeConfigLoaded called');
    
    // Fetch configuration from the server's configuration endpoint
    try {
        console.log('[Aspire.JS] Fetching configuration from /_blazor/_configuration...');
        const response = await fetch('/_blazor/_configuration');
        if (response.ok) {
            const serverConfig = await response.json();
            
            // Store configuration in module variable
            aspireConfig = serverConfig;
            
            console.log('[Aspire.JS] Configuration loaded:', JSON.stringify(serverConfig, null, 2));
            
            // Check if we have WebAssembly environment variables to inject
            // Note: Property names match C# PascalCase convention
            const wasmConfig = serverConfig.WebAssembly || serverConfig.webAssembly;
            const envVars = wasmConfig?.Environment || wasmConfig?.environment;
            
            if (envVars && Object.keys(envVars).length > 0) {
                console.log('[Aspire.JS] Found WebAssembly environment variables:', Object.keys(envVars).length);
                
                // Initialize environmentVariables if not present
                if (!config.environmentVariables) {
                    config.environmentVariables = {};
                }
                
                // Add all Aspire environment variables to the MonoConfig
                for (const [key, value] of Object.entries(envVars)) {
                    console.log(`[Aspire.JS] Setting env var: ${key} = ${value}`);
                    config.environmentVariables[key] = value;
                }
                
                console.log('[Aspire.JS] Environment variables injected into MonoConfig:', 
                    Object.keys(config.environmentVariables).length);
            } else {
                console.log('[Aspire.JS] No WebAssembly environment variables in server config');
            }
        } else {
            console.warn('[Aspire.JS] Configuration endpoint returned:', response.status);
        }
    } catch (error) {
        console.warn('[Aspire.JS] Failed to load configuration:', error);
    }
}

export function getAspireConfig() {
    return aspireConfig;
}
