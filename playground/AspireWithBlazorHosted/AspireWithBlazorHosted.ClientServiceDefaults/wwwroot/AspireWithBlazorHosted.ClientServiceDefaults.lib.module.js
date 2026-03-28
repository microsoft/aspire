let aspireConfig = null;

// Blazor Web App (hosted model): called by blazor.web.js before the WASM runtime starts.
// Reads the client configuration from an HTML comment embedded by the server during prerendering,
// then injects the values as environment variables via configureRuntime.
export async function beforeWebAssemblyStart(options) {
    const config = readConfigFromDomComment();
    if (config) {
        aspireConfig = config;
        const envVars = config?.webAssembly?.environment;
        if (envVars && Object.keys(envVars).length > 0) {
            const prevConfigure = options.configureRuntime;
            options.configureRuntime = (builder) => {
                if (prevConfigure) {
                    prevConfigure(builder);
                }
                for (const [key, value] of Object.entries(envVars)) {
                    builder.withEnvironmentVariable(key, value);
                }
            };
        }
    }
}

// Standalone WASM: called by dotnet.js when the mono runtime config is loaded.
// Fetches client configuration from the gateway's /_blazor/_configuration endpoint
// and injects the values as environment variables on the MonoConfig.
export async function onRuntimeConfigLoaded(config) {
    // If config was already loaded from DOM comment (hosted model), skip the fetch.
    if (aspireConfig) {
        return;
    }

    try {
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

function readConfigFromDomComment() {
    return walkNodesForConfig(document);
}

function walkNodesForConfig(node) {
    if (node.nodeType === Node.COMMENT_NODE) {
        const content = node.textContent?.trim();
        const prefix = 'Blazor-Client-Config:';
        if (content?.startsWith(prefix)) {
            const encoded = content.substring(prefix.length);
            try {
                return JSON.parse(atob(encoded));
            } catch {
                return null;
            }
        }
    }

    if (node.childNodes) {
        for (const child of node.childNodes) {
            const result = walkNodesForConfig(child);
            if (result) return result;
        }
    }

    return null;
}
