import * as path from 'path';
import { EnvVar } from "../dcp/types";

export const aspireCliPathEnvironmentVariableName = 'AspireCliPath';

export function mergeEnvs(base: NodeJS.ProcessEnv, envVars?: EnvVar[]): Record<string, string | undefined> {
    const merged: Record<string, string | undefined> = { ...base };
    if (envVars) {
        for (const e of envVars) {
            merged[e.name] = e.value;
        }
    }
    return merged;
}

export function getEnvironmentWithoutE2EBridgeVariables(): NodeJS.ProcessEnv {
    return Object.fromEntries(
        Object.entries(process.env).filter(([key]) => !key.startsWith('ASPIRE_EXTENSION_E2E_'))
    );
}

export function getAspireCliPathForMSBuild(cliPath: string | undefined, workingDirectory?: string): string | undefined {
    const trimmedPath = cliPath?.trim();
    if (!trimmedPath || isBareAspireCommand(trimmedPath)) {
        return undefined;
    }

    return path.isAbsolute(trimmedPath)
        ? trimmedPath
        : path.resolve(workingDirectory ?? process.cwd(), trimmedPath);
}

export function withAspireCliPathForMSBuild(env: EnvVar[], cliPath: string | undefined, workingDirectory?: string): EnvVar[] {
    const aspireCliPath = getAspireCliPathForMSBuild(cliPath, workingDirectory);
    if (!aspireCliPath) {
        return env;
    }

    const aspireCliPathKey = aspireCliPathEnvironmentVariableName.toLowerCase();
    return [
        ...env.filter(variable => variable.name.toLowerCase() !== aspireCliPathKey),
        { name: aspireCliPathEnvironmentVariableName, value: aspireCliPath },
    ];
}

function isBareAspireCommand(value: string): boolean {
    if (value.includes('/') || value.includes('\\')) {
        return false;
    }

    return /^(?:aspire|aspire\.exe|aspire\.cmd|aspire\.bat)$/i.test(value);
}

export const enum EnvironmentVariables {
    ASPIRE_CLI_STOP_ON_ENTRY = "ASPIRE_CLI_STOP_ON_ENTRY",
    ASPIRE_APPHOST_STOP_ON_ENTRY = "ASPIRE_APPHOST_STOP_ON_ENTRY"
}
