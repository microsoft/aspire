import { mkdir, readFile, rm } from 'node:fs/promises';
import { dirname } from 'node:path';
import type { PipelineStepContext } from '../.aspire/modules/aspire.mjs';
import { exec } from '../lib/process.mjs';
import type { RepoRoot } from '../lib/repo.mjs';

type ImageRequirement = boolean | 'auto';

export interface LoadCliE2EImagesOptions {
    imageDirectory: string;
    githubEnvFile: string;
    requireDotnet?: ImageRequirement;
    requirePolyglot?: ImageRequirement;
    requireJava?: ImageRequirement;
}

export async function loadCliE2EImages(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    options: LoadCliE2EImagesOptions): Promise<void> {
    await mkdir(dirname(options.githubEnvFile), { recursive: true });
    await rm(options.githubEnvFile, { force: true });

    const args = [
        '--image-dir',
        options.imageDirectory,
        '--github-env',
        options.githubEnvFile
    ];

    appendImageRequirement(args, '--require-dotnet', options.requireDotnet);
    appendImageRequirement(args, '--require-polyglot', options.requirePolyglot);
    appendImageRequirement(args, '--require-java', options.requireJava);

    await exec(context, repoRoot, {
        title: 'Loading prebuilt CLI E2E Docker image',
        command: './eng/scripts/load-cli-e2e-images.sh',
        args
    });
}

export async function readCliE2EImageEnvironmentFile(path: string): Promise<NodeJS.ProcessEnv> {
    let contents: string;
    try {
        contents = await readFile(path, 'utf8');
    }
    catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        throw new Error(`Could not read CLI E2E image environment file at ${path}: ${message}`);
    }

    const env: NodeJS.ProcessEnv = {};

    // load-cli-e2e-images.sh writes simple GitHub environment records like:
    //   ASPIRE_E2E_DOTNET_IMAGE=aspire-cli-e2e-dotnet:prebuilt
    //   ASPIRE_E2E_REQUIRE_DOTNET_IMAGE=true
    // It does not emit multiline NAME<<EOF records, so split on the first '='.
    for (const line of contents.split(/\r?\n/)) {
        if (line.length === 0) {
            continue;
        }

        const multilineSeparatorIndex = line.indexOf('<<');
        const separatorIndex = line.indexOf('=');
        if (multilineSeparatorIndex > 0 && (separatorIndex < 0 || multilineSeparatorIndex < separatorIndex)) {
            throw new Error(`Multiline CLI E2E image environment records are not supported in ${path}: ${line}`);
        }

        if (separatorIndex <= 0) {
            throw new Error(`Invalid CLI E2E image environment record in ${path}: ${line}`);
        }

        env[line.slice(0, separatorIndex)] = line.slice(separatorIndex + 1);
    }

    return env;
}

function appendImageRequirement(args: string[], option: string, requirement: ImageRequirement | undefined): void {
    if (requirement === undefined) {
        return;
    }

    args.push(option, requirement === 'auto' ? requirement : String(requirement));
}
