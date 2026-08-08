import * as assert from 'assert';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { browserDebuggerExtension } from '../debugger/languages/browser';
import { nodeDebuggerExtension } from '../debugger/languages/node';
import { prepareDebugSession } from '../debugger/debuggerExtensions';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { BrowserLaunchConfiguration, ExecutableLaunchConfiguration } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    stubBrowserProfileFs,
    stubbedMkdtempSuffix,
    configureBrowserDebugSession,
    createDebugSession,
    createResourceDebugConfig,
    DebugSessionHarness
} from './helpers/debugSessionHarness';

const browserProfileRootName = 'aspire-vscode-browser-debug';
const expectedRmOptions = { recursive: true, force: true, maxRetries: 3, retryDelay: 100 };

function browserProfileRootDir(): string {
    return path.join(os.tmpdir(), browserProfileRootName);
}

/**
 * The profile directory `stubBrowserProfileFs` produces for a run id. The leaf carries a generated
 * suffix because the real code creates it with `mkdtemp` rather than deriving a guessable name.
 */
function profileDirFor(runId: string): string {
    return path.join(browserProfileRootDir(), `${runId}-${stubbedMkdtempSuffix}`);
}

function assertProperDescendantOfProfileRoot(candidate: string): void {
    const relative = path.relative(browserProfileRootDir(), candidate);
    assert.ok(relative.length > 0, `Expected '${candidate}' to be below '${browserProfileRootDir()}', not equal to it`);
    assert.strictEqual(relative.startsWith(`..${path.sep}`), false, `Expected '${candidate}' to stay below '${browserProfileRootDir()}'`);
    assert.strictEqual(path.isAbsolute(relative), false, `Expected '${candidate}' to stay below '${browserProfileRootDir()}'`);
}

suite('Browser Debugger Tests', () => {
    setup(() => {
        // Installed for every test so none of them can create real directories under the shared OS
        // temp directory. Tests that assert on the calls request the same stubs back.
        stubBrowserProfileFs();
    });

    teardown(() => {
        cleanupRun('run-1');
        sinon.restore();
    });

    test('configures js-debug browser launch with isolated profile and clean-exit flags', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        stubBrowserProfileFs();
        const launchConfig: BrowserLaunchConfiguration = {
            type: 'browser',
            mode: 'Debug',
            url: 'https://localhost:5001',
            web_root: '/workspace/app',
            browser: 'chrome'
        };
        const debugConfig = createResourceDebugConfig();

        await configureBrowserDebugSession(launchConfig, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-chrome');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.url, 'https://localhost:5001');
        assert.strictEqual(debugConfig.webRoot, '/workspace/app');
        assert.strictEqual(debugConfig.sourceMaps, true);
        assert.deepStrictEqual(debugConfig.resolveSourceMapLocations, ['**', '!**/node_modules/**']);
        assert.deepStrictEqual(debugConfig.runtimeArgs, [
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode'
        ]);
        assert.strictEqual(debugConfig.userDataDir, profileDirFor('run-1'));
        assert.strictEqual(debugConfig.debugSessionId, 'dcp-1');
        // The signal is declared by the integration, not written by the callback, so assert it at
        // its source. js-debug is server-hosted and tears down child target sessions on its own, so
        // the root debug session ending is the only reliable run lifetime signal for browsers.
        assert.strictEqual(browserDebuggerExtension.terminationSignal, 'debugSessionEnd');
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
        assert.strictEqual(debugConfig.cwd, undefined);

        cleanupRun('run-1');
        assert.strictEqual(rmStub.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);
    });

    test('defaults to Edge and preserves user runtime args', async () => {
        const launchConfig: BrowserLaunchConfiguration = {
            type: 'browser',
            url: 'https://localhost:5001'
        };
        const debugConfig = createResourceDebugConfig();
        debugConfig.runtimeArgs = ['--custom-flag', '--no-first-run'];

        await configureBrowserDebugSession(launchConfig, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-msedge');
        assert.deepStrictEqual(debugConfig.runtimeArgs, [
            '--custom-flag',
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode'
        ]);
    });

    test('uses the registered cleanup run id for the browser profile directory', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        const debugConfig = createResourceDebugConfig({ runId: 'custom-run-id' });

        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        assert.strictEqual(debugConfig.userDataDir, profileDirFor('custom-run-id'));

        cleanupRun('run-1');
        assert.strictEqual(rmStub.called, false);

        cleanupRun('custom-run-id');
        assert.strictEqual(rmStub.calledOnceWithExactly(profileDirFor('custom-run-id'), expectedRmOptions), true);
    });

    // Path containment. The profile directory is deleted recursively when the run ends, so a run id
    // must never be able to move that delete outside the profile root Aspire owns. `..` is the
    // dangerous case: `.` and `-` are legal in a run id and survive character sanitizing untouched,
    // so the post-creation realpath containment check is the final guard before cleanup is
    // registered.
    const escapingRunIds: { runId: string; description: string }[] = [
        { runId: '..', description: 'parent directory traversal' },
        { runId: '.', description: 'the temp directory itself' },
        { runId: '', description: 'an empty run id' },
        { runId: '../..', description: 'repeated parent traversal' },
        { runId: '.././..', description: 'mixed traversal and current-directory segments' },
        { runId: path.join(os.tmpdir(), 'elsewhere'), description: 'an absolute path inside the temp directory' },
        { runId: '/etc/passwd', description: 'an absolute POSIX path' },
        { runId: 'C:\\Windows\\System32', description: 'an absolute Windows path' },
        { runId: '..\\..\\elsewhere', description: 'Windows separator traversal' },
        { runId: 'a/../../../b', description: 'embedded separator traversal' }
    ];

    for (const { runId, description } of escapingRunIds) {
        test(`keeps the browser profile directory under the profile root for ${description}`, async () => {
            const rmStub = sinon.stub(fs.promises, 'rm').resolves();
            stubBrowserProfileFs();
            const warnStub = sinon.stub(extensionLogOutputChannel, 'warn');
            const debugConfig = createResourceDebugConfig({ runId });

            await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

            const userDataDir = debugConfig.userDataDir as string | undefined;
            if (userDataDir === undefined) {
                assert.ok(
                    warnStub.getCalls().some(call => /without an isolated profile/.test(call.args[0])),
                    'Expected the rejected profile directory to be logged');
            }
            else {
                assertProperDescendantOfProfileRoot(userDataDir);
            }

            cleanupRun(runId);

            for (const call of rmStub.getCalls()) {
                const deleted = call.args[0] as string;
                assertProperDescendantOfProfileRoot(deleted);
            }
        });
    }

    test('creates the browser profile directory under the owned profile root', async () => {
        // A deterministic path can be pre-created as a symlink by another local process and then
        // followed. mkdtemp fails rather than following an existing entry, so creation itself is the
        // race protection.
        const profileFs = stubBrowserProfileFs();
        const debugConfig = createResourceDebugConfig({ runId: 'run-1' });

        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        assert.strictEqual(profileFs.mkdir.calledOnceWithExactly(browserProfileRootDir(), { recursive: true, mode: 0o700 }), true);
        assert.strictEqual(profileFs.lstat.calledOnceWithExactly(browserProfileRootDir()), true);
        assert.strictEqual(profileFs.mkdtemp.calledOnce, true);
        assert.strictEqual(profileFs.mkdtemp.firstCall.args[0], path.join(browserProfileRootDir(), 'run-1-'));
        assert.deepStrictEqual(profileFs.realpath.getCalls().map(call => call.args[0]), [
            browserProfileRootDir(),
            profileDirFor('run-1')
        ]);
        // The directory handed to the browser is the one mkdtemp actually created, not the prefix.
        assert.strictEqual(debugConfig.userDataDir, profileDirFor('run-1'));
    });

    test('refuses an unsafe browser profile root before creating a profile directory', async () => {
        const profileFs = stubBrowserProfileFs();
        const warnStub = sinon.stub(extensionLogOutputChannel, 'warn');
        profileFs.lstat.resolves({
            isDirectory: () => true,
            isSymbolicLink: () => true,
            mode: 0o700,
            uid: typeof process.getuid === 'function' ? process.getuid() : 0,
        } as fs.Stats);
        const debugConfig = createResourceDebugConfig({ runId: 'run-1' });

        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        assert.strictEqual(profileFs.mkdtemp.called, false);
        assert.strictEqual(debugConfig.userDataDir, undefined);
        assert.ok(warnStub.getCalls().some(call => /unsafe browser debug profile root/.test(call.args[0])));
    });

    test('refuses a created profile directory whose real path escapes the profile root', async () => {
        // Defense in depth behind mkdtemp: whatever produced the final path, a recursive delete
        // must never be aimed outside the intended parent.
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        const warnStub = sinon.stub(extensionLogOutputChannel, 'warn');
        const profileFs = stubBrowserProfileFs();
        const createdPath = profileDirFor('run-1');
        profileFs.mkdtemp.resolves(createdPath);
        profileFs.realpath.callsFake(async (candidate: fs.PathLike) =>
            String(candidate) === createdPath ? path.join(os.tmpdir(), 'escaped-profile') : String(candidate));
        const debugConfig = createResourceDebugConfig();

        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        assert.strictEqual(debugConfig.userDataDir, undefined);
        assert.ok(warnStub.getCalls().some(call => /outside/.test(call.args[0])));

        cleanupRun('run-1');
        assert.strictEqual(rmStub.called, false);
    });

    test('ignores workspace debugger settings that try to take over Aspire-owned configuration fields', async () => {
        // `prepareDebugSession` merges the workspace `debuggers.<type>` block into the generated
        // configuration. Every field on the configuration is therefore workspace-writable unless it
        // is re-applied afterwards, and several of them are not user knobs:
        //   - `runId` derives a directory that is later deleted recursively, so `'..'` would aim
        //     that delete at the OS temp directory.
        //   - `debugSessionId` becomes `dcp_id` on DCP wire notifications.
        //   - `terminationSignal` decides who reports the run terminating.
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        stubBrowserProfileFs();
        const prepared = await prepareDebugSession(
            {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                debuggers: {
                    browser: {
                        runId: '..',
                        debugSessionId: 'workspace-supplied-dcp-id',
                        terminationSignal: 'adapterExit',
                        isApphost: true,
                        args: ['--user-supplied']
                    } as never
                }
            },
            { type: 'browser', url: 'https://localhost:5001' } as BrowserLaunchConfiguration,
            [],
            [],
            { debug: true, runId: 'run-1', debugSessionId: 'dcp-1', isApphost: false, debugSession: {} as AspireDebugSession },
            browserDebuggerExtension);

        const configuration = prepared.debugConfiguration;
        assert.strictEqual(configuration.runId, 'run-1');
        assert.strictEqual(configuration.debugSessionId, 'dcp-1');
        assert.strictEqual(configuration.isApphost, false);
        assert.strictEqual(configuration.terminationSignal, 'debugSessionEnd');
        assert.strictEqual(configuration.userDataDir, profileDirFor('run-1'));

        cleanupRun('run-1');
        assert.strictEqual(rmStub.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);
    });

    test('ignores a workspace attempt to rewire the termination signal of a non-browser resource', async () => {
        // The browser extension overwrites several fields itself, which can mask an override. Node
        // touches none of them, so this is the case that proves the guarantee comes from
        // prepareDebugSession rather than from a language callback happening to win the race.
        // A workspace that could set `terminationSignal: 'debugSessionEnd'` here would silence
        // adapterTracker's onExit notification and leave every node run alive forever in DCP.
        const prepared = await prepareDebugSession(
            {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                debuggers: {
                    node: {
                        terminationSignal: 'debugSessionEnd',
                        runId: '../../etc',
                        debugSessionId: 'workspace-supplied-dcp-id'
                    } as never
                }
            },
            { type: 'node', program: '/workspace/app/index.js' } as unknown as ExecutableLaunchConfiguration,
            [],
            [],
            { debug: true, runId: 'node-run', debugSessionId: 'node-dcp', isApphost: false, debugSession: {} as AspireDebugSession },
            nodeDebuggerExtension);

        assert.strictEqual(prepared.debugConfiguration.terminationSignal, 'adapterExit');
        assert.strictEqual(prepared.debugConfiguration.runId, 'node-run');
        assert.strictEqual(prepared.debugConfiguration.debugSessionId, 'node-dcp');
    });

    test('maps Firefox to the VS Code Firefox debug adapter', async () => {
        // The `firefox` adapter is only available when the firefox-devtools.vscode-firefox-debug
        // extension is installed, so stub it as present for this happy-path assertion.
        sinon.stub(vscode.extensions, 'getExtension').callsFake((id: string) =>
            id === 'firefox-devtools.vscode-firefox-debug' ? ({ id } as vscode.Extension<unknown>) : undefined);
        const debugConfig = createResourceDebugConfig();

        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001', browser: 'firefox' }, debugConfig);

        assert.strictEqual(debugConfig.type, 'firefox');
    });

    test('prompts to install the Firefox debugger and fails when the adapter is missing', async () => {
        const getExtensionStub = sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showErrorStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined as any);
        const debugConfig = createResourceDebugConfig();

        await assert.rejects(
            configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001', browser: 'firefox' }, debugConfig),
            /Firefox Debugger extension/);

        assert.ok(getExtensionStub.calledWith('firefox-devtools.vscode-firefox-debug'));
        assert.strictEqual(showErrorStub.calledOnce, true);
        assert.match(showErrorStub.firstCall.args[0], /Firefox Debugger extension/);
    });

    test('installs the Firefox debugger when the user accepts the install prompt', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        sinon.stub(vscode.window, 'showErrorMessage').resolves('Install' as any);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves();
        const debugConfig = createResourceDebugConfig();

        await assert.rejects(
            configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001', browser: 'firefox' }, debugConfig),
            /Firefox Debugger extension/);

        // The prompt is fire-and-forget, so let the resolved showErrorMessage promise settle.
        await Promise.resolve();
        await Promise.resolve();

        assert.ok(executeCommandStub.calledOnceWithExactly('workbench.extensions.installExtension', 'firefox-devtools.vscode-firefox-debug'));
    });

    test('logs the missing URL reason when browser launch configuration is incomplete', async () => {
        const infoStub = sinon.stub(extensionLogOutputChannel, 'info');
        const launchConfig: BrowserLaunchConfiguration = {
            type: 'browser'
        };

        await assert.rejects(configureBrowserDebugSession(launchConfig, createResourceDebugConfig()));

        assert.strictEqual(infoStub.calledOnce, true);
        assert.match(infoStub.firstCall.args[0], /Browser launch configuration did not include a URL/);
    });

    test('sends sessionTerminated and cleans up when the root browser debug session terminates', async () => {
        const harness = new DebugSessionHarness();
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        harness.terminateSession(resourceDebugSession.session);

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);

        harness.dispose();
    });

    test('waits for stopped browser debug session before cleaning profile directory', async () => {
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred' });
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        const stop = Promise.resolve(resourceDebugSession.stopSession());
        await Promise.resolve();

        assert.strictEqual(harness.rm.called, false);

        harness.finishStopDebugging();
        await stop;

        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);

        harness.dispose();
    });

    test('stopSession is awaitable and single-shot for a DCP-requested browser stop', async () => {
        // This is the shape a DCP `DELETE /run_session` drives (microsoft/aspire#19125 schedules the
        // debugger teardown after acknowledging the delete). Three things must hold together:
        //   1. stopSession() resolves only after VS Code finished stopping the browser session, so the
        //      caller can sequence teardown rather than fire-and-forget.
        //   2. exactly one `sessionTerminated` reaches DCP, with no `exit_code` (a requested stop is
        //      not a program exit).
        //   3. repeated stops are memoized, so DCP-requested stop plus extension disposal cannot
        //      terminate the run twice.
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred' });
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        let stopResolved = false;
        const firstStop = Promise.resolve(resourceDebugSession.stopSession()).then(() => { stopResolved = true; });
        const secondStop = resourceDebugSession.stopSession();
        await Promise.resolve();

        assert.strictEqual(harness.stopDebugging.callCount, 1, 'Expected the browser session to be stopped once');
        assert.strictEqual(stopResolved, false, 'Expected stopSession to stay pending until VS Code finished stopping');
        assert.strictEqual(harness.sendNotification.called, false, 'Expected no termination before the stop completed');

        harness.finishStopDebugging();
        await firstStop;
        await secondStop;

        assert.strictEqual(stopResolved, true);
        assert.strictEqual(harness.stopDebugging.callCount, 1, 'Expected the second stop to reuse the in-flight stop');
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);

        harness.dispose();
        assert.strictEqual(harness.sessionTerminatedNotifications().length, 1, 'Expected disposal after a requested stop to stay single-shot');
    });

    test('stopSession is awaitable and single-shot for a non-browser resource session', async () => {
        // Only browser runs use the `debugSessionEnd` signal; the AppHost and every normal resource
        // session go through this same stop path with `adapterExit`. It has to make the same
        // ordering promise, because AspireDebugSession.stopDebugging() stops the AppHost first and only
        // then the Aspire parent — a stop that resolved early would let VS Code's parent session
        // cascade race the AppHost registry refresh.
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred', startedSessionId: 'resource-session-id' });
        const debugConfig = createResourceDebugConfig({ type: 'coreclr', name: 'apiservice' });

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        let stopResolved = false;
        const firstStop = Promise.resolve(resourceDebugSession.stopSession()).then(() => { stopResolved = true; });
        const secondStop = resourceDebugSession.stopSession();
        await Promise.resolve();

        assert.strictEqual(harness.stopDebugging.callCount, 1, 'Expected the resource session to be stopped once');
        assert.strictEqual(stopResolved, false, 'Expected stopSession to stay pending until VS Code finished stopping');

        harness.finishStopDebugging();
        await firstStop;
        await secondStop;

        assert.strictEqual(stopResolved, true);
        assert.strictEqual(harness.stopDebugging.callCount, 1, 'Expected the second stop to reuse the in-flight stop');
        // `adapterExit` runs report termination from the debug adapter's onExit, not from here.
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);

        harness.dispose();
    });

    test('stopSession rejects when VS Code fails to stop a non-browser resource session', async () => {
        // The failure has to reach the caller instead of being logged and dropped. stopDebugging()
        // reports AppHost stop failures to its caller, and a swallowed rejection there would let
        // teardown proceed as though the session had stopped.
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred', startedSessionId: 'resource-session-id' });
        const debugConfig = createResourceDebugConfig({ type: 'coreclr', name: 'apiservice' });

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        const stop = Promise.resolve(resourceDebugSession.stopSession());
        await Promise.resolve();

        harness.failStopDebugging(new Error('VS Code failed to stop the session'));

        await assert.rejects(() => stop, /VS Code failed to stop the session/);
        assert.strictEqual(harness.stopDebugging.callCount, 1);
        // A rejected stop means VS Code never confirmed the session ended, so the run must not be
        // reported as terminated. Claiming termination here would mark the resource stopped in the
        // dashboard while its process is still running.
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);

        harness.dispose();
    });

    test('does not delete the browser profile or report termination when the stop fails', async () => {
        // The browser is potentially still running after a failed stop, and its profile directory is
        // its live working state. Deleting it would corrupt a running browser, so cleanup has to wait
        // for a stop that actually succeeded (or for the session to end on its own).
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred' });
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        const stop = Promise.resolve(resourceDebugSession.stopSession());
        await Promise.resolve();
        harness.failStopDebugging(new Error('VS Code failed to stop the session'));

        await assert.rejects(() => stop, /VS Code failed to stop the session/);
        assert.strictEqual(harness.rm.called, false, 'Expected the profile directory to survive a failed stop');
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);

        // The termination listener has to survive the failure, otherwise a session that later ends
        // for real would never terminate the run in DCP.
        harness.terminateSession(resourceDebugSession.session);

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);

        harness.dispose();
    });

    test('sends sessionTerminated when browser debug session starts after Aspire session disposal', async () => {
        const harness = new DebugSessionHarness({ autoStartSession: false });
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSessionPromise = harness.aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        harness.aspireDebugSession.dispose();

        harness.startSession(createDebugSession('browser-session-id', debugConfig));

        const resourceDebugSession = await resourceDebugSessionPromise;

        assert.strictEqual(resourceDebugSession, undefined);
        assert.strictEqual(harness.stopDebugging.calledWith(sinon.match.has('id', 'browser-session-id')), true);
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);
    });

    test('does not send sessionTerminated for a browser child session from another parent', async () => {
        const harness = new DebugSessionHarness();
        const otherParentDebugSession = createDebugSession('other-browser-session-id', {
            type: 'pwa-msedge',
            request: 'launch',
            name: 'Browser: https://localhost:5001',
        });
        const debugConfig = createResourceDebugConfig({
            terminationSignal: 'debugSessionEnd'
        });

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        harness.terminateSession(createDebugSession('same-name-different-parent-session-id', {
            type: 'pwa-msedge',
            request: 'launch',
            name: 'Browser: https://localhost:5001',
        }, otherParentDebugSession));

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);

        harness.dispose();
    });

    test('does not send sessionTerminated for a transient browser child target', async () => {
        const harness = new DebugSessionHarness();
        const debugConfig = createResourceDebugConfig({
            terminationSignal: 'debugSessionEnd'
        });

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        harness.terminateSession(createDebugSession('js-debug-child-session-id', {
            type: 'pwa-msedge',
            request: 'launch',
            name: 'Page title from browser target',
        }, resourceDebugSession.session));

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);

        harness.terminateSession(resourceDebugSession.session);

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);

        harness.dispose();
    });

    test('skips the termination notification when the run has no DCP session id', async () => {
        // `debugSessionId` is typed nullable and every other DCP notification path in
        // AspireDebugSession skips with a warning when it is missing rather than inventing an id
        // (see trackAlreadyStartedResourceSession). Termination has to agree: a notification
        // addressed to no run is not deliverable, and guessing an id would target another run.
        const harness = new DebugSessionHarness();
        const debugConfig = createResourceDebugConfig({ debugSessionId: null });
        // Configure through the real browser path so the profile-directory cleanup is registered
        // and the assertion below proves cleanup is independent of the notification.
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        harness.terminateSession(resourceDebugSession.session);

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);
        // Cleanup still has to run, otherwise the browser profile directory leaks.
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);

        harness.dispose();
    });

    test('waits for browser debug shutdown before cleaning up a session that starts after disposal', async () => {
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred' });
        harness.onBeforeSessionStarted = () => harness.aspireDebugSession.dispose();
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        let resolved = false;
        const resourceDebugSessionPromise = harness.aspireDebugSession.startAndGetDebugSession(debugConfig).then(result => {
            resolved = true;
            return result;
        });
        await Promise.resolve();
        await Promise.resolve();

        assert.strictEqual(resolved, false);
        assert.strictEqual(harness.sendNotification.called, false);
        assert.strictEqual(harness.rm.called, false);

        harness.finishStopDebugging();
        const resourceDebugSession = await resourceDebugSessionPromise;

        assert.strictEqual(resourceDebugSession, undefined);
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);
    });

    // A failed stop deliberately keeps waiting for a real termination, so something has to bound
    // that wait or a browser that never closes would hold the listener for the life of the
    // extension host. Disposing the owning Aspire session is that bound.
    test('releases the termination listener when the owning session is disposed after a failed stop', async () => {
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred' });
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);
        const resourceDebugSession = await harness.aspireDebugSession.startAndGetDebugSession(debugConfig);
        assert.ok(resourceDebugSession);

        const stop = Promise.resolve(resourceDebugSession.stopSession());
        harness.failStopDebugging(new Error('VS Code failed to stop the session'));
        await assert.rejects(stop);

        // Still armed: a termination arriving before disposal must still finish the run.
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);
        assert.strictEqual(harness.rm.called, false);

        harness.aspireDebugSession.dispose();
        const hadListener = harness.terminateSession(resourceDebugSession.session);

        assert.strictEqual(hadListener, false, 'Expected the termination listener to have been released on disposal');
        // Nothing observed the debuggee ending before the session went away, so the run is not
        // reported as terminated and the profile is left for the OS rather than deleted blindly.
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);
        assert.strictEqual(harness.rm.called, false);
    });

    // The DCP /run_session handler reads `undefined` from startAndGetDebugSession as "the debugger
    // never started" and responds by calling cleanupRun(runId), which recursively deletes the
    // browser profile. A session that started and then failed to stop is still running, so it must
    // not be reported that way or the cleanup would delete a live browser's profile.
    test('returns the session when a start after disposal cannot be stopped', async () => {
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred' });
        harness.onBeforeSessionStarted = () => harness.aspireDebugSession.dispose();
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSessionPromise = harness.aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        await Promise.resolve();

        harness.failStopDebugging(new Error('VS Code failed to stop the session'));
        const resourceDebugSession = await resourceDebugSessionPromise;

        // Handed back rather than undefined, so the caller does not treat this as a failed start.
        assert.ok(resourceDebugSession, 'Expected the started session to be returned after a failed stop');
        assert.strictEqual(resourceDebugSession.session.configuration.runId, 'run-1');
        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), []);
        assert.strictEqual(harness.rm.called, false);

        // The returned handle supports a real retry, and the profile is only cleaned up once the
        // stop actually succeeds.
        const retry = resourceDebugSession.stopSession();
        harness.finishStopDebugging();
        await retry;

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);
    });

    // Even when nothing retries the stop, the run must still be able to complete: a session that
    // ends on its own later is the only remaining path to reporting termination and cleaning up.
    test('finishes a start after disposal when a failed stop is followed by a real termination', async () => {
        const harness = new DebugSessionHarness({ stopDebugging: 'deferred' });
        harness.onBeforeSessionStarted = () => harness.aspireDebugSession.dispose();
        const debugConfig = createResourceDebugConfig();
        await configureBrowserDebugSession({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSessionPromise = harness.aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        await Promise.resolve();

        harness.failStopDebugging(new Error('VS Code failed to stop the session'));
        const resourceDebugSession = await resourceDebugSessionPromise;
        assert.ok(resourceDebugSession);
        assert.strictEqual(harness.rm.called, false);

        harness.terminateSession(resourceDebugSession.session);

        assert.deepStrictEqual(harness.sessionTerminatedNotifications(), [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);
        assert.strictEqual(harness.rm.calledOnceWithExactly(profileDirFor('run-1'), expectedRmOptions), true);
    });

    test('openDashboard debugFirefox launches the Firefox debug configuration', async () => {
        // The dashboard Firefox launch path is distinct from resource-based browser debugging:
        // it builds its own debug configuration in AspireDebugSession.launchDebugBrowser rather
        // than going through browserDebuggerExtension. Stub the Firefox extension as installed so
        // we exercise the happy path instead of the install prompt/fallback.
        sinon.stub(vscode.extensions, 'getExtension').callsFake((id: string) =>
            id === 'firefox-devtools.vscode-firefox-debug' ? ({ id } as vscode.Extension<unknown>) : undefined);
        const harness = new DebugSessionHarness({ autoStartSession: false });
        const openExternalStub = sinon.stub(vscode.env, 'openExternal').resolves(true);

        await harness.aspireDebugSession.openDashboard('https://localhost:5001', 'debugFirefox');

        assert.strictEqual(harness.startDebugging.calledOnce, true);
        assert.strictEqual(openExternalStub.called, false);
        const launchedConfig = harness.startDebugging.firstCall.args[1] as vscode.DebugConfiguration;
        assert.strictEqual(launchedConfig.type, 'firefox');
        assert.strictEqual(launchedConfig.request, 'launch');
        assert.strictEqual(launchedConfig.url, 'https://localhost:5001');
        assert.deepStrictEqual(launchedConfig.pathMappings, []);
        assert.strictEqual(typeof launchedConfig.webRoot, 'string');
        assert.ok((launchedConfig.webRoot as string).length > 0);

        harness.dispose();
    });

    test('openDashboard debugFirefox prompts to install and falls back to the external browser when the adapter is missing', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showErrorStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined as any);
        const harness = new DebugSessionHarness({ autoStartSession: false });
        const openExternalStub = sinon.stub(vscode.env, 'openExternal').resolves(true);

        await harness.aspireDebugSession.openDashboard('https://localhost:5001', 'debugFirefox');

        assert.strictEqual(harness.startDebugging.called, false);
        assert.strictEqual(showErrorStub.calledOnce, true);
        assert.match(showErrorStub.firstCall.args[0], /Firefox Debugger extension/);
        assert.strictEqual(openExternalStub.calledOnce, true);

        harness.dispose();
    });
});
