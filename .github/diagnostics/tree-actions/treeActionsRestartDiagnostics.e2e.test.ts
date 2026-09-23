// TEMPORARY #20103 investigation. Copied into the pinned test checkout by the
// manual reproduction workflow; never included in a shipping extension.
import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { findResource, getCommandInvocationCount, readStateFile, waitForAppHostLaunching, waitForCommandOutcome, waitForExtensionState, waitForHttpText, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResourceState, waitForRunningAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

const directory = process.env.TREE_ACTIONS_DIAGNOSTICS_DIRECTORY;
assert.ok(directory && path.isAbsolute(directory), 'An absolute diagnostic directory is required.');
fs.mkdirSync(directory, { recursive: true });
const tracePath = path.join(directory, 'lifecycle.jsonl');
const statusPath = path.join(directory, 'stress-status.json');
const abortPath = path.join(directory, 'abort');
const cycles = Number(process.env.TREE_ACTIONS_DIAGNOSTIC_CYCLES ?? '30');
assert.ok(Number.isInteger(cycles) && cycles > 0 && cycles <= 30, 'The diagnostic cycle budget must be 1-30.');
let completedCycles = 0;
let appHostPath = '';
let workerName = '';

function trace(phase: string, cycle: number, command?: string, details: Record<string, unknown> = {}): void {
    const record: Record<string, unknown> = { timestamp: new Date().toISOString(), phase, cycle, command, ...details };
    try {
        const file = readStateFile();
        const worker = findResource(file.state, workerName || 'e2e-worker');
        // Do not export dashboard URLs, command argument values, or the full
        // state: live checkpoints precede the normal runner's final redaction.
        record.appHostPid = file.state.workspaceAppHost?.appHostPid;
        record.worker = worker && { name: worker.name, state: worker.state };
        record.control = file.control && {
            revision: file.control.revision,
            status: file.control.status,
            startedObserved: file.control.startedObserved,
        };
        record.recentCommands = file.commandInvocations
            .filter(event => event.command.endsWith('Resource'))
            .slice(-3)
            .map(event => ({
                command: event.command, sequence: event.sequence,
                outcome: event.outcome, durationMs: event.durationMs,
            }));
    } catch (error) {
        record.snapshotError = error instanceof Error ? error.name : 'UnknownError';
    }
    fs.appendFileSync(tracePath, `${JSON.stringify(record)}\n`, 'utf8');
    console.log(`[tree-actions-diag] ${JSON.stringify(record)}`);
}

function writeStatus(outcome: string): void {
    fs.writeFileSync(statusPath, JSON.stringify({
        outcome, requestedCycles: cycles, completedCycles,
        updatedAt: new Date().toISOString(),
    }, undefined, 2));
}

suite('Aspire tree-actions shutdown diagnostics', function () {
    this.timeout(600000);

    teardown(async () => {
        trace('teardown-start', completedCycles);
        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => waitForNoDebugSessions(),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoRunningAppHost(),
        ], 'Diagnostic tree-actions teardown failed.');
        trace('teardown-complete', completedCycles);
    });

    test('rapid stop/start/restart preserves the worker', async () => {
        writeStatus('running');
        trace('suite-start', 0);
        try {
            await openAspireView();
            await waitForRepositoryIdle();
            const discovered = await waitForWorkspaceAppHost();
            appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
            const before = getCommandInvocationCount('aspire-vscode.runAppHost');
            await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
            await waitForAppHostLaunching(appHostPath);
            await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000, before);
            await waitForRunningAppHost();
            const running = await waitForResourceState('e2e-worker', ['Running'], 180000);
            const worker = findResource(running.state, 'e2e-worker');
            assert.ok(worker);
            workerName = worker.name;
            const endpoint = worker.urls?.find(url => !url.isInternal)?.url;
            assert.ok(endpoint);
            await waitForHttpText(endpoint, 'ok');

            for (let cycle = 1; cycle <= cycles; cycle++) {
                assert.ok(!fs.existsSync(abortPath), 'Diagnostic controller requested stopping after a capture failure.');
                trace('cycle-start', cycle);
                await runAction('stopResource', cycle, ['Exited', 'Finished', 'Stopped']);
                await runAction('startResource', cycle, ['Running']);
                // Match the historical sequence: restart as soon as Running is
                // observed, without an added delay or a stronger readiness gate.
                await runAction('restartResource', cycle, ['Running']);
                completedCycles++;
                writeStatus('running');
                trace('cycle-complete', cycle);
            }
            writeStatus('passed');
            trace('suite-complete', completedCycles);
        } catch (error) {
            writeStatus('failed');
            trace('suite-failed', completedCycles, undefined, {
                errorName: error instanceof Error ? error.name : 'UnknownError',
            });
            throw error;
        }
    });
});

async function runAction(command: 'stopResource' | 'startResource' | 'restartResource', cycle: number, states: string[]): Promise<void> {
    const commandId = `aspire-vscode.${command}`;
    const before = getCommandInvocationCount(commandId);
    const started = Date.now();
    trace('command-start', cycle, command);
    try {
        // Retain the original helper's 10-second control timeout.
        await executeE2eControlCommand({ name: command, appHostPath, resourceName: workerName });
        await waitForCommandOutcome(commandId, 'success', 60000, before);
        await waitForResourceState(workerName, states, 90000);
        trace('command-complete', cycle, command, { elapsedMs: Date.now() - started });
    } catch (error) {
        trace('command-error', cycle, command, {
            elapsedMs: Date.now() - started,
            errorName: error instanceof Error ? error.name : 'UnknownError',
        });
        // Observe the native operation's eventual result before teardown can
        // destroy the evidence. This does not retry or turn the failure green.
        try {
            await waitForExtensionState(
                file => file.commandInvocations.some(event => event.command === commandId && event.sequence > before),
                'diagnostic terminal command outcome', 25000);
            trace('diagnostic-terminal-outcome', cycle, command, { elapsedMs: Date.now() - started });
        } catch (observationError) {
            trace('diagnostic-outcome-unavailable', cycle, command, {
                elapsedMs: Date.now() - started,
                errorName: observationError instanceof Error ? observationError.name : 'UnknownError',
            });
        }
        throw error;
    }
}
