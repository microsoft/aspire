import * as path from 'path';
import { EnvVar } from "../dcp/types";

export const aspireCliPathEnvironmentVariableName = 'AspireCliPath';

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
    const aspireCliPathKey = aspireCliPathEnvironmentVariableName.toLowerCase();
    const filteredEnv = env.filter(variable => variable.name.toLowerCase() !== aspireCliPathKey);

    if (!aspireCliPath) {
        return filteredEnv;
    }

    return [
        ...filteredEnv,
        { name: aspireCliPathEnvironmentVariableName, value: aspireCliPath },
    ];
}

function isBareAspireCommand(value: string): boolean {
    if (value.includes('/') || value.includes('\\')) {
        return false;
    }

    return /^(?:aspire|aspire\.exe|aspire\.cmd|aspire\.bat)$/i.test(value);
}

function filterBaseEnvironment(env: NodeJS.ProcessEnv): Record<string, string | undefined> {
    const aspireCliPathKey = aspireCliPathEnvironmentVariableName.toLowerCase();

    return Object.fromEntries(
        Object.entries(env).filter(([key]) => !key.startsWith('ASPIRE_EXTENSION_E2E_') && key.toLowerCase() !== aspireCliPathKey)
    );
}

export const enum EnvironmentVariables {
    ASPIRE_CLI_STOP_ON_ENTRY = "ASPIRE_CLI_STOP_ON_ENTRY",
    ASPIRE_APPHOST_STOP_ON_ENTRY = "ASPIRE_APPHOST_STOP_ON_ENTRY"
}
