// Matches VS Code's debug-server-ready schema while remaining permissive for future action strings.
export type VSCodeServerReadyAction =
    | {
        action: "openExternally";
        pattern: string;
        uriFormat: string;
        killOnServerStop?: boolean;
    }
    | {
        action: "debugWithChrome" | "debugWithEdge";
        pattern: string;
        uriFormat: string;
        webRoot: string;
        killOnServerStop?: boolean;
    }
    | {
        action: "startDebugging";
        pattern: string;
        name: string;
        killOnServerStop?: boolean;
    }
    | {
        action: "startDebugging";
        pattern: string;
        config: Record<string, unknown>;
        killOnServerStop?: boolean;
    }
    | {
        action: string;
        pattern: string;
        uriFormat?: string;
        webRoot?: string;
        name?: string;
        config?: Record<string, unknown>;
        killOnServerStop?: boolean;
    };

export function determineVSCodeServerReadyAction(
    launchBrowser?: boolean,
    applicationUrl?: string,
    debugConfigurationServerReadyAction?: VSCodeServerReadyAction
): VSCodeServerReadyAction | undefined {
    if (launchBrowser === false) {
        return undefined;
    }

    if (debugConfigurationServerReadyAction) {
        return debugConfigurationServerReadyAction;
    }

    if (launchBrowser === undefined || !applicationUrl) {
        return undefined;
    }

    const uriFormat = applicationUrl.includes(';') ? applicationUrl.split(';')[0] : applicationUrl;

    return {
        action: "openExternally",
        pattern: "\\bNow listening on:\\s+(https?://\\S+)",
        uriFormat: uriFormat
    };
}
