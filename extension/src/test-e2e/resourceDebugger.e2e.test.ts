import * as assert from 'assert';
import { isSamePath, waitForNoDebugSessions, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { clearBreakpoints, executeE2eControlCommand, getAppHostPidFromState, getNodeAppBreakpointLine, isProcessRunning, runE2eTeardown, stopPrimaryAppHostIfRunning, waitForProcessExit } from './helpers/fixtures';
import { getNodeAppScriptPath, getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView } from './helpers/vscode';
import { getRemainingE2eDeadlineMs, runWithE2eDeadline } from '../testing/e2eDeadline';
import { readReportedPidFromDebugOutput } from '../testing/resourceDebugOutput';

interface ResourceDebugSessionSnapshot {
    id: string;
    type: string;
    name: string;
    parentSessionId?: string;
    parentSessionType?: string;
    configuration: Record<string, unknown>;
}

interface DebugAdapterMessageSummary {
    sessionId: string;
    sessionType: string;
    sessionName: string;
    command?: string;
}

interface ResourceDebugProof {
    proof: string;
    appHostPath: string;
    resourceName: string;
    breakpoint: { sourcePath: string; line: number; text?: string };
    appHostDebugSession?: ResourceDebugSessionSnapshot;
    resourceDebugSession?: ResourceDebugSessionSnapshot;
    supportedLaunchConfigurations: string[];
    debugSessions: ResourceDebugSessionSnapshot[];
    matchingStackFrame?: { source?: { path?: string }; line?: number; name?: string };
    topStackFrame?: { source?: { path?: string }; line?: number; name?: string };
    processEvents: Array<{ sessionId: string; sessionType: string; systemProcessId?: number; name?: string; startMethod?: string }>;
    continueRequests: DebugAdapterMessageSummary[];
    outputHead: Array<{ sessionId: string; sessionType: string; output: string }>;
    outputSample: Array<{ sessionId: string; sessionType: string; output: string }>;
}

const nodeResourceName = 'e2e-node';
const resourceDebuggerDeadlineTimeoutMs = 900000;
// Teardown has four ordered phases with independent ceilings. Their sum bounds cleanup while
// ensuring an expired proof deadline cannot prevent later cleanup callbacks from running.
const resourceDebuggerTeardownTimeoutMs = 480000;
const resourceDebuggerProofControlSlackMs = 60000;
const resourceDebuggerPhaseTimeoutMs = {
    openAspireView: 60000,
    repositoryIdle: 120000,
    workspaceAppHost: 120000,
    proof: 300000,
    proofControl: 360000,
    stopDebuggingControl: 180000,
    processExit: 120000,
    debugSessions: 90000,
    appHostStop: 120000,
    appHostExit: 120000,
} as const;

let resourceDebuggerDeadline = 0;

suite('Aspire resource debugger E2E', function () {
    // Proof phases share one deadline so adding another setup or assertion cannot silently extend
    // the test. Teardown gets a separate bounded budget so cleanup still runs after that deadline.
    this.timeout(resourceDebuggerDeadlineTimeoutMs + resourceDebuggerTeardownTimeoutMs);

    setup(() => {
        resourceDebuggerDeadline = Date.now() + resourceDebuggerDeadlineTimeoutMs;
    });

    teardown(async () => {
        await runE2eTeardown([
            () => runResourceDebuggerCleanupPhase(
                'resource debugger teardown stop control',
                resourceDebuggerPhaseTimeoutMs.stopDebuggingControl,
                timeoutMs => executeE2eControlCommand({ name: 'stopDebugging' }, { timeoutMs })),
            () => runResourceDebuggerCleanupPhase(
                'resource debugger teardown breakpoint cleanup',
                resourceDebuggerPhaseTimeoutMs.debugSessions,
                () => clearBreakpoints()),
            () => runResourceDebuggerCleanupPhase(
                'resource debugger teardown AppHost stop',
                resourceDebuggerPhaseTimeoutMs.appHostStop,
                () => stopPrimaryAppHostIfRunning()),
            () => runResourceDebuggerCleanupPhase(
                'resource debugger teardown debug sessions',
                resourceDebuggerPhaseTimeoutMs.debugSessions,
                timeoutMs => waitForNoDebugSessions(timeoutMs)),
        ], 'Resource debugger E2E teardown failed.');
    });

    test('stops on a breakpoint inside the Node resource and reports a matching stack frame', async () => {
        const proof = await getSharedResourceDebugProof(resourceDebuggerDeadline);
        const scriptPath = getNodeAppScriptPath();

        assert.strictEqual(proof.proof, 'aspire-resource-debug-breakpoint-hit');
        assert.strictEqual(proof.resourceName, nodeResourceName);
        assert.ok(isSamePath(proof.breakpoint.sourcePath, scriptPath), `Expected the breakpoint to be set in ${scriptPath}, got ${proof.breakpoint.sourcePath}.`);
        assert.strictEqual(proof.breakpoint.line, getNodeAppBreakpointLine() + 1);

        const matchingFrame = proof.matchingStackFrame;
        assert.ok(matchingFrame, `Expected a stack frame in ${scriptPath}: ${JSON.stringify(proof.topStackFrame)}`);
        assert.ok(matchingFrame.source?.path && isSamePath(matchingFrame.source.path, scriptPath), `Expected the stopped frame to come from ${scriptPath}, got ${matchingFrame.source?.path}.`);
        assert.strictEqual(matchingFrame.line, getNodeAppBreakpointLine() + 1);
    });

    test('debugs the Node resource in a session distinct from the AppHost or Aspire session', async () => {
        const proof = await getSharedResourceDebugProof(resourceDebuggerDeadline);

        const appHostSession = proof.appHostDebugSession;
        const resourceSession = proof.resourceDebugSession;
        assert.ok(resourceSession, `Expected the stopped resource debug session: ${JSON.stringify(proof.debugSessions.map(toSessionSummary))}`);
        assert.ok(appHostSession, `Expected a debug session that owns the AppHost: ${JSON.stringify(proof.debugSessions.map(toSessionSummary))}`);

        assert.strictEqual(resourceSession.type, 'pwa-node');
        assert.notStrictEqual(resourceSession.id, appHostSession.id);

        // Which session owns a C# AppHost depends on the installed extensions, so assert both
        // shapes exactly instead of tolerating a missing AppHost session. The CLI hands the AppHost
        // launch to this extension only when it advertises the `project` capability
        // (`DotNetCliRunner.ExecuteAsync` -> `HasCapabilityAsync(KnownCapabilities.Project)`), which
        // the extension reports only when `ms-dotnettools.csharp` is installed. The E2E VS Code
        // instance installs just the Aspire VSIX (see extension/scripts/run-e2e.js), so in CI the
        // CLI runs the AppHost itself with `dotnet run` and the synthetic `aspire` parent owns it;
        // a developer running this locally with the C# extension installed gets a real `coreclr`
        // AppHost child session instead.
        //
        // The two shapes carry the AppHost project differently, so `program` is only asserted on the
        // synthetic session. The real `coreclr` session never keeps the `.csproj` in `program`:
        // `createDebugSessionConfiguration` replaces it with the built output path, the `dotnet`
        // muxer, or the launch profile's executable (`dotnet.ts` `program` assignments), so a shared
        // assertion would fail locally before ever reaching the branch meant to validate that shape.
        if (proof.supportedLaunchConfigurations.includes('project')) {
            assert.strictEqual(appHostSession.type, 'coreclr');
            assert.strictEqual(appHostSession.configuration.isApphost, true);
            assert.ok(
                String(appHostSession.configuration.program ?? '').length > 0,
                `Expected the coreclr AppHost session to launch a built program: ${JSON.stringify(appHostSession.configuration.program)}.`);
        }
        else {
            assert.strictEqual(appHostSession.type, 'aspire');
            assert.strictEqual(appHostSession.configuration.isApphost, undefined);
            assert.ok(isSamePath(String(appHostSession.configuration.program ?? ''), getPrimaryAppHostProjectPath()));
        }
    });

    test('stopping debugging tears down the Node resource process tree', async () => {
        // The teardown this asserts on depends on the resource stop ordering fixed in
        // https://github.com/microsoft/aspire/pull/19145, which this PR declares as a prerequisite:
        // stopping while a resource is suspended on a breakpoint has to stop the resource session
        // before the AppHost or the debuggee and its children are left running.
        const proof = await runResourceDebugProof({ stopDebuggingOnCompletion: false }, resourceDebuggerDeadline);

        assert.ok(proof.resourceDebugSession, `Expected the stopped resource debug session: ${JSON.stringify(proof.debugSessions.map(toSessionSummary))}`);
        assert.deepStrictEqual(
            proof.continueRequests.filter(request => request.sessionId === proof.resourceDebugSession!.id),
            [],
            'Expected the resource debug session to remain suspended until this test starts the stop.');

        // The debuggee reports its own pid and the pid of the child it spawns, so the assertion covers
        // the process tree rather than only the process js-debug launched. The pids come from the
        // resource's own captured output because js-debug does not send the optional DAP `process`
        // event when it runs inside VS Code: it only sends `process` from its standalone DAP server
        // entry points, and even there it carries no `systemProcessId`.
        // See https://github.com/microsoft/vscode-js-debug/blob/main/src/vsDebugServer.ts
        const debuggeePid = readReportedPidFromDebugOutput(proof, 'ASPIRE_E2E_NODE_PID');
        const childPid = readReportedPidFromDebugOutput(proof, 'ASPIRE_E2E_NODE_CHILD_PID');
        assert.ok(isProcessRunning(debuggeePid), `Expected the Node resource process ${debuggeePid} to still be running before debugging stops.`);
        assert.ok(isProcessRunning(childPid), `Expected the Node child process ${childPid} to still be running before debugging stops.`);

        // Captured while the AppHost is still running so the teardown assertion below can check the
        // real process rather than the extension's view of it.
        const appHostPid = getAppHostPidFromState(proof.appHostPath);
        assert.ok(appHostPid !== undefined, 'Expected the extension state to report an AppHost pid while the AppHost is running.');
        assert.ok(isProcessRunning(appHostPid), `Expected the AppHost process ${appHostPid} to still be running before debugging stops.`);

        // Only the start of the stop is awaited here. This test is about the debuggee's process tree,
        // and waiting for the whole Aspire stop to settle would fold AppHost shutdown timing into the
        // assertion. The suite teardown still performs and awaits the full stop.
        // Cleanup for a failure before this point is handled by that teardown; a local finally here
        // would replace the assertion error with the teardown error and hide why the test failed.
        await runResourceDebuggerPhase(
            'resource debugger stop control',
            resourceDebuggerDeadline,
            resourceDebuggerPhaseTimeoutMs.stopDebuggingControl,
            timeoutMs => executeE2eControlCommand({ name: 'stopDebugging' }, { waitFor: 'started', timeoutMs }));

        await Promise.all([
            runResourceDebuggerPhase(
                'debugged Node resource process exit',
                resourceDebuggerDeadline,
                resourceDebuggerPhaseTimeoutMs.processExit,
                timeoutMs => waitForProcessExit(debuggeePid, 'the debugged Node resource process', timeoutMs)),
            runResourceDebuggerPhase(
                'Node resource child process exit',
                resourceDebuggerDeadline,
                resourceDebuggerPhaseTimeoutMs.processExit,
                timeoutMs => waitForProcessExit(childPid, 'the Node resource child process', timeoutMs)),
        ]);

        await runResourceDebuggerPhase(
            'resource debugger sessions to stop',
            resourceDebuggerDeadline,
            resourceDebuggerPhaseTimeoutMs.debugSessions,
            timeoutMs => waitForNoDebugSessions(timeoutMs));

        // The stop is still in flight in the extension host: the AppHost's own graceful shutdown after
        // an IDE debug session can outlast the extension's internal wait, and the E2E control channel
        // applies one command at a time, so the teardown's commands would queue behind it. Stopping the
        // AppHost through the CLI does not use the control channel, so it lets that wait settle.
        await runResourceDebuggerPhase(
            'resource debugger AppHost stop',
            resourceDebuggerDeadline,
            resourceDebuggerPhaseTimeoutMs.appHostStop,
            () => stopPrimaryAppHostIfRunning());

        // Assert on the AppHost process rather than on `waitForNoRunningAppHost`. The state file's
        // AppHost list is a mirror that lags a stop and can still name a dead pid long afterwards
        // (documented in `waitForNoRunningAppHostPathOrStopKnownProcess`, and why teardown relies on
        // process-aware stopping instead of that mirror). Process liveness is the stronger claim and
        // the one this test is actually making: stopping the debugger must leave no part of the tree
        // running.
        await runResourceDebuggerPhase(
            'resource debugger AppHost process exit',
            resourceDebuggerDeadline,
            resourceDebuggerPhaseTimeoutMs.appHostExit,
            timeoutMs => waitForProcessExit(appHostPid, 'the AppHost process', timeoutMs));
    });
});

let sharedResourceDebugProof: Promise<ResourceDebugProof> | undefined;

/**
 * Runs the debug proof once for the assertions that only read its payload.
 *
 * Each proof launches the AppHost and starts the resource under the debugger, so repeating it per
 * assertion would double the shard's runtime and its exposure to startup flakiness for no extra
 * coverage. The teardown between tests does not invalidate the captured payload.
 */
async function getSharedResourceDebugProof(deadline: number): Promise<ResourceDebugProof> {
    sharedResourceDebugProof ??= runResourceDebugProof({ stopDebuggingOnCompletion: true }, deadline);

    return await sharedResourceDebugProof;
}

async function runResourceDebugProof(options: { stopDebuggingOnCompletion: boolean }, deadline: number): Promise<ResourceDebugProof> {
    await runResourceDebuggerPhase(
        'Aspire view setup',
        deadline,
        resourceDebuggerPhaseTimeoutMs.openAspireView,
        () => openAspireView());
    await runResourceDebuggerPhase('repository idle setup', deadline, resourceDebuggerPhaseTimeoutMs.repositoryIdle, timeoutMs => waitForRepositoryIdle(timeoutMs));
    const discovered = await runResourceDebuggerPhase(
        'workspace AppHost setup',
        deadline,
        resourceDebuggerPhaseTimeoutMs.workspaceAppHost,
        timeoutMs => waitForWorkspaceAppHost(timeoutMs));
    const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

    const status = await runResourceDebuggerPhase(
        'resource debugger proof control',
        deadline,
        resourceDebuggerPhaseTimeoutMs.proofControl,
        timeoutMs => {
            // Leave the control channel time to observe and publish the proof result after the
            // extension-host command reaches its own deadline.
            const proofTimeoutMs = Math.min(
                resourceDebuggerPhaseTimeoutMs.proof,
                Math.max(1, timeoutMs - resourceDebuggerProofControlSlackMs));
            return executeE2eControlCommand({
                name: 'proveResourceDebugging',
                appHostPath,
                resourceName: nodeResourceName,
                sourcePath: getNodeAppScriptPath(),
                breakpointLine: getNodeAppBreakpointLine(),
                timeoutMs: proofTimeoutMs,
                expectedResourceDebugSessionType: 'pwa-node',
                stopDebuggingOnCompletion: options.stopDebuggingOnCompletion,
            }, { timeoutMs });
        });

    const proof = status.result as ResourceDebugProof | undefined;
    assert.ok(proof, `The resource debug proof returned no result: ${JSON.stringify(status)}`);

    return proof;
}

async function runResourceDebuggerPhase<T>(
    description: string,
    deadline: number,
    phaseCeilingMs: number,
    operation: (timeoutMs: number) => PromiseLike<T>,
): Promise<T> {
    const timeoutMs = getRemainingE2eDeadlineMs(description, deadline, phaseCeilingMs);
    const phaseDeadline = Math.min(deadline, Date.now() + timeoutMs);

    return await runWithE2eDeadline(description, phaseDeadline, () => operation(timeoutMs));
}

async function runResourceDebuggerCleanupPhase<T>(
    description: string,
    phaseCeilingMs: number,
    operation: (timeoutMs: number) => PromiseLike<T>,
): Promise<T> {
    const deadline = Date.now() + phaseCeilingMs;

    return await runResourceDebuggerPhase(description, deadline, phaseCeilingMs, operation);
}

function toSessionSummary(session: ResourceDebugSessionSnapshot): { id: string; type: string; name: string } {
    return { id: session.id, type: session.type, name: session.name };
}
