import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { PipelineStepContext } from '../.aspire/modules/aspire.mjs';
import { exec } from './process.mjs';

export interface RepoRoot {
    path(...segments: string[]): string;
}

export function findRepoRoot(fromImportMetaUrl: string): RepoRoot {
    const startDirectory = dirname(fileURLToPath(fromImportMetaUrl));
    const result = spawnSync('git', ['rev-parse', '--show-toplevel'], {
        cwd: startDirectory,
        encoding: 'utf8'
    });

    if (result.error !== undefined) {
        throw new Error(`Could not find repository root from ${fromImportMetaUrl}: ${result.error.message}`);
    }

    if (result.status !== 0) {
        const stderr = result.stderr.trim();
        const detail = stderr.length > 0 ? `: ${stderr}` : '';
        throw new Error(`Could not find repository root from ${fromImportMetaUrl}${detail}`);
    }

    const root = result.stdout.trim();
    if (root.length === 0) {
        throw new Error(`Could not find repository root from ${fromImportMetaUrl}: git did not return a path`);
    }

    return {
        path: (...segments) => resolve(root, ...segments)
    };
}

export async function restoreRepository(context: PipelineStepContext, repoRoot: RepoRoot): Promise<void> {
    await exec(context, repoRoot, {
        title: 'Restoring repository tools and SDK',
        command: './restore.sh'
    });
}
