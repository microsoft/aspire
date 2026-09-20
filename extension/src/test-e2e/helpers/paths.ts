import * as fs from 'fs';
import * as path from 'path';

export function getExtensionRoot(): string {
    return process.env.ASPIRE_EXTENSION_E2E_EXTENSION_ROOT ?? path.resolve(__dirname, '../../..');
}

export function getRepoRoot(): string {
    return process.env.ASPIRE_EXTENSION_E2E_REPO_ROOT ?? path.resolve(getExtensionRoot(), '..');
}

export function getDiagnosticsDir(): string {
    return process.env.ASPIRE_EXTENSION_E2E_RESULTS_DIR ?? path.join(getExtensionRoot(), '.test-results', 'e2e');
}

export function getWorkspaceRoot(): string {
    return getRequiredPathFromEnvironment('ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT');
}

export function getRunRoot(): string | undefined {
    return process.env.ASPIRE_EXTENSION_E2E_RUN_ROOT ? path.resolve(process.env.ASPIRE_EXTENSION_E2E_RUN_ROOT) : undefined;
}

export function getPrimaryAppHostProjectPath(): string {
    return getRequiredPathFromEnvironment('ASPIRE_EXTENSION_E2E_PRIMARY_APPHOST');
}

export function getCliPath(): string {
    return getRequiredPathFromEnvironment('ASPIRE_EXTENSION_E2E_CLI_PATH');
}

export function getVsixPath(): string | undefined {
    return process.env.ASPIRE_EXTENSION_E2E_VSIX ? path.resolve(process.env.ASPIRE_EXTENSION_E2E_VSIX) : undefined;
}

export function getStateFilePath(): string {
    return getRequiredPathFromEnvironment('ASPIRE_EXTENSION_E2E_STATE_FILE');
}

export function getControlFilePath(): string | undefined {
    return process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE ? path.resolve(process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE) : undefined;
}

/**
 * The id the runner assigned to this E2E run. The state and control files live at a stable
 * per-shard path, so this is what distinguishes the extension host launched for this run from one
 * left behind by an earlier run that is still polling the same files.
 */
export function getRunId(): string | undefined {
    return process.env.ASPIRE_EXTENSION_E2E_RUN_ID;
}

export function ensureDiagnosticsDir(): string {
    const diagnosticsDir = getDiagnosticsDir();
    fs.mkdirSync(diagnosticsDir, { recursive: true });
    return diagnosticsDir;
}

function getRequiredPathFromEnvironment(name: string): string {
    const value = process.env[name];
    if (!value) {
        throw new Error(`${name} is required for Aspire VS Code extension E2E tests.`);
    }

    return path.resolve(value);
}
