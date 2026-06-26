import * as path from 'path';
import { EnvVar } from "../dcp/types";

export const aspireCliPathEnvironmentVariableName = 'AspireCliPath';

const filteredEnvironmentKeyCounts = new Map<string, number>();

export function mergeEnvs(base: NodeJS.ProcessEnv, envVars?: EnvVar[]): Record<string, string | undefined> {
    const merged = filterBaseEnvironment(base);
    if (envVars) {
        for (const e of envVars) {
            merged[e.name] = e.value;
        }
    }
    return merged;
}

export function getEnvironmentWithoutE2EBridgeVariables(): NodeJS.ProcessEnv {
    return filterBaseEnvironment(process.env);
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
    const filteredEnv = withoutAspireCliPathForMSBuild(env);

    if (!aspireCliPath) {
        return filteredEnv;
    }

    return [
        ...filteredEnv,
        { name: aspireCliPathEnvironmentVariableName, value: aspireCliPath },
    ];
}

export function withoutAspireCliPathForMSBuild(env: EnvVar[]): EnvVar[] {
    const aspireCliPathKey = aspireCliPathEnvironmentVariableName.toLowerCase();
    return env.filter(variable => variable.name.toLowerCase() !== aspireCliPathKey);
}

export function isBareAspireCommand(value: string): boolean {
    if (value.includes('/') || value.includes('\\')) {
        return false;
    }

    return /^(?:aspire|aspire\.exe|aspire\.cmd|aspire\.bat)$/i.test(value);
}

function filterBaseEnvironment(env: NodeJS.ProcessEnv): Record<string, string | undefined> {
    const aspireCliPathKey = aspireCliPathEnvironmentVariableName.toLowerCase();

    return Object.fromEntries(
        Object.entries(env).filter(([key]) =>
            !key.startsWith('ASPIRE_EXTENSION_E2E_') &&
            !filteredEnvironmentKeyCounts.has(key) &&
            key.toLowerCase() !== aspireCliPathKey)
    );
}

export function addFilteredEnvironmentKeys(keys: string[]): void {
    for (const key of keys) {
        filteredEnvironmentKeyCounts.set(key, (filteredEnvironmentKeyCounts.get(key) ?? 0) + 1);
    }
}

export function removeFilteredEnvironmentKeys(keys: string[]): void {
    for (const key of keys) {
        const count = filteredEnvironmentKeyCounts.get(key);
        if (count === undefined) {
            continue;
        }

        if (count <= 1) {
            filteredEnvironmentKeyCounts.delete(key);
        } else {
            filteredEnvironmentKeyCounts.set(key, count - 1);
        }
    }
}

export const enum EnvironmentVariables {
    ASPIRE_CLI_STOP_ON_ENTRY = "ASPIRE_CLI_STOP_ON_ENTRY",
    ASPIRE_APPHOST_STOP_ON_ENTRY = "ASPIRE_APPHOST_STOP_ON_ENTRY",
    ASPIRE_CLI_START_TIMEOUT = "ASPIRE_CLI_START_TIMEOUT"
}
