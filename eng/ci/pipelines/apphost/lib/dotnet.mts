import type { PipelineStepContext } from '../.aspire/modules/aspire.mjs';
import { exec } from './process.mjs';
import type { RepoRoot } from './repo.mjs';

export interface DotnetOptions {
    title: string;
    env?: NodeJS.ProcessEnv;
    workingDirectory?: string;
    throwOnFailure?: boolean;
}

export async function dotnet(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    verb: string,
    args: string[],
    options: DotnetOptions): Promise<number | null> {
    return await exec(context, repoRoot, {
        title: options.title,
        command: 'dotnet',
        args: [verb, ...args],
        env: options.env,
        workingDirectory: options.workingDirectory,
        throwOnFailure: options.throwOnFailure
    });
}
