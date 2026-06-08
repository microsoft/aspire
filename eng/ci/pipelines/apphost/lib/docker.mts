import { mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';
import type { PipelineStepContext } from '../.aspire/modules/aspire.mjs';
import { githubActions } from './github.mjs';
import { exec } from './process.mjs';
import type { RepoRoot } from './repo.mjs';

export const defaultUbuntuAptMirror = 'http://azure.archive.ubuntu.com/ubuntu/';

export interface DockerOptions {
    title: string;
    env?: NodeJS.ProcessEnv;
    workingDirectory?: string;
    throwOnFailure?: boolean;
}

export interface DockerImageBuildOptions {
    displayName: string;
    dockerfile: string;
    tag: string;
    buildArgs?: Record<string, string>;
    cacheScope?: string;
    env?: NodeJS.ProcessEnv;
    useBuildx?: boolean;
    throwOnFailure?: boolean;
    title?: string;
}

export interface DockerImageBuildAttempt {
    name: string;
    title?: string;
    buildArgs?: Record<string, string>;
    env?: NodeJS.ProcessEnv;
}

export async function verifyDocker(context: PipelineStepContext, repoRoot: RepoRoot): Promise<void> {
    await docker(context, repoRoot, 'info', [], {
        title: 'Verifying Docker is running'
    });
}

export async function docker(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    verb: string,
    args: string[],
    options: DockerOptions): Promise<number | null> {
    return await exec(context, repoRoot, {
        title: options.title,
        command: 'docker',
        args: [verb, ...args],
        env: options.env,
        workingDirectory: options.workingDirectory,
        throwOnFailure: options.throwOnFailure
    });
}

export async function ensureDockerBuildxBuilder(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    builderName: string): Promise<void> {
    const exitCode = await docker(context, repoRoot, 'buildx', ['inspect', builderName], {
        title: `Checking Docker buildx builder ${builderName}`,
        throwOnFailure: false
    });

    if (exitCode === 0) {
        await docker(context, repoRoot, 'buildx', ['use', builderName], {
            title: `Using Docker buildx builder ${builderName}`
        });
    }
    else {
        await docker(context, repoRoot, 'buildx', ['create', '--name', builderName, '--use'], {
            title: `Creating Docker buildx builder ${builderName}`
        });
    }

    await docker(context, repoRoot, 'buildx', ['inspect', builderName, '--bootstrap'], {
        title: `Bootstrapping Docker buildx builder ${builderName}`
    });
}

export async function buildDockerImageWithAttempts(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    options: DockerImageBuildOptions,
    attempts: DockerImageBuildAttempt[]): Promise<number | null> {
    if (attempts.length === 0) {
        throw new Error(`${options.displayName} must declare at least one Docker build attempt.`);
    }

    const throwOnFailure = options.throwOnFailure ?? true;

    for (let i = 0; i < attempts.length; i++) {
        const attempt = attempts[i];
        const isLastAttempt = i === attempts.length - 1;
        const exitCode = await buildDockerImage(context, repoRoot, {
            ...options,
            title: attempt.title ?? `Building ${options.displayName} (${attempt.name})`,
            buildArgs: {
                ...options.buildArgs,
                ...attempt.buildArgs
            },
            env: {
                ...options.env,
                ...attempt.env
            },
            throwOnFailure: isLastAttempt ? throwOnFailure : false
        });

        if (exitCode === 0 || isLastAttempt) {
            return exitCode;
        }

        const nextAttempt = attempts[i + 1];
        const reportingStep = await context.reportingStep();
        await reportingStep.logStep(
            'warning',
            `${options.displayName} build attempt '${attempt.name}' failed; retrying with '${nextAttempt.name}'.`);
    }

    throw new Error(`${options.displayName} finished without running a Docker build attempt.`);
}

export async function buildDockerImage(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    options: DockerImageBuildOptions): Promise<number | null> {
    const useBuildx = options.useBuildx ?? true;
    const args = useBuildx
        ? ['build', '--load']
        : [];

    if (useBuildx && options.cacheScope !== undefined) {
        args.push(...githubActions.getBuildxCacheArgs(options.cacheScope));
    }

    for (const [name, value] of Object.entries(options.buildArgs ?? {})) {
        args.push('--build-arg', `${name}=${value}`);
    }

    args.push('-f', options.dockerfile, '-t', options.tag, '.');

    return await docker(context, repoRoot, useBuildx ? 'buildx' : 'build', args, {
        title: options.title ?? `Building ${options.displayName}`,
        env: options.env,
        throwOnFailure: options.throwOnFailure
    });
}

export async function tagDockerImage(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    sourceTag: string,
    targetTag: string): Promise<void> {
    await docker(context, repoRoot, 'tag', [sourceTag, targetTag], {
        title: 'Tagging Docker image',
    });
}

export async function saveDockerImageTarball(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    displayName: string,
    tag: string,
    outputPath: string): Promise<void> {
    await mkdir(dirname(outputPath), { recursive: true });
    await exec(context, repoRoot, {
        title: `Saving ${displayName} tarball`,
        command: 'bash',
        args: ['-c', 'docker save "$1" | gzip > "$2"', 'bash', tag, outputPath]
    });
}
