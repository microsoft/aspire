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

const defaultServerReadyActionPattern = "\\bNow listening on:\\s+(https?://\\S+)";

function escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

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

    const applicationUrls = applicationUrl
        .split(';')
        .map(url => url.trim())
        .filter(url => url.length > 0);

    if (applicationUrls.length === 0) {
        return undefined;
    }

    if (applicationUrls.length === 1) {
        const uriFormat = applicationUrls[0];

        return {
            action: "openExternally",
            pattern: `\\bNow listening on:\\s+${escapeRegExp(uriFormat)}`,
            uriFormat: uriFormat
        };
    }

    return {
        action: "openExternally",
        pattern: defaultServerReadyActionPattern,
        uriFormat: "%s"
    };
}
