/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../debugger/languages/cli';
import { AppHostDiscoveryService, findCandidateForEditorFile, findConfiguredAppHostPaths, getDebugTargetForCandidate, selectWorkspaceAppHostPath } from '../utils/appHostDiscovery';
import type { AppHostDiscoverySnapshot } from '../utils/appHostDiscovery';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import type { ConfigInfoProvider } from '../utils/configInfoProvider';
import { appHostDiscoveryFindFilesMaxResults } from '../utils/workspaceFileSearch';

suite('AppHost discovery', () => {
    test('resolves SDK-style C# AppHost source file to discovered project candidate', () => {
        const appHostProjectPath = buildPath('workspace', 'AppHost', 'AppHost.csproj');
        const programPath = buildPath('workspace', 'AppHost', 'Program.cs');

        const candidate = findCandidateForEditorFile(programPath, [{
            path: appHostProjectPath,
            language: 'csharp',
            status: 'buildable',
        }]);

        assert.strictEqual(candidate?.path, appHostProjectPath);
        assert.strictEqual(candidate ? getDebugTargetForCandidate(candidate) : undefined, appHostProjectPath);
    });

    test('keeps file-based C# AppHost candidate as source file', () => {
        const appHostPath = buildPath('workspace', 'AppHost', 'apphost.cs');

        const candidate = findCandidateForEditorFile(appHostPath, [{
            path: appHostPath,
            language: 'csharp',
            status: 'buildable',
        }]);

        assert.strictEqual(candidate?.path, appHostPath);
        assert.strictEqual(candidate ? getDebugTargetForCandidate(candidate) : undefined, appHostPath);
    });

    test('keeps TypeScript AppHost candidate as source file', () => {
        const appHostPath = buildPath('workspace', 'AppHost', 'apphost.ts');

        const candidate = findCandidateForEditorFile(appHostPath, [{
            path: appHostPath,
            language: 'typescript/nodejs',
            status: 'buildable',
        }]);

        assert.strictEqual(candidate?.path, appHostPath);
        assert.strictEqual(candidate ? getDebugTargetForCandidate(candidate) : undefined, appHostPath);
    });

    test('returns undefined when no discovered candidate contains C# source file', () => {
        const programPath = buildPath('workspace', 'Web', 'Program.cs');

        const candidate = findCandidateForEditorFile(programPath, [{
            path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
            language: 'csharp',
            status: 'buildable',
        }]);

        assert.strictEqual(candidate, undefined);
    });

    test('does not map source file to non-C# project candidate', () => {
        const programPath = buildPath('workspace', 'AppHost', 'Program.cs');

        const candidate = findCandidateForEditorFile(programPath, [{
            path: buildPath('workspace', 'AppHost', 'apphost.ts'),
            language: 'typescript/nodejs',
            status: 'buildable',
        }]);

        assert.strictEqual(candidate, undefined);
    });

    test('maps C# file in AppHost project directory to discovered project candidate', () => {
        const helperPath = buildPath('workspace', 'AppHost', 'Helper.cs');

        const candidate = findCandidateForEditorFile(helperPath, [{
            path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
            language: 'csharp',
            status: 'buildable',
        }]);

        assert.strictEqual(candidate?.path, buildPath('workspace', 'AppHost', 'AppHost.csproj'));
    });

    test('does not map C# file under bin directory to discovered project candidate', () => {
        const generatedPath = buildPath('workspace', 'AppHost', 'bin', 'Debug', 'net10.0', 'Generated.cs');

        const candidate = findCandidateForEditorFile(generatedPath, [{
            path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
            language: 'csharp',
            status: 'buildable',
        }]);

        assert.strictEqual(candidate, undefined);
    });

    suite('service', () => {
        let sandbox: sinon.SinonSandbox;
        let findFilesStub: sinon.SinonStub;

        setup(() => {
            sandbox = sinon.createSandbox();
            findFilesStub = sandbox.stub(vscode.workspace, 'findFiles').resolves([]);
        });

        teardown(() => {
            sandbox.restore();
        });

        test('does not force refresh discovery after cached negative editor lookup', async () => {
            stubFileSystemWatchers(sandbox);
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
                const firstResult = await service.tryFindCandidateForEditorFile(buildPath('workspace', 'Web', 'Program.cs'), workspaceFolder);
                const secondResult = await service.tryFindCandidateForEditorFile(buildPath('workspace', 'Web', 'Program.cs'), workspaceFolder);

                assert.strictEqual(firstResult, undefined);
                assert.strictEqual(secondResult, undefined);
                assert.strictEqual(spawnStub.callCount, 1);
            }
            finally {
                service.dispose();
            }
        });

        test('fires change event and invalidates cache when watched files change', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            let changedWorkspaceFolder: vscode.WorkspaceFolder | undefined;
            const subscription = service.onDidChangeCandidates(folder => {
                changedWorkspaceFolder = folder;
            });

            try {
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0]();
                assert.strictEqual(changedWorkspaceFolder, undefined);
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 2);

                await clock.tickAsync(250);
                assert.strictEqual(changedWorkspaceFolder, workspaceFolder);

                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 2);
            }
            finally {
                subscription.dispose();
                service.dispose();
            }
        });

        test('ignores watched files in excluded directories', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            let changeCount = 0;
            const subscription = service.onDidChangeCandidates(() => {
                changeCount++;
            });

            try {
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0](vscode.Uri.file(buildPath('workspace', 'AppHost', 'bin', 'Debug', 'Generated.csproj')));
                assert.strictEqual(changeCount, 0);
                watcherCallbacks[0](vscode.Uri.file(buildPath('workspace', '.worktrees', 'feature', 'AppHost', 'AppHost.csproj')));
                assert.strictEqual(changeCount, 0);
                watcherCallbacks[0](vscode.Uri.file(buildPath('workspace', '.claude', 'worktrees', 'feature', 'AppHost', 'AppHost.csproj')));
                assert.strictEqual(changeCount, 0);

                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);
            }
            finally {
                subscription.dispose();
                service.dispose();
            }
        });

        test('ignores watched files matched by user exclude patterns', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            sandbox.stub(vscode.workspace, 'getConfiguration').callsFake((section?: string) => ({
                get: () => section === 'files'
                    ? { '**/private-checkouts/**': true }
                    : {},
            } as unknown as vscode.WorkspaceConfiguration));
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            let changeCount = 0;
            const subscription = service.onDidChangeCandidates(() => {
                changeCount++;
            });

            try {
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0](vscode.Uri.file(buildPath('workspace', 'private-checkouts', 'feature', 'AppHost', 'AppHost.csproj')));
                await clock.tickAsync(250);

                assert.strictEqual(changeCount, 0);
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);
            }
            finally {
                subscription.dispose();
                service.dispose();
            }
        });

        test('ignores watched files matched by bracket user exclude patterns', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            sandbox.stub(vscode.workspace, 'getConfiguration').callsFake((section?: string) => ({
                get: () => section === 'files'
                    ? { '**/[Pp]rivate-checkouts/**': true }
                    : {},
            } as unknown as vscode.WorkspaceConfiguration));
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            let changeCount = 0;
            const subscription = service.onDidChangeCandidates(() => {
                changeCount++;
            });

            try {
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0](vscode.Uri.file(buildPath('workspace', 'private-checkouts', 'feature', 'AppHost', 'AppHost.csproj')));
                await clock.tickAsync(250);

                assert.strictEqual(changeCount, 0);
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);
            }
            finally {
                subscription.dispose();
                service.dispose();
            }
        });

        test('malformed bracket user exclude patterns do not break watcher invalidation', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            sandbox.stub(vscode.workspace, 'getConfiguration').callsFake((section?: string) => ({
                get: () => section === 'files'
                    ? { '**/[z-a]/**': true }
                    : {},
            } as unknown as vscode.WorkspaceConfiguration));
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            let changeCount = 0;
            const subscription = service.onDidChangeCandidates(() => {
                changeCount++;
            });

            try {
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0](vscode.Uri.file(buildPath('workspace', 'AppHost', 'AppHost.csproj')));
                await clock.tickAsync(250);

                assert.strictEqual(changeCount, 1);
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 2);
            }
            finally {
                subscription.dispose();
                service.dispose();
            }
        });

        test('negated bracket user exclude patterns do not match path separators', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            sandbox.stub(vscode.workspace, 'getConfiguration').callsFake((section?: string) => ({
                get: () => section === 'files'
                    ? { '**[!x]AppHost.csproj': true }
                    : {},
            } as unknown as vscode.WorkspaceConfiguration));
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            let changeCount = 0;
            const subscription = service.onDidChangeCandidates(() => {
                changeCount++;
            });

            try {
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0](vscode.Uri.file(buildPath('workspace', 'x', 'AppHost.csproj')));
                await clock.tickAsync(250);

                assert.strictEqual(changeCount, 1);
                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 2);
            }
            finally {
                subscription.dispose();
                service.dispose();
            }
        });

        test('kills in-flight CLI process when disposed', async () => {
            stubFileSystemWatchers(sandbox);
            const childProcess = {
                killed: false,
                kill: sandbox.stub().callsFake(() => {
                    childProcess.killed = true;
                    return true;
                }),
            };
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').returns(childProcess as any);
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            const discovery = service.discover(workspaceFolder);
            await waitForMicrotasks();

            service.dispose();

            await assert.rejects(discovery, /disposed/);
            assert.strictEqual(spawnStub.callCount, 1);
            assert.strictEqual(childProcess.kill.callCount, 1);
            assert.strictEqual(childProcess.killed, true);
        });

        test('caller cancellation does not reject shared discovery for other callers', async () => {
            stubFileSystemWatchers(sandbox);
            let options: cliModule.SpawnProcessOptions | undefined;
            const childProcess = {
                killed: false,
                kill: sandbox.stub().callsFake(() => {
                    childProcess.killed = true;
                    return true;
                }),
            };
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, spawnOptions) => {
                options = spawnOptions;
                return childProcess as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const cancellationSource = new vscode.CancellationTokenSource();

            try {
                const cancelledDiscovery = service.discover(workspaceFolder, false, cancellationSource.token);
                const sharedDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.ok(options);

                cancellationSource.cancel();

                await assert.rejects(cancelledDiscovery, /cancelled/);
                assert.strictEqual(childProcess.kill.callCount, 0);

                options.stdoutCallback?.(JSON.stringify([{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]));
                options.exitCallback?.(0);

                assert.deepStrictEqual(await sharedDiscovery, [{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]);
            }
            finally {
                cancellationSource.dispose();
                service.dispose();
            }
        });

        test('already cancelled caller token does not start discovery on cache miss', async () => {
            stubFileSystemWatchers(sandbox);
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const cancellationSource = new vscode.CancellationTokenSource();
            cancellationSource.cancel();

            try {
                await assert.rejects(service.discover(workspaceFolder, false, cancellationSource.token), /cancelled/);
                assert.strictEqual(spawnStub.callCount, 0);

                const result = await service.discover(workspaceFolder);
                assert.deepStrictEqual(result, []);
                assert.strictEqual(spawnStub.callCount, 1);
            }
            finally {
                cancellationSource.dispose();
                service.dispose();
            }
        });

        test('times out hung CLI process and allows retry', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(vscode.workspace, 'getConfiguration').returns({
                get: <T>(key: string, defaultValue: T) => key === 'appHostDiscoveryTimeoutMs' ? 5000 as T : defaultValue,
            } as vscode.WorkspaceConfiguration);
            const clock = sandbox.useFakeTimers();
            const killedArgs: string[][] = [];
            let hangCli = true;
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                const childProcess = {
                    killed: false,
                    kill: sandbox.stub().callsFake(() => {
                        childProcess.killed = true;
                        killedArgs.push(args);
                        return true;
                    }),
                };
                if (!hangCli) {
                    options?.stdoutCallback?.('[]');
                    options?.exitCallback?.(0);
                }
                return childProcess as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const discovery = service.discover(workspaceFolder);
                await waitForMicrotasks();

                await clock.tickAsync(5_000);
                await waitForMicrotasks();
                await clock.tickAsync(5_000);

                await assert.rejects(discovery, /timed out after 5 seconds/);
                assert.deepStrictEqual(killedArgs, [
                    ['ls', '--format', 'json'],
                    ['extension', 'get-apphosts'],
                ]);

                hangCli = false;
                const retryResult = await service.discover(workspaceFolder);
                assert.deepStrictEqual(retryResult, []);
                assert.strictEqual(spawnStub.callCount, 3);
            }
            finally {
                service.dispose();
                clock.restore();
            }
        });

        test('keeps valid aspire ls candidates when future entries have unexpected shape', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([
                    {
                        path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', 'Future', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 42,
                        extraMetadata: true,
                    },
                ]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.deepStrictEqual(result, [{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]);
            }
            finally {
                service.dispose();
            }
        });

        test('streams aspire ls candidates before command completion when capability is advertised', async () => {
            stubFileSystemWatchers(sandbox);
            const configInfoProvider = makeConfigInfoProvider(true);
            let lineCallback: ((line: string) => void) | undefined;
            let exitCallback: ((code: number | null) => void) | undefined;
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                lineCallback = options?.lineCallback;
                exitCallback = options?.exitCallback;
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider(), configInfoProvider);

            try {
                const snapshots: AppHostDiscoverySnapshot[] = [];
                const streamTask = collectSnapshots(service.discoverStream(makeWorkspaceFolder(buildPath('workspace'))), snapshots);
                await waitForMicrotasks();

                assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ls', '--format', 'json', '--stream']);
                assert.ok(lineCallback);
                assert.ok(exitCallback);

                lineCallback(JSON.stringify({
                    path: buildPath('workspace', 'First', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }));
                await waitForMicrotasks();

                assert.strictEqual(snapshots.length, 1);
                assert.strictEqual(snapshots[0].isComplete, false);
                assert.deepStrictEqual(snapshots[0].candidates.map(candidate => candidate.path), [
                    buildPath('workspace', 'First', 'AppHost.csproj'),
                ]);

                lineCallback(JSON.stringify({
                    path: buildPath('workspace', 'Second', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }));
                exitCallback(0);
                const finalCandidates = await streamTask;

                assert.deepStrictEqual(finalCandidates.map(candidate => candidate.path), [
                    buildPath('workspace', 'First', 'AppHost.csproj'),
                    buildPath('workspace', 'Second', 'AppHost.csproj'),
                ]);
                assert.strictEqual(snapshots.at(-1)?.isComplete, true);
            }
            finally {
                service.dispose();
            }
        });

        test('shares in-flight streaming discovery with promise discovery', async () => {
            stubFileSystemWatchers(sandbox);
            const configInfoProvider = makeConfigInfoProvider(true);
            let lineCallback: ((line: string) => void) | undefined;
            let exitCallback: ((code: number | null) => void) | undefined;
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                lineCallback = options?.lineCallback;
                exitCallback = options?.exitCallback;
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider(), configInfoProvider);
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const snapshots: AppHostDiscoverySnapshot[] = [];
                const streamTask = collectSnapshots(service.discoverStream(workspaceFolder), snapshots);
                const discoverTask = service.discover(workspaceFolder);
                await waitForMicrotasks();

                lineCallback?.(JSON.stringify({
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }));
                exitCallback?.(0);

                const [streamResult, discoverResult] = await Promise.all([streamTask, discoverTask]);
                assert.strictEqual(spawnStub.callCount, 1);
                assert.deepStrictEqual(streamResult, discoverResult);
                assert.strictEqual(snapshots.length, 2);
            }
            finally {
                service.dispose();
            }
        });

        test('starts streaming discovery when stream attaches after promise-only discovery', async () => {
            stubFileSystemWatchers(sandbox);
            const configInfoProvider = makeConfigInfoProvider(true);
            const spawnedDiscoveries: Array<{ args: string[]; options: any }> = [];
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                spawnedDiscoveries.push({ args, options });
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider(), configInfoProvider);
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const discoverTask = service.discover(workspaceFolder);
                await waitForMicrotasks();

                const snapshots: AppHostDiscoverySnapshot[] = [];
                const streamTask = collectSnapshots(service.discoverStream(workspaceFolder), snapshots);
                await waitForMicrotasks();

                assert.deepStrictEqual(spawnedDiscoveries.map(discovery => discovery.args), [
                    ['ls', '--format', 'json'],
                    ['ls', '--format', 'json', '--stream'],
                ]);
                assert.ok(spawnedDiscoveries[1].options.lineCallback);
                assert.ok(spawnedDiscoveries[1].options.exitCallback);

                spawnedDiscoveries[0].options.stdoutCallback(JSON.stringify([{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]));
                spawnedDiscoveries[0].options.exitCallback(0);

                spawnedDiscoveries[1].options.lineCallback(JSON.stringify({
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }));
                await waitForMicrotasks();

                assert.strictEqual(snapshots.length, 1);
                assert.strictEqual(snapshots[0].isComplete, false);

                spawnedDiscoveries[1].options.exitCallback(0);

                const [discoverResult, streamResult] = await Promise.all([discoverTask, streamTask]);
                assert.strictEqual(spawnStub.callCount, 2);
                assert.deepStrictEqual(discoverResult, streamResult);
                assert.strictEqual(snapshots.at(-1)?.isComplete, true);
            }
            finally {
                service.dispose();
            }
        });

        test('keeps active streaming discovery cached when replaced promise-only discovery fails', async () => {
            stubFileSystemWatchers(sandbox);
            const configInfoProvider = makeConfigInfoProvider(true);
            const spawnedDiscoveries: Array<{ args: string[]; options: any }> = [];
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                spawnedDiscoveries.push({ args, options });
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider(), configInfoProvider);
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const discoverTask = service.discover(workspaceFolder);
                await waitForMicrotasks();

                const firstSnapshots: AppHostDiscoverySnapshot[] = [];
                const firstStreamTask = collectSnapshots(service.discoverStream(workspaceFolder), firstSnapshots);
                await waitForMicrotasks();

                assert.strictEqual(spawnStub.callCount, 2);

                spawnedDiscoveries[0].options.stderrCallback('first discovery failed');
                spawnedDiscoveries[0].options.exitCallback(1);
                await waitForMicrotasks();
                assert.deepStrictEqual(spawnedDiscoveries[2].args, ['extension', 'get-apphosts']);
                spawnedDiscoveries[2].options.stderrCallback('legacy discovery failed');
                spawnedDiscoveries[2].options.exitCallback(1);
                await assert.rejects(discoverTask, /first discovery failed/);

                const secondSnapshots: AppHostDiscoverySnapshot[] = [];
                const secondStreamTask = collectSnapshots(service.discoverStream(workspaceFolder), secondSnapshots);
                await waitForMicrotasks();

                assert.strictEqual(spawnStub.callCount, 3);

                spawnedDiscoveries[1].options.lineCallback(JSON.stringify({
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }));
                await waitForMicrotasks();
                spawnedDiscoveries[1].options.exitCallback(0);

                const [firstStreamResult, secondStreamResult] = await Promise.all([firstStreamTask, secondStreamTask]);
                assert.deepStrictEqual(firstStreamResult, secondStreamResult);
                assert.deepStrictEqual(firstSnapshots.at(-1)?.candidates, secondSnapshots.at(-1)?.candidates);
            }
            finally {
                service.dispose();
            }
        });

        test('uses buffered aspire ls when streaming capability is not advertised', async () => {
            stubFileSystemWatchers(sandbox);
            const configInfoProvider = makeConfigInfoProvider(false);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider(), configInfoProvider);

            try {
                const snapshots: AppHostDiscoverySnapshot[] = [];
                const finalCandidates = await collectSnapshots(service.discoverStream(makeWorkspaceFolder(buildPath('workspace'))), snapshots);

                assert.deepStrictEqual(finalCandidates, [{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]);
                assert.deepStrictEqual(snapshots.map(snapshot => snapshot.isComplete), [true]);
            }
            finally {
                service.dispose();
            }
        });

        test('reports both aspire ls and legacy fallback errors when discovery fails', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                options?.stderrCallback?.(`${args.join(' ')} failed`);
                options?.exitCallback?.(1);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                await assert.rejects(
                    service.discover(makeWorkspaceFolder(buildPath('workspace'))),
                    /aspire ls discovery failed: ls --format json failed\naspire extension get-apphosts fallback failed: extension get-apphosts failed/);
            }
            finally {
                service.dispose();
            }
        });

        test('falls back to project files when malformed config blocks CLI discovery', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const appHostProjectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
                fs.mkdirSync(path.dirname(appHostProjectPath), { recursive: true });
                fs.writeFileSync(appHostProjectPath, `<Project Sdk="Aspire.AppHost.Sdk/13.5.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('*.csproj') ? [vscode.Uri.file(appHostProjectPath)] : [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                    options?.stderrCallback?.(`The configuration file '${path.join(tempDir, 'aspire.config.json')}' contains invalid JSON.`);
                    options?.exitCallback?.(1);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result, [{
                        path: vscode.Uri.file(appHostProjectPath).fsPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('uses VS Code file system when falling back to project files', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const appHostProjectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
                const appHostProjectUri = vscode.Uri.file(appHostProjectPath);
                fs.mkdirSync(path.dirname(appHostProjectPath), { recursive: true });
                fs.writeFileSync(appHostProjectPath, `<Project Sdk="Aspire.AppHost.Sdk/13.5.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('*.csproj') ? [appHostProjectUri] : [];
                });
                const nodeReadStub = sandbox.stub(fs.promises, 'readFile').rejects(new Error('Node fs should not be used for workspace project fallback.'));
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                    options?.stderrCallback?.('CLI discovery failed');
                    options?.exitCallback?.(1);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result, [{
                        path: appHostProjectUri.fsPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]);
                    assert.strictEqual(nodeReadStub.callCount, 0);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('workspace project fallback checks projects beyond bounded file-search batch', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const projectPaths = Array.from({ length: appHostDiscoveryFindFilesMaxResults + 1 }, (_, index) => path.join(tempDir, 'Project' + index, 'Project' + index + '.csproj'));
                const appHostProjectPath = projectPaths[projectPaths.length - 1];
                for (const projectPath of projectPaths) {
                    fs.mkdirSync(path.dirname(projectPath), { recursive: true });
                    fs.writeFileSync(projectPath, projectPath === appHostProjectPath
                        ? '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />'
                        : '<Project Sdk="Microsoft.NET.Sdk" />');
                }
                findFilesStub.callsFake(async (include: vscode.GlobPattern, _exclude?: vscode.GlobPattern | null, maxResults?: number) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    if (!pattern.endsWith('*.csproj')) {
                        return [];
                    }

                    const uris = projectPaths.map(vscode.Uri.file);
                    return typeof maxResults === 'number' ? uris.slice(0, maxResults) : uris;
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                    options?.stderrCallback?.(`${args.join(' ')} failed`);
                    options?.exitCallback?.(1);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result, [{
                        path: vscode.Uri.file(appHostProjectPath).fsPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('configured AppHost path search excludes nested worktrees and user excluded folders', async () => {
            sandbox.stub(vscode.workspace, 'getConfiguration').callsFake((section?: string) => {
                const values: Record<string, Record<string, boolean>> = {
                    files: {
                        '**/private-checkouts/**': true,
                        '**/generated-but-enabled/**': false,
                    },
                    search: {
                        '**/scratch-worktrees/**': true,
                    },
                };

                return {
                    get: <T>(key: string, defaultValue: T) => key === 'exclude' && section ? values[section] as T : defaultValue,
                } as vscode.WorkspaceConfiguration;
            });
            const cancellationToken = makeCancellationToken();

            await findConfiguredAppHostPaths(makeWorkspaceFolder(buildPath('workspace')), cancellationToken);

            for (const call of findFilesStub.getCalls()) {
                const excludePattern = String(call.args[1]);
                assert.ok(excludePattern.includes('**/.worktrees/**'));
                assert.ok(excludePattern.includes('**/.claude/**'));
                assert.ok(excludePattern.includes('**/private-checkouts/**'));
                assert.ok(excludePattern.includes('**/scratch-worktrees/**'));
                assert.ok(!excludePattern.includes('**/generated-but-enabled/**'));
                assert.strictEqual(call.args[2], appHostDiscoveryFindFilesMaxResults);
                assert.strictEqual(call.args[3], cancellationToken);
            }
        });

        test('caller cancellation does not reject shared configured AppHost path search for other callers', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            let resolveFindFiles: ((uris: vscode.Uri[]) => void) | undefined;
            const findFilesPromise = new Promise<vscode.Uri[]>(resolve => {
                resolveFindFiles = resolve;
            });
            findFilesStub.callsFake(() => findFilesPromise);
            const cancellationSource = new vscode.CancellationTokenSource();
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const cancelledDiscovery = service.discover(workspaceFolder, false, cancellationSource.token);
                const sharedDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.ok(resolveFindFiles);

                cancellationSource.cancel();
                const cancelledResult = assert.rejects(cancelledDiscovery, /cancelled/);
                resolveFindFiles([]);
                await cancelledResult;

                assert.deepStrictEqual(await sharedDiscovery, []);
                assert.strictEqual(findFilesStub.callCount, 2);
            }
            finally {
                cancellationSource.dispose();
                service.dispose();
            }
        });

        test('service discovery shares configured AppHost path search between concurrent callers', async () => {
            stubFileSystemWatchers(sandbox);
            let options: cliModule.SpawnProcessOptions | undefined;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, spawnOptions) => {
                options = spawnOptions;
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const firstDiscovery = service.discover(workspaceFolder);
                const secondDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.ok(options);

                options.stdoutCallback?.('[]');
                options.exitCallback?.(0);

                await Promise.all([firstDiscovery, secondDiscovery]);

                assert.strictEqual(findFilesStub.callCount, 2);
            }
            finally {
                service.dispose();
            }
        });

        test('selects configured path from recursive config during service discovery', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const firstConfigPath = path.join(tempDir, 'First', 'aspire.config.json');
                const secondConfigPath = path.join(tempDir, 'Second', 'aspire.config.json');
                const matchingAppHostPath = path.join(tempDir, 'Second', 'AppHost', 'AppHost.csproj');
                const otherAppHostPath = path.join(tempDir, 'Other', 'AppHost', 'AppHost.csproj');

                fs.mkdirSync(path.dirname(firstConfigPath), { recursive: true });
                fs.mkdirSync(path.dirname(secondConfigPath), { recursive: true });
                fs.writeFileSync(firstConfigPath, JSON.stringify({ appHost: { path: 'Missing/AppHost.csproj' } }));
                fs.writeFileSync(secondConfigPath, JSON.stringify({ appHost: { path: 'AppHost/AppHost.csproj' } }));
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('aspire.config.json')
                        ? [vscode.Uri.file(firstConfigPath), vscode.Uri.file(secondConfigPath)]
                        : [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                    options?.stdoutCallback?.(JSON.stringify([
                        {
                            path: otherAppHostPath,
                            language: 'csharp',
                            status: 'buildable',
                        },
                        {
                            path: matchingAppHostPath,
                            language: 'csharp',
                            status: 'buildable',
                        },
                    ]));
                    options?.exitCallback?.(0);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result, [
                        {
                            path: otherAppHostPath,
                            language: 'csharp',
                            status: 'buildable',
                            selected: false,
                        },
                        {
                            path: matchingAppHostPath,
                            language: 'csharp',
                            status: 'buildable',
                            selected: true,
                        },
                    ]);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('selects configured path that matches a later discovered candidate', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                const workspaceFolder = makeWorkspaceFolder(tempDir);
                const firstConfigPath = path.join(tempDir, 'First', 'aspire.config.json');
                const secondConfigPath = path.join(tempDir, 'Second', 'aspire.config.json');
                const matchingAppHostPath = path.join(tempDir, 'Second', 'AppHost', 'AppHost.csproj');

                fs.mkdirSync(path.dirname(firstConfigPath), { recursive: true });
                fs.mkdirSync(path.dirname(secondConfigPath), { recursive: true });
                fs.writeFileSync(firstConfigPath, JSON.stringify({ appHost: { path: 'Missing/AppHost.csproj' } }));
                fs.writeFileSync(secondConfigPath, JSON.stringify({ appHost: { path: 'AppHost/AppHost.csproj' } }));

                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('aspire.config.json')
                        ? [vscode.Uri.file(firstConfigPath), vscode.Uri.file(secondConfigPath)]
                        : [];
                });

                const selectedPath = await selectWorkspaceAppHostPath(workspaceFolder, [{
                    path: matchingAppHostPath,
                    language: 'csharp',
                    status: 'buildable',
                }]);

                assert.strictEqual(selectedPath, matchingAppHostPath);
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });
    });
});

function buildPath(...segments: string[]): string {
    return path.join(path.sep, ...segments);
}

function makeWorkspaceFolder(folderPath: string): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(folderPath),
        name: path.basename(folderPath),
        index: 0,
    };
}

function makeTerminalProvider(): AspireTerminalProvider {
    return {
        getAspireCliExecutablePath: async () => 'aspire',
        createEnvironment: () => ({}),
    } as unknown as AspireTerminalProvider;
}

function makeConfigInfoProvider(hasLsJsonStreamCapability: boolean): ConfigInfoProvider {
    return {
        hasCapability: async () => hasLsJsonStreamCapability,
    } as unknown as ConfigInfoProvider;
}

async function collectSnapshots(stream: AsyncIterable<AppHostDiscoverySnapshot>, snapshots: AppHostDiscoverySnapshot[]): Promise<AppHostDiscoverySnapshot['candidates']> {
    for await (const snapshot of stream) {
        snapshots.push(snapshot);
    }

    return snapshots.at(-1)?.candidates ?? [];
}

function makeCancellationToken(): vscode.CancellationToken {
    return {
        isCancellationRequested: false,
        onCancellationRequested: () => ({ dispose: () => { } }),
    };
}

async function waitForMicrotasks(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
}

function stubFileSystemWatchers(sandbox: sinon.SinonSandbox): Array<(uri?: vscode.Uri) => void> {
    const callbacks: Array<(uri?: vscode.Uri) => void> = [];
    sandbox.stub(vscode.workspace, 'createFileSystemWatcher').callsFake(() => ({
        onDidCreate: callback => {
            callbacks.push(uri => callback(uri ?? vscode.Uri.file(buildPath('workspace', 'AppHost', 'AppHost.csproj'))));
            return { dispose: () => { } };
        },
        onDidChange: callback => {
            callbacks.push(uri => callback(uri ?? vscode.Uri.file(buildPath('workspace', 'AppHost', 'AppHost.csproj'))));
            return { dispose: () => { } };
        },
        onDidDelete: callback => {
            callbacks.push(uri => callback(uri ?? vscode.Uri.file(buildPath('workspace', 'AppHost', 'AppHost.csproj'))));
            return { dispose: () => { } };
        },
        dispose: () => { },
    } as vscode.FileSystemWatcher));

    return callbacks;
}
