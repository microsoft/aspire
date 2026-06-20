import { EnvVar } from "../dcp/types";

const filteredEnvironmentKeyCounts = new Map<string, number>();

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
        Object.entries(process.env).filter(([key]) => !key.startsWith('ASPIRE_EXTENSION_E2E_') && !filteredEnvironmentKeyCounts.has(key))
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
