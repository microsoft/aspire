// Blazor WebAssembly JavaScript initializer for Aspire service defaults
// This initializer fetches configuration from the host and injects environment variables
// into the .NET WebAssembly runtime via the MonoConfig.environmentVariables property.

let aspireConfig = null;

// onRuntimeConfigLoaded - called when the boot configuration is downloaded
// This is where we can modify the MonoConfig to inject environment variables
// The config parameter is MonoConfig from dotnet.d.ts
// This callback can return a Promise that will be awaited before startup continues
export async function onRuntimeConfigLoaded(config) {
    // Fetch configuration from the server's configuration endpoint
    try {
        const response = await fetch('/_blazor/_configuration');
        if (response.ok) {
            const serverConfig = await response.json();
            
            // Store configuration in module variable
            aspireConfig = serverConfig;
            
            // Check if we have WebAssembly environment variables to inject
            // Note: Property names match C# PascalCase convention
            const wasmConfig = serverConfig.WebAssembly || serverConfig.webAssembly;
            const envVars = wasmConfig?.Environment || wasmConfig?.environment;
            
            if (envVars && Object.keys(envVars).length > 0) {
                // Initialize environmentVariables if not present
                if (!config.environmentVariables) {
                    config.environmentVariables = {};
                }
                
                // Add all Aspire environment variables to the MonoConfig
                // Convert configuration key format (":") to environment variable format ("__")
                for (const [key, value] of Object.entries(envVars)) {
                    const envKey = key.replaceAll(':', '__');
                    config.environmentVariables[envKey] = value;
                }
            }
        }
    } catch (error) {
        // Configuration loading failed - continue without Aspire configuration
    }
}

export function getAspireConfig() {
    return aspireConfig;
}
