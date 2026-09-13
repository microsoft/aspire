import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { blazorWasmDebugProofTimeoutMs, getBlazorWasmDebugProofControlTimeoutMs } from '../testing/blazorWasmDebugProofTimeouts';

export { blazorWasmDebugProofTimeoutMs, blazorWasmDebugProofResponseAllowanceMs, getBlazorWasmDebugProofControlTimeoutMs } from '../testing/blazorWasmDebugProofTimeouts';

import { isSamePath } from './helpers/assertions';
import { executeE2eControlCommand } from './helpers/fixtures';
import { ensureDiagnosticsDir } from './helpers/paths';

interface BlazorWasmDebugSessionSnapshot {
    id: string;
    type: string;
    name: string;
    parentSessionId?: string;
    parentSessionType?: string;
    configuration: {
        browser?: unknown;
        projectPath?: unknown;
        resourceType?: unknown;
        noDebug?: unknown;
    };
}

interface BlazorWasmStackFrame {
    line?: number;
    source?: {
        path?: string;
    };
}

export interface BlazorWasmDebugProof {
    proof: 'blazor-wasm-managed-breakpoint-hit';
    rootSession: BlazorWasmDebugSessionSnapshot;
    browserSession: BlazorWasmDebugSessionSnapshot;
    managedSession: BlazorWasmDebugSessionSnapshot;
    breakpointResponse: {
        success?: boolean;
    };
    stoppedEvent: {
        reason?: string;
    };
    stackTrace: {
        stackFrames?: BlazorWasmStackFrame[];
    };
    commandStateAfterStop: {
        commandName?: string;
        state?: string | null;
    };
}

export interface ProveBlazorScenarioOptions {
    appHostPath: string;
    resourceName: string;
    sourcePath: string;
    breakpointMarker: string;
    requestPath: string;
    expectedBrowser: 'edge' | 'chrome';
    clientProjectPath: string;
    closeMode: 'explicit' | 'natural';
    timeoutMs?: number;
}

export async function proveBlazorScenario(options: ProveBlazorScenarioOptions): Promise<BlazorWasmDebugProof> {
    const breakpointLine = findBreakpointLine(options.sourcePath, options.breakpointMarker);
    const timeoutMs = options.timeoutMs ?? blazorWasmDebugProofTimeoutMs;
    const status = await executeE2eControlCommand({
        name: 'proveBlazorWasmDebugging',
        appHostPath: options.appHostPath,
        resourceName: options.resourceName,
        sourcePath: options.sourcePath,
        breakpointLine,
        requestPath: options.requestPath,
        expectedBrowser: options.expectedBrowser,
        closeMode: options.closeMode,
        timeoutMs,
    }, {
        timeoutMs: getBlazorWasmDebugProofControlTimeoutMs(timeoutMs),
    });
    const proof = status.result as BlazorWasmDebugProof;
    // The next control command replaces the state-file result. Preserve each
    // scenario's real adapter evidence before teardown or the next scenario.
    const proofFileName = `blazor-${options.resourceName.replace(/[^a-zA-Z0-9-]/g, '_')}-proof.json`;
    fs.writeFileSync(path.join(ensureDiagnosticsDir(), proofFileName), JSON.stringify(proof, undefined, 2));
    const expectedBrowserSessionType = options.expectedBrowser === 'edge' ? 'pwa-msedge' : 'pwa-chrome';
    const matchingFrame = proof.stackTrace.stackFrames?.find(frame =>
        typeof frame.source?.path === 'string'
        && isSamePath(frame.source.path, options.sourcePath)
        && frame.line === breakpointLine + 1);

    assert.strictEqual(proof.proof, 'blazor-wasm-managed-breakpoint-hit');
    const expectedBrowserAlias = options.expectedBrowser === 'edge' ? 'msedge' : 'chrome';
    assert.ok(['blazorwasm', expectedBrowserAlias, expectedBrowserSessionType].includes(proof.rootSession.type));
    assert.notStrictEqual(proof.rootSession.configuration.noDebug, true);
    assert.strictEqual(proof.rootSession.configuration.browser, options.expectedBrowser);
    assert.strictEqual(proof.rootSession.configuration.resourceType, 'browser');
    assert.ok(
        typeof proof.rootSession.configuration.projectPath === 'string'
        && isSamePath(proof.rootSession.configuration.projectPath, options.clientProjectPath),
        `Expected Blazor client project '${options.clientProjectPath}', got '${String(proof.rootSession.configuration.projectPath)}'.`);
    assert.ok([expectedBrowserAlias, expectedBrowserSessionType].includes(proof.browserSession.type));
    assert.strictEqual(proof.browserSession.parentSessionId, proof.rootSession.id);
    assert.strictEqual(proof.breakpointResponse.success, true);
    assert.strictEqual(proof.stoppedEvent.reason, 'breakpoint');
    assert.ok(
        matchingFrame,
        `Expected a stack frame for ${options.sourcePath}:${breakpointLine + 1}. Stack: ${JSON.stringify(proof.stackTrace)}`);
    assert.strictEqual(proof.commandStateAfterStop.commandName, 'debug-in-browser');
    assert.strictEqual(proof.commandStateAfterStop.state, 'Enabled');

    return proof;
}

function findBreakpointLine(sourcePath: string, marker: string): number {
    const lines = fs.readFileSync(sourcePath, 'utf8').split(/\r?\n/);
    const index = lines.findIndex(line => line.includes(marker));
    if (index < 0) {
        throw new Error(`Could not find '${marker}' in ${sourcePath} to place a breakpoint on.`);
    }

    return index;
}
