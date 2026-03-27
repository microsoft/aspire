import * as vscode from 'vscode';
import { stripComments } from 'jsonc-parser';

/**
 * Reads a JSON file from disk, strips comments, and parses it.
 * Handles both standard JSON and JSONC (JSON with comments) formats.
 */
export async function readJsonFile(uri: vscode.Uri): Promise<any> {
    const buffer = await vscode.workspace.fs.readFile(uri);
    const raw = buffer.toString();
    return JSON.parse(stripComments(raw));
}

/**
 * Represents the new aspire.config.json format.
 */
export interface AspireConfigFile {
    appHost?: {
        path?: string;
        language?: string;
    };
    sdk?: {
        version?: string;
    };
    channel?: string;
    features?: { [key: string]: boolean };
    profiles?: { [key: string]: AspireConfigProfile };
    packages?: { [key: string]: string };
}

export interface AspireConfigProfile {
    applicationUrl?: string;
    environmentVariables?: { [key: string]: string };
}

/**
 * Represents the legacy .aspire/settings.json format.
 */
export interface AspireSettingsFile {
    appHostPath?: string;
    [key: string]: any;
}

/**
 * The well-known filename for the new unified config format.
 */
export const aspireConfigFileName = 'aspire.config.json';

/**
 * Extracts the AppHost path from either the new aspire.config.json format or the legacy .aspire/settings.json format.
 * @param json The parsed JSON object from a config file.
 * @returns The AppHost path string, or null if not found.
 */
export function getAppHostPathFromConfig(json: any): string | null {
    // New format: appHost.path
    if (json?.appHost?.path && typeof json.appHost.path === 'string') {
        return json.appHost.path;
    }
    // Legacy format: appHostPath
    if (json?.appHostPath && typeof json.appHostPath === 'string') {
        return json.appHostPath;
    }
    return null;
}
