import { spawn } from 'node:child_process';
import { isAbsolute } from 'node:path';
import type { PipelineStepContext } from '../.aspire/modules/aspire.mjs';
import type { RepoRoot } from './repo.mjs';

export interface ExecOptions {
    title: string;
    command: string;
    args?: string[];
    workingDirectory?: string;
    env?: NodeJS.ProcessEnv;
    throwOnFailure?: boolean;
}

export async function exec(
    context: PipelineStepContext,
    repoRoot: RepoRoot,
    options: ExecOptions): Promise<number | null> {
    const reportingStep = await context.reportingStep();
    const task = await reportingStep.createTask(options.title);
    const cwd = options.workingDirectory === undefined
        ? repoRoot.path()
        : isAbsolute(options.workingDirectory)
            ? options.workingDirectory
            : repoRoot.path(options.workingDirectory);
    const args = options.args ?? [];

    const result = await new Promise<{ kind: 'close', exitCode: number | null } | { kind: 'error', error: Error }>((resolve) => {
        const child = spawn(options.command, args, {
            cwd,
            env: {
                ...process.env,
                ...options.env
            },
            stdio: ['ignore', 'pipe', 'pipe']
        });

        const logPromises: Promise<unknown>[] = [];
        child.stdout?.setEncoding('utf8');
        child.stdout?.on('data', chunk => {
            logProcessOutput(logPromises, reportingStep, 'information', chunk);
        });
        child.stderr?.setEncoding('utf8');
        child.stderr?.on('data', chunk => {
            logProcessOutput(logPromises, reportingStep, 'error', chunk);
        });
        child.once('error', error => resolve({ kind: 'error', error }));
        child.once('close', exitCode => {
            Promise.all(logPromises)
                .then(() => resolve({ kind: 'close', exitCode }))
                .catch(error => resolve({ kind: 'error', error: error instanceof Error ? error : new Error(String(error)) }));
        });
    });

    if (result.kind === 'error') {
        const message = `${options.title} failed to start '${options.command}': ${result.error.message}`;
        await task.completeTask({ completionMessage: message, completionState: 'completed-with-error' });
        throw new Error(message);
    }

    function logProcessOutput(
        logPromises: Promise<unknown>[],
        reportingStep: Awaited<ReturnType<PipelineStepContext['reportingStep']>>,
        level: string,
        chunk: string): void {
        const output = chunk.trimEnd();
        if (output.length === 0) {
            return;
        }

        logPromises.push(Promise.resolve(reportingStep.logStep(level, output)));
    }

    if (result.exitCode === 0) {
        await task.completeTask({ completionMessage: `${options.title} completed`, completionState: 'completed' });
        return result.exitCode;
    }

    const message = result.exitCode === null
        ? `${options.title} terminated before reporting an exit code`
        : `${options.title} failed with exit code ${result.exitCode}`;

    if (options.throwOnFailure === false) {
        await task.completeTask({ completionMessage: message, completionState: 'completed' });
        return result.exitCode;
    }

    await task.completeTask({ completionMessage: message, completionState: 'completed-with-error' });
    throw new Error(message);
}
