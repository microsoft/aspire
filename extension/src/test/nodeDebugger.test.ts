import * as assert from 'assert';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { denoAppHostInspectorAttachTimeoutMs, findInspectorWaitingProcessIds, getDenoAppHostLaunchFailureMessage, hasCommandLineArgument, inspectorWaitingProcessSettleTimeoutMs, nodeDebuggerExtension, stopInspectorWaitingProcesses } from '../debugger/languages/node';
import { LaunchedChildProcess, LaunchedChildProcessClock } from '../debugger/launchedChildProcessDiscovery';
import { launchMethodDirect, launchMethodPackageManager } from '../debugger/languages/javascriptRuntime';
import { AspireResourceExtendedDebugConfiguration, NodeLaunchConfiguration } from '../dcp/types';

suite('Node Debugger Tests', () => {
    const resourceCleanups: vscode.Disposable[] = [];
    const fakeAspireDebugSession = {
        registerResourceCleanup: (disposable: vscode.Disposable) => resourceCleanups.push(disposable),
        registerPendingStartCancellation: () => ({ dispose: () => { } }),
        trackPendingResourceCleanup: (_name: string, cleanup: () => Promise<void>) => { void cleanup().catch(() => { }); },
    } as unknown as AspireDebugSession;

    teardown(() => {
        resourceCleanups.splice(0).forEach(disposable => disposable.dispose());
    });

    test('advertises versioned Deno AppHost debugging support', () => {
        assert.ok(getSupportedCapabilities().includes('deno.v1'));
    });

    test('configures js-debug to capture process stdout and stderr', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            script_path: '/workspace/app/server.js',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.outputCapture, 'std');
        assert.strictEqual(debugConfig.cwd, '/workspace/app');
    });

    test('uses runtime arguments for package manager launches', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'npm',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, ['run', 'dev'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-node');
        assert.strictEqual(debugConfig.outputCapture, 'std');
        assert.strictEqual(debugConfig.runtimeExecutable, 'npm');
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', 'dev']);
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
    });

    test('uses a realistic package-manager launch with an explicit launch_method', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'npm',
            working_directory: '/workspace/app',
            launch_method: launchMethodPackageManager
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, ['run', 'dev'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.runtimeExecutable, 'npm');
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', 'dev']);
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
    });

    test('uses a realistic direct launch with an explicit launch_method', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'node',
            script_path: '/workspace/app/server.js',
            working_directory: '/workspace/app',
            launch_method: launchMethodDirect
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.runtimeExecutable, 'node');
        assert.strictEqual(debugConfig.runtimeArgs, undefined);
        assert.strictEqual(debugConfig.program, '/workspace/app/server.js');
        assert.deepStrictEqual(debugConfig.args, []);
    });

    test('opens the Deno inspector when debugging an AppHost', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        const inspectorPort = debugConfig.attachSimplePort;
        assert.ok(typeof inspectorPort === 'number' && inspectorPort > 0);
        assert.strictEqual(debugConfig.runtimeExecutable, 'deno');
        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', `--inspect-wait=127.0.0.1:${inspectorPort}`, '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts']);
        // js-debug's 10s default attach timeout expired on slow Windows runners and left Deno blocked
        // in --inspect-wait. The attach gets a bounded 120s budget that is independent of the CLI's
        // 24h debug startup budget.
        assert.strictEqual(debugConfig.timeout, denoAppHostInspectorAttachTimeoutMs);
        assert.strictEqual(denoAppHostInspectorAttachTimeoutMs, 120000);
    });

    test('preserves an explicitly configured debugger timeout for the Deno AppHost inspector attach', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig = { ...createDebugConfig(), timeout: 300000 };

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '/workspace/app/apphost.mts'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.timeout, 300000);
    });

    for (const configuredConsole of ['integratedTerminal', 'externalTerminal', undefined]) {
        test(`debugs the Deno AppHost in the internal console when console is ${configuredConsole ?? 'unset'}`, async () => {
            const launchConfig: NodeLaunchConfiguration = {
                type: 'node',
                runtime_executable: 'deno',
                working_directory: '/workspace/app'
            };
            const debugConfig: AspireResourceExtendedDebugConfiguration = { ...createDebugConfig(), console: configuredConsole };

            await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-A', '/workspace/app/apphost.mts'],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                debugConfig);

            // A terminal launches the runtime outside the extension host's process tree, where inspector cleanup
            // cannot find it, and bypasses outputCapture.
            assert.strictEqual(debugConfig.console, 'internalConsole');
            assert.strictEqual(debugConfig.outputCapture, 'std');
        });
    }

    test('keeps a configured console for non-AppHost Deno launches', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig: AspireResourceExtendedDebugConfiguration = { ...createDebugConfig(), console: 'integratedTerminal' };

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '/workspace/app/main.ts'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.console, 'integratedTerminal');
    });

    test('does not change the debugger timeout for non-Deno package manager launches', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'npm',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', 'dev'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.timeout, undefined);
    });

    test('identifies the runtime waiting for the Deno AppHost inspector by its exact inspector argument', () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const linuxProcess = (args: string[]) => ({ pid: 2, parentPid: 1, executable: '/usr/bin/deno', command: args.join(' '), commandLineArguments: args });
        const windowsProcess = (command: string) => ({ pid: 2, parentPid: 1, executable: 'C:\\deno\\deno.exe', command });

        assert.strictEqual(hasCommandLineArgument(linuxProcess(['deno', 'run', argument, 'apphost.mts']), argument), true);
        assert.strictEqual(hasCommandLineArgument(linuxProcess(['deno', 'run', `${argument}5`, 'apphost.mts']), argument), false);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run ${argument} apphost.mts`), argument), true);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run "${argument}"`), argument), true);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run ${argument}5 apphost.mts`), argument), false);
        assert.strictEqual(hasCommandLineArgument(windowsProcess(`"C:\\deno\\deno.exe" run x${argument} apphost.mts`), argument), false);
    });

    test('finds every extension host descendant waiting on the Deno AppHost inspector argument', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const windowsProcess = (pid: number, parentPid: number, command: string): LaunchedChildProcess =>
            ({ pid, parentPid, executable: command.split(' ')[0], command });
        const windowsProcesses = [
            windowsProcess(10, 1, 'Code.exe --type=extensionHost'),
            // A deno.cmd shim leaves the argument on both cmd.exe and the deno.exe it starts.
            windowsProcess(20, 10, `C:\Windows\system32\cmd.exe /d /s /c ""C:\npm\deno.cmd" run ${argument} apphost.mts"`),
            windowsProcess(21, 20, `"C:\npm\node_modules\deno\deno.exe" run ${argument} apphost.mts`),
            windowsProcess(30, 10, `"C:\deno\deno.exe" run --inspect-wait=127.0.0.1:12345 other.mts`),
            windowsProcess(40, 1, `"C:\deno\deno.exe" run ${argument} unrelated.mts`),
        ];

        assert.deepStrictEqual(
            await findInspectorWaitingProcessIds(argument, { canTrustListedProcessIdentity: true, listProcesses: async () => windowsProcesses }, 10),
            { matches: [20, 21], unidentified: [] });

        // POSIX listings only carry the process tree, so each descendant's command line is read separately.
        // A failed read is unidentified rather than a non-match; a process found gone is neither.
        const posixArguments = new Map<number, string[]>([[11, ['deno', 'run', argument, 'apphost.mts']], [12, ['node', 'server.js']]]);
        const posixProcesses = [10, 11, 12, 13, 14, 15].map(pid => ({ pid, parentPid: pid === 10 ? 1 : 10, executable: '', command: '' }));
        assert.deepStrictEqual(
            await findInspectorWaitingProcessIds(argument, {
                listProcesses: async () => posixProcesses,
                getProcess: async pid => {
                    if (pid === 13) {
                        throw new Error('timed out');
                    }

                    if (pid === 14) {
                        throw Object.assign(new Error('ENOENT'), { code: 'ENOENT' });
                    }

                    return pid === 15 ? undefined : { pid, parentPid: 10, executable: '', command: '', commandLineArguments: posixArguments.get(pid) };
                },
            }, 10),
            { matches: [11], unidentified: [13] });
    });

    function createFakeClock(): LaunchedChildProcessClock & { sleeps: number[] } {
        let now = 0;
        const sleeps: number[] = [];
        return {
            sleeps,
            now: () => now,
            sleep: async milliseconds => {
                sleeps.push(milliseconds);
                now += milliseconds;
            },
        };
    }

    // A Windows-style listing (trusted identity) of the extension host (pid 10) and its children.
    function createListing(listings: number[][], argument: string) {
        let call = 0;
        return {
            canTrustListedProcessIdentity: true,
            listProcesses: async (): Promise<LaunchedChildProcess[]> => {
                const pids = listings[Math.min(call++, listings.length - 1)];
                return [
                    { pid: 10, parentPid: 1, executable: 'Code.exe', command: 'Code.exe --type=extensionHost' },
                    ...pids.map(pid => ({ pid, parentPid: 10, executable: 'deno.exe', command: `deno.exe run ${argument} apphost.mts` })),
                ];
            },
        };
    }

    test('stops a Deno AppHost that appears in the process listing after cleanup starts', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const clock = createFakeClock();
        const killed: number[] = [];
        // Cancelled before js-debug spawned the runtime: the first listings miss it, then it appears, then it exits.
        const result = await stopInspectorWaitingProcesses(argument, createListing([[], [], [21], []], argument), clock, {
            launcherPid: 10,
            kill: pid => killed.push(pid),
        });

        assert.deepStrictEqual(killed, [21]);
        assert.deepStrictEqual(result, { stopped: [21], running: [], unidentified: [] });
        assert.deepStrictEqual(clock.sleeps, [100, 200, 400]);
    });

    test('bounds the search for a Deno AppHost that never appears and does not report a stop', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const clock = createFakeClock();
        const killed: number[] = [];
        const result = await stopInspectorWaitingProcesses(argument, createListing([[]], argument), clock, {
            launcherPid: 10,
            kill: pid => killed.push(pid),
        });

        assert.deepStrictEqual(killed, []);
        assert.deepStrictEqual(result, { stopped: [], running: [], unidentified: [] });
        assert.strictEqual(clock.now(), inspectorWaitingProcessSettleTimeoutMs);
    });

    test('reports a Deno AppHost that is still running after cleanup instead of claiming it stopped', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const clock = createFakeClock();
        const killed: number[] = [];
        const result = await stopInspectorWaitingProcesses(argument, createListing([[21]], argument), clock, {
            launcherPid: 10,
            settleTimeoutMs: 2_000,
            kill: pid => killed.push(pid),
        });

        assert.deepStrictEqual(killed, [21]);
        assert.deepStrictEqual(result, { stopped: [], running: [21], unidentified: [] });
    });

    test('does not wait for a Deno AppHost once the launch can no longer spawn one', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const clock = createFakeClock();
        let spawnSettled = false;
        const result = await stopInspectorWaitingProcesses(argument, createListing([[]], argument), clock, {
            launcherPid: 10,
            kill: () => { },
            isSpawnSettled: () => spawnSettled,
        });
        // Still waiting while js-debug could spawn it: the whole settle window.
        assert.deepStrictEqual(result, { stopped: [], running: [], unidentified: [] });
        assert.strictEqual(clock.now(), inspectorWaitingProcessSettleTimeoutMs);

        // After js-debug answered the launch request (for example a missing executable), one listing is enough.
        spawnSettled = true;
        const settledClock = createFakeClock();
        assert.deepStrictEqual(
            await stopInspectorWaitingProcesses(argument, createListing([[]], argument), settledClock, { launcherPid: 10, kill: () => { }, isSpawnSettled: () => spawnSettled }),
            { stopped: [], running: [], unidentified: [] });
        assert.deepStrictEqual(settledClock.sleeps, []);
    });

    test('a retried cleanup stops a Deno AppHost an earlier attempt could not confirm', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        // The shim is gone and deno.exe is no longer under the extension host; only the earlier attempt knows it.
        const result = await stopInspectorWaitingProcesses(argument, createShimListing([
            { shim: false, deno: true },
            { shim: false, deno: false },
        ], argument), createFakeClock(), { launcherPid: 10, kill: pid => killed.push(pid), knownProcessIds: [21], isSpawnSettled: () => true });

        assert.deepStrictEqual(killed, [21]);
        assert.deepStrictEqual(result, { stopped: [21], running: [], unidentified: [] });
    });

    function createFailingListing(failures: number, argument: string, pids: number[] = []) {
        let call = 0;
        const listing = createListing([pids], argument);
        return {
            canTrustListedProcessIdentity: true,
            listProcesses: async (): Promise<LaunchedChildProcess[]> => {
                if (call++ < failures) {
                    throw new Error('Get-CimInstance failed');
                }

                return listing.listProcesses();
            },
        };
    }

    test('does not report a Deno AppHost cleanup as successful when the process listing fails', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        // The launch already finished, so an empty listing would end the search, but a failed one must not.
        await assert.rejects(
            stopInspectorWaitingProcesses(argument, createFailingListing(Number.MAX_SAFE_INTEGER, argument), createFakeClock(), {
                launcherPid: 10,
                settleTimeoutMs: 2_000,
                kill: () => { },
                isSpawnSettled: () => true,
            }),
            /Get-CimInstance failed/);

        // A transient failure is retried, and the first successful empty listing ends the search.
        const clock = createFakeClock();
        assert.deepStrictEqual(
            await stopInspectorWaitingProcesses(argument, createFailingListing(1, argument), clock, { launcherPid: 10, kill: () => { }, isSpawnSettled: () => true }),
            { stopped: [], running: [], unidentified: [] });
        assert.deepStrictEqual(clock.sleeps, [100]);
    });

    test('keeps processes from an earlier cleanup attempt unconfirmed without signalling them when the process listing fails', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        // PID 21 may have exited and been reused, so it must not be signalled without a matching listing.
        const result = await stopInspectorWaitingProcesses(argument, createFailingListing(Number.MAX_SAFE_INTEGER, argument), createFakeClock(), {
            launcherPid: 10,
            settleTimeoutMs: 2_000,
            kill: pid => killed.push(pid),
            knownProcessIds: [21],
            isSpawnSettled: () => true,
        });

        assert.deepStrictEqual(result, { stopped: [], running: [21], unidentified: [] });
        assert.deepStrictEqual(killed, []);
    });

    // A POSIX listing of the extension host (pid 10) and its children, which only carries pids and parent pids,
    // so each command line comes from getProcess. `details` decides each getProcess call for the Deno runtime.
    function createPosixListing(listings: number[][], argument: string, details: (call: number) => 'ok' | 'gone' | Error) {
        let listCall = 0;
        let detailCall = 0;
        return {
            listProcesses: async (): Promise<LaunchedChildProcess[]> => {
                const pids = listings[Math.min(listCall++, listings.length - 1)];
                return [{ pid: 10, parentPid: 1, executable: '', command: '' }, ...pids.map(pid => ({ pid, parentPid: 10, executable: '', command: '' }))];
            },
            getProcess: async (pid: number): Promise<LaunchedChildProcess | undefined> => {
                const outcome = details(detailCall++);
                if (outcome instanceof Error) {
                    throw outcome;
                }

                return outcome === 'gone'
                    ? undefined
                    : { pid, parentPid: 10, executable: '/usr/bin/deno', command: '', commandLineArguments: ['deno', 'run', argument, 'apphost.mts'] };
            },
        };
    }

    test('does not treat a failed POSIX process-detail lookup as the Deno AppHost having exited', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        // The listing keeps showing pid 21, but its command line can never be read, so it cannot be matched or
        // signalled. The launch already finished, so an empty match would otherwise end cleanup as a success.
        const result = await stopInspectorWaitingProcesses(argument, createPosixListing([[21]], argument, () => new Error('Unable to resolve the running application process.')), createFakeClock(), {
            launcherPid: 10,
            settleTimeoutMs: 2_000,
            kill: pid => killed.push(pid),
            isSpawnSettled: () => true,
        });

        assert.deepStrictEqual(killed, []);
        assert.deepStrictEqual(result, { stopped: [], running: [], unidentified: [21] });
    });

    test('retries a failed POSIX process-detail lookup and stops the Deno AppHost once it is identified', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        const clock = createFakeClock();
        const result = await stopInspectorWaitingProcesses(
            argument,
            createPosixListing([[21], [21], [21], []], argument, call => call === 0 ? new Error('timed out') : 'ok'),
            clock,
            { launcherPid: 10, kill: pid => killed.push(pid), isSpawnSettled: () => true });

        assert.deepStrictEqual(killed, [21]);
        assert.deepStrictEqual(result, { stopped: [21], running: [], unidentified: [] });
        assert.deepStrictEqual(clock.sleeps, [100, 200]);
    });

    test('does not treat the exited ps helper from each POSIX listing as an unidentified process', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        let listCall = 0;
        let denoRunning = true;
        // Like `ps -axo pid=,ppid=` on macOS: every listing includes its own short-lived ps child of the extension
        // host, which has exited before its details are read, so `ps -p` fails with a generic error rather than ENOENT.
        const query = {
            listProcesses: async (): Promise<LaunchedChildProcess[]> => [
                { pid: 10, parentPid: 1, executable: '', command: '' },
                ...(denoRunning ? [{ pid: 21, parentPid: 10, executable: '', command: '' }] : []),
                { pid: 1000 + listCall++, parentPid: 10, executable: '', command: '' },
            ],
            getProcess: async (pid: number): Promise<LaunchedChildProcess | undefined> => {
                if (pid >= 1000) {
                    throw new Error('Command \'ps\' exited with code 1.');
                }

                return { pid, parentPid: 10, executable: '/usr/local/bin/deno', command: '', commandLineArguments: ['deno', 'run', argument, 'apphost.mts'] };
            },
        };

        const result = await stopInspectorWaitingProcesses(argument, query, createFakeClock(), {
            launcherPid: 10,
            kill: pid => {
                killed.push(pid);
                denoRunning = false;
            },
            isSpawnSettled: () => true,
        });

        assert.deepStrictEqual(killed, [21]);
        assert.deepStrictEqual(result, { stopped: [21], running: [], unidentified: [] });
    });

    test('stops a Deno AppHost that appears while the launch finishes during a POSIX lookup', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        let listCall = 0;
        let launchFinished = false;
        let denoState: 'not-spawned' | 'running' | 'exited' = 'not-spawned';
        const query = {
            listProcesses: async (): Promise<LaunchedChildProcess[]> => [
                { pid: 10, parentPid: 1, executable: '', command: '' },
                ...(denoState === 'running' ? [{ pid: 21, parentPid: 10, executable: '', command: '' }] : []),
                { pid: 1000 + listCall++, parentPid: 10, executable: '', command: '' },
            ],
            getProcess: async (pid: number): Promise<LaunchedChildProcess | undefined> => {
                if (pid >= 1000) {
                    // js-debug spawns Deno and answers the launch request while the exited ps helper is looked up.
                    if (denoState === 'not-spawned') {
                        denoState = 'running';
                        launchFinished = true;
                    }

                    throw new Error('Command \'ps\' exited with code 1.');
                }

                return { pid, parentPid: 10, executable: '/usr/bin/deno', command: '', commandLineArguments: ['deno', 'run', argument, 'apphost.mts'] };
            },
        };

        const result = await stopInspectorWaitingProcesses(argument, query, createFakeClock(), {
            launcherPid: 10,
            kill: pid => {
                killed.push(pid);
                denoState = 'exited';
            },
            isSpawnSettled: () => launchFinished,
        });

        assert.deepStrictEqual(killed, [21]);
        assert.deepStrictEqual(result, { stopped: [21], running: [], unidentified: [] });
    });

    test('keeps following an unidentified POSIX runtime after its stopped wrapper reparents it', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        let wrapperRunning = true;
        let denoRunning = true;
        let denoLookups = 0;
        const query = {
            listProcesses: async (): Promise<LaunchedChildProcess[]> => [
                { pid: 10, parentPid: 1, executable: '', command: '' },
                ...(wrapperRunning ? [{ pid: 20, parentPid: 10, executable: '', command: '' }] : []),
                // Once the wrapper is stopped, init adopts Deno, so it is no longer under the extension host or the wrapper.
                ...(denoRunning ? [{ pid: 21, parentPid: wrapperRunning ? 20 : 1, executable: '', command: '' }] : []),
            ],
            getProcess: async (pid: number): Promise<LaunchedChildProcess | undefined> => {
                if (pid === 21 && denoLookups++ === 0) {
                    throw new Error('timed out');
                }

                return { pid, parentPid: 10, executable: pid === 20 ? '/bin/sh' : '/usr/bin/deno', command: '', commandLineArguments: [pid === 20 ? 'deno-wrapper' : 'deno', 'run', argument, 'apphost.mts'] };
            },
        };

        const result = await stopInspectorWaitingProcesses(argument, query, createFakeClock(), {
            launcherPid: 10,
            kill: pid => {
                killed.push(pid);
                if (pid === 20) {
                    wrapperRunning = false;
                }
                else {
                    denoRunning = false;
                }
            },
            isSpawnSettled: () => true,
        });

        assert.deepStrictEqual(killed, [20, 21]);
        assert.deepStrictEqual(result, { stopped: [20, 21], running: [], unidentified: [] });
    });

    test('confirms a Deno AppHost exited when its POSIX process details are gone', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const missing = Object.assign(new Error('ENOENT: no such file or directory, readlink \'/proc/21/exe\''), { code: 'ENOENT' });
        for (const details of [() => missing, () => 'gone' as const]) {
            const clock = createFakeClock();
            // Listed, but it exited before its details were read: a later listing is not needed to confirm it.
            assert.deepStrictEqual(
                await stopInspectorWaitingProcesses(argument, createPosixListing([[21]], argument, details), clock, { launcherPid: 10, kill: () => { }, isSpawnSettled: () => true }),
                { stopped: [], running: [], unidentified: [] });
            assert.deepStrictEqual(clock.sleeps, []);
        }

        // A pid that is no longer listed has exited, even though its earlier lookup failed.
        assert.deepStrictEqual(
            await stopInspectorWaitingProcesses(argument, createPosixListing([[21], []], argument, () => new Error('timed out')), createFakeClock(), { launcherPid: 10, kill: () => { }, isSpawnSettled: () => true }),
            { stopped: [], running: [], unidentified: [] });
    });

    // A Windows listing where a deno.cmd shim (pid 20, a child of the extension host, pid 10) started deno.exe (pid 21).
    function createShimListing(listings: { shim: boolean; deno: boolean }[], argument: string) {
        let call = 0;
        return {
            canTrustListedProcessIdentity: true,
            listProcesses: async (): Promise<LaunchedChildProcess[]> => {
                const { shim, deno } = listings[Math.min(call++, listings.length - 1)];
                return [
                    { pid: 10, parentPid: 1, executable: 'Code.exe', command: 'Code.exe --type=extensionHost' },
                    ...(shim ? [{ pid: 20, parentPid: 10, executable: 'cmd.exe', command: `cmd.exe /d /s /c "deno.cmd run ${argument} apphost.mts"` }] : []),
                    // Once the shim exits, deno.exe still reports it as its parent but is no longer in the extension host's tree.
                    ...(deno ? [{ pid: 21, parentPid: 20, executable: 'deno.exe', command: `deno.exe run ${argument} apphost.mts` }] : []),
                ];
            },
        };
    }

    test('keeps confirming a Deno AppHost whose Windows shim exited before it did', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const clock = createFakeClock();
        const killed: number[] = [];
        const result = await stopInspectorWaitingProcesses(argument, createShimListing([
            { shim: true, deno: true },
            { shim: false, deno: true },
            { shim: false, deno: false },
        ], argument), clock, { launcherPid: 10, kill: pid => killed.push(pid) });

        assert.deepStrictEqual(killed, [20, 21]);
        assert.deepStrictEqual(result, { stopped: [20, 21], running: [], unidentified: [] });
        assert.deepStrictEqual(clock.sleeps, [100, 200]);

        // If deno.exe never exits, it is reported as still running rather than as stopped.
        const stuck = await stopInspectorWaitingProcesses(argument, createShimListing([
            { shim: true, deno: true },
            { shim: false, deno: true },
        ], argument), createFakeClock(), { launcherPid: 10, settleTimeoutMs: 2_000, kill: () => { } });
        assert.deepStrictEqual(stuck, { stopped: [20], running: [21], unidentified: [] });
    });

    test('stops a Deno AppHost that first appears after its Windows shim exited', async () => {
        const argument = '--inspect-wait=127.0.0.1:1234';
        const killed: number[] = [];
        const result = await stopInspectorWaitingProcesses(argument, createShimListing([
            { shim: true, deno: false },
            { shim: false, deno: true },
            { shim: false, deno: false },
        ], argument), createFakeClock(), { launcherPid: 10, kill: pid => killed.push(pid) });

        assert.deepStrictEqual(killed, [20, 21]);
        assert.deepStrictEqual(result, { stopped: [20, 21], running: [], unidentified: [] });
    });

    test('only reports an inspector readiness timeout when the launch ran for the whole timeout', () => {
        const reason = 'Could not connect to debug target at http://127.0.0.1:1234: Could not find any debuggable target';
        const timedOut = getDenoAppHostLaunchFailureMessage('127.0.0.1:1234', 120_000, 120_004, reason);
        assert.ok(timedOut.includes('127.0.0.1:1234') && timedOut.includes('120 seconds') && timedOut.endsWith(reason), timedOut);
        assert.ok(!/stopped/i.test(timedOut), timedOut);

        const missingExecutable = getDenoAppHostLaunchFailureMessage('127.0.0.1:1234', 120_000, 15, 'Can\'t find Node.js binary "deno"');
        assert.strictEqual(missingExecutable, 'The debugger could not launch the Deno AppHost: Can\'t find Node.js binary "deno"');
    });

    test('does not open the Deno inspector when running an AppHost without debugging', async () => {
        const launchConfig: NodeLaunchConfiguration = {
            type: 'node',
            runtime_executable: 'deno',
            working_directory: '/workspace/app'
        };
        const debugConfig = createDebugConfig();

        await nodeDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts'],
            [],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.deepStrictEqual(debugConfig.runtimeArgs, ['run', '-A', '--unstable-sloppy-imports', '/workspace/app/apphost.mts']);
        assert.strictEqual(debugConfig.attachSimplePort, undefined);
        assert.strictEqual(debugConfig.timeout, undefined);
    });
});

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'node',
        name: 'Node',
        request: 'launch',
        program: '/workspace/app/server.js',
        args: []
    };
}
