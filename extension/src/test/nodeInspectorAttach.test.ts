import * as assert from 'assert';
import * as fs from 'fs';
import * as net from 'net';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { findDescendantProcessIds, SystemLaunchedChildProcessQuery } from '../debugger/launchedChildProcessDiscovery';
import { denoAppHostInspectorAttachTimeoutMs, nodeDebuggerExtension } from '../debugger/languages/node';
import { AspireResourceExtendedDebugConfiguration, NodeLaunchConfiguration } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';

// Stands in for a Deno AppHost started with --inspect-wait: it opens its inspector only when the test
// tells it to, then blocks until a debugger attaches. It reports progress over a control socket so the
// test can synchronize on real events instead of sleeping.
const inspectorFixture = `
const inspector = require('inspector');
const net = require('net');
const controlPort = Number(process.argv[2]);
const inspectorPort = Number(/:(\\d+)$/.exec(process.argv[3])[1]);
const control = net.connect(controlPort, '127.0.0.1', () => control.write('spawned ' + process.pid + '\\n'));
control.setEncoding('utf8');
control.on('data', data => {
    if (data.includes('open-inspector')) {
        inspector.open(inspectorPort, '127.0.0.1', true);
        control.end('attached\\n');
    }
});
`;

interface FixtureProcess {
    pid: number;
    socket: net.Socket;
    lines: string[];
    // Before it reports "attached" the fixture never closes its control socket, so closure then means the process exited.
    closed: Promise<void>;
    isClosed(): boolean;
    waitForLine(line: string): Promise<void>;
}

// Drives VS Code's built-in js-debug with the attach configuration produced for a Deno AppHost, so the
// inspector readiness timeout is exercised by the same adapter that enforces it in production.
suite('Node Debugger Deno AppHost inspector attach (js-debug)', function () {
    this.timeout(60000);

    let tempDir: string;
    let fixturePath: string;
    let controlServer: net.Server;
    let controlPort: number;
    let nextFixture: ((fixture: FixtureProcess) => void) | undefined;
    const disposables: vscode.Disposable[] = [];
    const resourceCleanups: vscode.Disposable[] = [];
    const pendingStartCancellations = new Set<vscode.Disposable>();
    // Tracked independently of the cleanup under test so teardown can always stop leftover fixtures.
    const controlSockets = new Set<net.Socket>();
    const fixtures: FixtureProcess[] = [];
    const aspireDebugSession = {
        registerResourceCleanup: (disposable: vscode.Disposable) => resourceCleanups.push(disposable),
        registerPendingStartCancellation: (disposable: vscode.Disposable) => {
            pendingStartCancellations.add(disposable);
            return { dispose: () => pendingStartCancellations.delete(disposable) };
        },
        trackPendingResourceCleanup: (_name: string, cleanup: () => Promise<void>) => { void cleanup().catch(() => { }); },
    } as unknown as AspireDebugSession;

    suiteSetup(async () => {
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-inspector-attach-'));
        fixturePath = path.join(tempDir, 'inspector-fixture.js');
        fs.writeFileSync(fixturePath, inspectorFixture);

        controlServer = net.createServer(socket => {
            socket.setEncoding('utf8');
            controlSockets.add(socket);
            const lines: string[] = [];
            const waiters: { line: string; resolve: () => void }[] = [];
            let isClosed = false;
            const closed = new Promise<void>(resolve => socket.once('close', () => {
                isClosed = true;
                controlSockets.delete(socket);
                resolve();
            }));
            let buffer = '';
            let fixture: FixtureProcess | undefined;
            socket.on('data', (data: string) => {
                buffer += data;
                let newline: number;
                while ((newline = buffer.indexOf('\n')) >= 0) {
                    const line = buffer.slice(0, newline);
                    buffer = buffer.slice(newline + 1);
                    lines.push(line);
                    for (const waiter of waiters.filter(w => w.line === line)) {
                        waiter.resolve();
                    }

                    if (!fixture && line.startsWith('spawned ')) {
                        fixture = {
                            pid: Number(line.slice('spawned '.length)),
                            socket,
                            lines,
                            closed,
                            isClosed: () => isClosed,
                            waitForLine: (expected: string) => lines.includes(expected)
                                ? Promise.resolve()
                                : new Promise<void>(resolve => waiters.push({ line: expected, resolve })),
                        };
                        fixtures.push(fixture);
                        nextFixture?.(fixture);
                    }
                }
            });
            socket.on('error', () => { /* the fixture may be killed mid-write */ });
        });
        await new Promise<void>(resolve => controlServer.listen(0, '127.0.0.1', resolve));
        controlPort = (controlServer.address() as net.AddressInfo).port;
    });

    suiteTeardown(async () => {
        controlSockets.forEach(socket => socket.destroy());
        await new Promise<void>(resolve => controlServer.close(() => resolve()));
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    // What the cleanup under test logged, reported with the process tree when a test fails, since a runtime
    // that is never stopped otherwise only surfaces as a test timeout.
    const cleanupLog: string[] = [];
    setup(() => {
        cleanupLog.length = 0;
        for (const level of ['trace', 'debug', 'info', 'warn', 'error'] as const) {
            const log = extensionLogOutputChannel[level].bind(extensionLogOutputChannel);
            sinon.stub(extensionLogOutputChannel, level).callsFake((message: string | Error, ...args: unknown[]) => {
                cleanupLog.push(`${new Date().toISOString()} [${level}] ${message instanceof Error ? message.message : message}`);
                log(message as string, ...args);
            });
        }
    });

    async function describeUnstoppedFixtures(): Promise<string> {
        const lines = [`extension host pid ${process.pid}`, ...cleanupLog];
        const running = fixtures.filter(fixture => !fixture.isClosed());
        if (running.length === 0) {
            return lines.join('\n');
        }

        try {
            const listing = await new SystemLaunchedChildProcessQuery().listProcesses(undefined, 30_000);
            const descendants = findDescendantProcessIds(process.pid, false, listing);
            lines.push(`listed ${listing.length} processes; extension host descendants: ${descendants === undefined ? 'unresolvable' : descendants.join(', ')}`);
            for (const fixture of running) {
                lines.push(`fixture ${fixture.pid} still connected; output: ${JSON.stringify(fixture.lines)}`);
                for (let pid: number | undefined = fixture.pid, depth = 0; pid !== undefined && depth < 8; depth++) {
                    const entries = listing.filter(candidate => candidate.pid === pid);
                    lines.push(entries.length === 0
                        ? `  pid ${pid} is not listed`
                        : entries.map(entry => `  pid ${entry.pid} parent ${entry.parentPid} ${entry.executable}: ${entry.command}`).join('\n'));
                    pid = pid === process.pid ? undefined : entries[0]?.parentPid;
                }
            }
        }
        catch (error) {
            lines.push(`process listing failed: ${error}`);
        }

        return lines.join('\n');
    }

    teardown(async function () {
        if (this.currentTest?.state !== 'passed') {
            console.log(`Deno AppHost inspector cleanup diagnostics for '${this.currentTest?.title}':\n${await describeUnstoppedFixtures()}`);
        }

        nextFixture = undefined;
        try {
            disposables.splice(0).forEach(d => d.dispose());
            await vscode.debug.stopDebugging();
            resourceCleanups.splice(0).forEach(d => d.dispose());
        }
        finally {
            sinon.restore();
            pendingStartCancellations.clear();
            // Don't rely on the production cleanup being tested: kill any fixture that is still connected.
            for (const fixture of fixtures.splice(0)) {
                if (!fixture.isClosed()) {
                    try {
                        process.kill(fixture.pid);
                    }
                    catch {
                        // Already exited.
                    }
                }
            }
            controlSockets.forEach(socket => socket.destroy());
        }
    });

    async function createAppHostAttachConfiguration(
        name: string,
        configuredTimeout?: number,
        debugSession: AspireDebugSession = aspireDebugSession,
        configuredConsole?: string): Promise<AspireResourceExtendedDebugConfiguration> {
        const launchConfig: NodeLaunchConfiguration = { type: 'node', runtime_executable: 'deno', working_directory: tempDir };
        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: name,
            debugSessionId: name,
            type: 'node',
            name,
            request: 'launch',
            ...(configuredTimeout === undefined ? {} : { timeout: configuredTimeout }),
            ...(configuredConsole === undefined ? {} : { console: configuredConsole }),
        };

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', path.join(tempDir, 'apphost.mts')],
            [],
            { debug: true, runId: name, debugSessionId: name, isApphost: true, debugSession },
            debugConfig);

        // Keep js-debug's attach settings and the runtime's --inspect-wait argument from the Deno AppHost
        // configuration, but run the controllable fixture instead of deno so the test decides when (or
        // whether) the inspector becomes ready.
        const inspectorArgument = debugConfig.runtimeArgs.find((arg: string) => arg.startsWith('--inspect-wait='));
        assert.strictEqual(inspectorArgument, `--inspect-wait=127.0.0.1:${debugConfig.attachSimplePort}`);
        debugConfig.runtimeExecutable = 'node';
        debugConfig.runtimeArgs = [fixturePath, String(controlPort), inspectorArgument];
        return debugConfig;
    }

    function waitForFixture(): Promise<FixtureProcess> {
        return new Promise(resolve => { nextFixture = resolve; });
    }

    // js-debug reports launch failures as a DAP error response whose user-facing text is in body.error.format.
    function captureLaunchResponse(name: string): Promise<{ success: boolean; error?: string }> {
        return new Promise(resolve => {
            disposables.push(vscode.debug.registerDebugAdapterTrackerFactory('pwa-node', {
                createDebugAdapterTracker: session => session.configuration.name !== name ? undefined : {
                    onDidSendMessage: (message: { type?: string; command?: string; success?: boolean; message?: string; body?: { error?: { format?: string } } }) => {
                        if (message.type === 'response' && message.command === 'launch') {
                            resolve({ success: message.success === true, error: message.body?.error?.format ?? message.message });
                        }
                    }
                }
            }));
        });
    }

    // onDidStartDebugSession only fires once launch succeeds, so take the root session from its adapter tracker.
    function waitForRootSession(name: string): Promise<vscode.DebugSession> {
        return new Promise(resolve => {
            disposables.push(vscode.debug.registerDebugAdapterTrackerFactory('pwa-node', {
                createDebugAdapterTracker: session => {
                    if (session.configuration.name === name && !session.configuration.__pendingTargetId) {
                        resolve(session);
                    }
                    return undefined;
                }
            }));
        });
    }

    test('attaches once a delayed inspector becomes ready', async () => {
        const name = 'delayed-inspector';
        const debugConfig = await createAppHostAttachConfiguration(name);
        assert.strictEqual(debugConfig.timeout, denoAppHostInspectorAttachTimeoutMs);
        const fixtureStarted = waitForFixture();
        const launchResponse = captureLaunchResponse(name);

        const started = vscode.debug.startDebugging(undefined, debugConfig);
        const fixture = await fixtureStarted;

        // js-debug is now polling a port that is not listening yet; open the inspector late.
        fixture.socket.write('open-inspector\n');
        await fixture.waitForLine('attached');

        assert.strictEqual(await started, true);
        assert.strictEqual((await launchResponse).success, true);
        await fixture.closed;
    });

    test('fails with a clear error and stops the process when the inspector never becomes ready', async () => {
        const name = 'never-ready-inspector';
        // A short explicitly configured timeout stands in for the 120s default so the bounded failure
        // is observable within the test timeout.
        const debugConfig = await createAppHostAttachConfiguration(name, 2000);
        assert.strictEqual(debugConfig.timeout, 2000);
        const fixtureStarted = waitForFixture();
        const launchResponse = captureLaunchResponse(name);
        const startedAt = Date.now();

        const started = vscode.debug.startDebugging(undefined, debugConfig);
        const fixture = await fixtureStarted;

        const response = await launchResponse;
        assert.strictEqual(response.success, false);
        assert.ok(response.error?.includes(`127.0.0.1:${debugConfig.attachSimplePort}`),
            `Expected the attach failure to name the inspector address, got: ${response.error}`);
        assert.ok(Date.now() - startedAt >= 2000, 'js-debug gave up before the configured inspector timeout');

        await fixture.closed;
        assert.ok(!fixture.lines.includes('attached'));
        assert.strictEqual(await started, false);
    });

    test('stops a never-ready runtime when the AppHost debugger is configured to use the integrated terminal', async () => {
        const name = 'never-ready-integrated-terminal';
        // As with a `debuggers.apphost.console` override. In a terminal the runtime would run outside the
        // extension host's process tree, so cleanup could not find it after the attach times out.
        const debugConfig = await createAppHostAttachConfiguration(name, 2000, aspireDebugSession, 'integratedTerminal');
        assert.strictEqual(debugConfig.console, 'internalConsole');
        const fixtureStarted = waitForFixture();
        const launchResponse = captureLaunchResponse(name);

        const started = vscode.debug.startDebugging(undefined, debugConfig);
        const fixture = await fixtureStarted;

        assert.strictEqual((await launchResponse).success, false);
        await fixture.closed;
        assert.ok(!fixture.lines.includes('attached'));
        assert.strictEqual(await started, false);
    });

    test('stops a pending inspector attach when the debug session is cancelled', async () => {
        const name = 'cancelled-inspector';
        const debugConfig = await createAppHostAttachConfiguration(name);
        assert.strictEqual(debugConfig.timeout, denoAppHostInspectorAttachTimeoutMs);
        const fixtureStarted = waitForFixture();
        const rootSession = waitForRootSession(name);

        const started = vscode.debug.startDebugging(undefined, debugConfig);
        const [session, fixture] = await Promise.all([rootSession, fixtureStarted]);

        // js-debug is still polling the inspector port, well inside the 120s attach timeout.
        cleanupLog.push(`${new Date().toISOString()} [test] stopping the debug session; fixture ${fixture.pid}`);
        await vscode.debug.stopDebugging(session);

        cleanupLog.push(`${new Date().toISOString()} [test] debug session stopped`);
        await fixture.closed;
        cleanupLog.push(`${new Date().toISOString()} [test] fixture exited`);
        assert.ok(!fixture.lines.includes('attached'));
        assert.strictEqual(await started, false);
    });

    // TEMPORARY: stress the cancel path on CI to capture diagnostics for the flake. Not for merge.
    let stressFailures = 0;
    for (let iteration = 0; iteration < 120; iteration++) {
        test(`STRESS ${iteration}: integrated terminal then cancel`, async function () {
            if (stressFailures >= 3) {
                this.skip();
            }

            stressFailures++;
            {
                const name = `stress-terminal-${iteration}`;
                const debugConfig = await createAppHostAttachConfiguration(name, 2000, aspireDebugSession, 'integratedTerminal');
                const fixtureStarted = waitForFixture();
                const launchResponse = captureLaunchResponse(name);
                const started = vscode.debug.startDebugging(undefined, debugConfig);
                const fixture = await fixtureStarted;
                assert.strictEqual((await launchResponse).success, false);
                await fixture.closed;
                assert.strictEqual(await started, false);
            }
            {
                const name = `stress-cancel-${iteration}`;
                const debugConfig = await createAppHostAttachConfiguration(name);
                const fixtureStarted = waitForFixture();
                const rootSession = waitForRootSession(name);
                const started = vscode.debug.startDebugging(undefined, debugConfig);
                const [session, fixture] = await Promise.all([rootSession, fixtureStarted]);
                cleanupLog.push(`${new Date().toISOString()} [test] stopping the debug session; fixture ${fixture.pid}`);
                await vscode.debug.stopDebugging(session);
                cleanupLog.push(`${new Date().toISOString()} [test] debug session stopped`);
                await fixture.closed;
                cleanupLog.push(`${new Date().toISOString()} [test] fixture exited`);
                assert.strictEqual(await started, false);
            }
            stressFailures--;
        });
    }

    test('stops the runtime when the session is cancelled as js-debug receives the launch request', async () => {
        const name = 'cancelled-during-launch';
        const debugConfig = await createAppHostAttachConfiguration(name);
        const fixtureStarted = waitForFixture();
        let cancelled = false;
        // Request the stop before js-debug has spawned the runtime. VS Code delivers it later, so the
        // runtime-not-yet-listed race itself is covered deterministically by the stopInspectorWaitingProcesses
        // unit tests; this checks the end-to-end cancellation path.
        disposables.push(vscode.debug.registerDebugAdapterTrackerFactory('pwa-node', {
            createDebugAdapterTracker: session => session.configuration.name !== name || session.configuration.__pendingTargetId ? undefined : {
                onWillReceiveMessage: (message: { type?: string; command?: string }) => {
                    if (message.type === 'request' && message.command === 'launch' && !cancelled) {
                        cancelled = true;
                        void vscode.debug.stopDebugging(session);
                    }
                }
            }
        }));

        const started = vscode.debug.startDebugging(undefined, debugConfig);
        const fixture = await fixtureStarted;
        assert.ok(cancelled);

        await fixture.closed;
        assert.ok(!fixture.lines.includes('attached'));
        assert.strictEqual(await started, false);
    });

    test('keeps cleanup unconfirmed and retryable when the runtime is listed but its command line cannot be read', async function () {
        if (process.platform === 'win32') {
            // Windows listings carry each command line, so no separate process-detail lookup can fail.
            this.skip();
        }

        const name = 'unreadable-process-details';
        const cleanups: (() => Promise<void>)[] = [];
        const debugSession = { ...aspireDebugSession, trackPendingResourceCleanup: (_name: string, cleanup: () => Promise<void>) => { cleanups.push(cleanup); } } as unknown as AspireDebugSession;
        const debugConfig = await createAppHostAttachConfiguration(name, 2000, debugSession);
        // The process listing (pids and parent pids) succeeds, but every command-line lookup fails, as with a
        // detail-query timeout or I/O error, until the test allows it.
        let detailsReadable = false;
        const getProcess = SystemLaunchedChildProcessQuery.prototype.getProcess;
        sinon.stub(SystemLaunchedChildProcessQuery.prototype, 'getProcess').callsFake(function (this: SystemLaunchedChildProcessQuery, ...args) {
            return detailsReadable ? getProcess.apply(this, args) : Promise.reject(new Error('Unable to resolve the running application process.'));
        });
        const fixtureStarted = waitForFixture();
        const launchResponse = captureLaunchResponse(name);

        const started = vscode.debug.startDebugging(undefined, debugConfig);
        const fixture = await fixtureStarted;
        assert.strictEqual((await launchResponse).success, false);
        assert.strictEqual(await started, false);
        assert.strictEqual(cleanups.length, 1);

        // The launch has finished, so an empty match would end cleanup, but an unreadable process is not a gone one.
        await assert.rejects(cleanups[0](), /Could not read the command line of process .*127\.0\.0\.1:/);
        assert.ok(!fixture.isClosed(), 'The runtime should not be signalled without confirming its identity');

        // The owning session's next shutdown retries the same cleanup, which now identifies and stops the runtime.
        detailsReadable = true;
        await cleanups[0]();
        await fixture.closed;
        assert.ok(!fixture.lines.includes('attached'));
    });

    function createOwningSession(): AspireDebugSession {
        const parentSession = {
            id: 'owning-aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: { type: 'aspire', request: 'launch', name: 'Aspire', program: path.join(tempDir, 'apphost.mts'), command: 'run' },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const stopDebugging = vscode.debug.stopDebugging;
        // The synthetic parent is not a real VS Code session; every other stop goes to VS Code.
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(session =>
            session?.id === parentSession.id ? Promise.resolve() : stopDebugging(session));
        return new AspireDebugSession(
            parentSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            { isDebugConfigEnvironmentLoggingEnabled: () => false } as any,
            () => { });
    }

    test('stops a pending inspector attach when the owning Aspire session stops', async () => {
        const name = 'owning-session-stopped';
        const owningSession = createOwningSession();
        const debugConfig = await createAppHostAttachConfiguration(name, undefined, owningSession);
        const fixtureStarted = waitForFixture();

        // Mirror AspireDebugSession.startAndGetDebugSession: shutdown waits for this pending start.
        const pendingStart = owningSession.beginPendingDebugSessionStart(name);
        let startSettled = false;
        const started = vscode.debug.startDebugging(undefined, debugConfig).then(result => {
            startSettled = true;
            pendingStart.dispose();
            return result;
        });
        const fixture = await fixtureStarted;

        // js-debug is still polling the inspector, well inside the 120s attach timeout. Without
        // pending-start cancellation the shutdown waits on this start until its stop budget runs out,
        // and it must not finish before the runtime has exited.
        await owningSession.stopDebugging();
        assert.ok(startSettled, 'The owning session finished stopping before the pending Deno AppHost start settled');
        assert.ok(fixture.isClosed(), 'The owning session finished stopping while the Deno AppHost was still running');
        assert.ok(!fixture.lines.includes('attached'));
        assert.strictEqual(await started, false);
    });

    test('stops the owning Aspire session promptly after a launch that never spawned the Deno AppHost', async () => {
        const name = 'missing-executable';
        const owningSession = createOwningSession();
        const debugConfig = await createAppHostAttachConfiguration(name, undefined, owningSession);
        debugConfig.runtimeExecutable = path.join(tempDir, 'missing-deno');
        const launchResponse = captureLaunchResponse(name);

        const pendingStart = owningSession.beginPendingDebugSessionStart(name);
        // VS Code reports the missing executable in an error dialog, which the test host refuses by rejecting.
        const started = await vscode.debug.startDebugging(undefined, debugConfig).then(result => result, () => false);
        pendingStart.dispose();
        assert.strictEqual(started, false);
        assert.strictEqual((await launchResponse).success, false);

        // There is no runtime to wait for, so cleanup must not hold shutdown until its stop budget times out.
        await owningSession.stopDebugging();
    });
});
