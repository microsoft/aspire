/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../debugger/languages/cli';
import { AppHostDiscoveryService, CandidateAppHostDisplayInfo, findCandidateForEditorFile, findConfiguredAppHostPaths, getDebugTargetForCandidate, getWorkspaceAppHostProjectSearchResult, selectWorkspaceAppHostPath } from '../utils/appHostDiscovery';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';
import { appHostDiscoveryFindFilesMaxResults } from '../utils/workspaceFileSearch';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }
    sendDangerousTelemetryEvent(): void { /* not used here */ }
    sendDangerousTelemetryErrorEvent(): void { /* not used here */ }
    sendRawTelemetryEvent(): void { /* not used here */ }
    dispose(): Promise<void> { return Promise.resolve(); }
}

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

        test('emits discovery result telemetry after successful discovery', async () => {
            stubFileSystemWatchers(sandbox);
            const fake = new FakeTelemetryReporter();
            const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
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
                await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.strictEqual(fake.events.length, 1);
                const event = fake.events[0];
                assert.strictEqual(event.name, 'apphost/discovery/result');
                assert.deepStrictEqual(event.properties, {
                    outcome: 'success',
                    source: 'ls',
                    apphost_languages: 'csharp',
                });
                assert.strictEqual(event.measurements?.candidate_count, 1);
                assert.strictEqual(event.measurements?.buildable_candidate_count, 1);
                assert.ok(typeof event.measurements?.duration_ms === 'number');
            }
            finally {
                service.dispose();
                restore();
                __resetCommonPropertiesForTests();
            }
        });

        test('emits discovery result telemetry after failed discovery', async () => {
            stubFileSystemWatchers(sandbox);
            const fake = new FakeTelemetryReporter();
            const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stderrCallback?.('nope');
                options?.exitCallback?.(1);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                await assert.rejects(service.discover(makeWorkspaceFolder(buildPath('workspace'))), /aspire ls discovery failed/);

                assert.strictEqual(fake.events.length, 1);
                const event = fake.events[0];
                assert.strictEqual(event.name, 'apphost/discovery/result');
                assert.deepStrictEqual(event.properties, {
                    outcome: 'error',
                    source: 'all',
                    apphost_languages: 'none',
                });
                assert.strictEqual(event.measurements?.candidate_count, 0);
                assert.strictEqual(event.measurements?.buildable_candidate_count, 0);
                assert.ok(typeof event.measurements?.duration_ms === 'number');
            }
            finally {
                service.dispose();
                restore();
                __resetCommonPropertiesForTests();
            }
        });

        test('keeps workspace folder debug target unchanged and returns default candidate separately', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([{
                    path: buildPath('workspace', 'NestedAppHost', 'apphost.ts'),
                    language: 'typescript/nodejs',
                    status: 'buildable',
                }]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const result = await service.resolveDebugTarget(workspaceFolder.uri.fsPath, workspaceFolder);
                const candidate = await service.tryFindWorkspaceDefaultCandidate(workspaceFolder.uri.fsPath, workspaceFolder);

                assert.strictEqual(result, workspaceFolder.uri.fsPath);
                assert.strictEqual(candidate?.path, buildPath('workspace', 'NestedAppHost', 'apphost.ts'));
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

        test('watches Node module AppHost filenames', async () => {
            const watchedPatterns: string[] = [];
            sandbox.stub(vscode.workspace, 'createFileSystemWatcher').callsFake((pattern) => {
                watchedPatterns.push(typeof pattern === 'string' ? pattern : pattern.pattern);
                return {
                    ignoreCreateEvents: false,
                    ignoreChangeEvents: false,
                    ignoreDeleteEvents: false,
                    onDidCreate: () => ({ dispose: () => { } }),
                    onDidChange: () => ({ dispose: () => { } }),
                    onDidDelete: () => ({ dispose: () => { } }),
                    dispose: () => { },
                } as vscode.FileSystemWatcher;
            });
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.('[]');
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                await service.discover(workspaceFolder);

                assert.ok(watchedPatterns.includes('**/apphost.ts'));
                assert.ok(watchedPatterns.includes('**/apphost.mts'));
                assert.ok(watchedPatterns.includes('**/apphost.cts'));
                assert.ok(watchedPatterns.includes('**/apphost.js'));
                assert.ok(watchedPatterns.includes('**/apphost.mjs'));
                assert.ok(watchedPatterns.includes('**/apphost.cjs'));
            }
            finally {
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

        test('candidate callbacks receive streamed candidates when joining shared in-flight discovery', async () => {
            stubFileSystemWatchers(sandbox);
            let options: cliModule.SpawnProcessOptions | undefined;
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, spawnOptions) => {
                options = spawnOptions;
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const firstCandidate = {
                path: buildPath('workspace', 'First', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const secondCandidate = {
                path: buildPath('workspace', 'Second', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };

            try {
                const firstDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.ok(options);

                options.lineCallback?.(JSON.stringify(firstCandidate));

                const observedCandidates: CandidateAppHostDisplayInfo[] = [];
                const secondDiscovery = service.discover(workspaceFolder, false, undefined, candidate => observedCandidates.push(candidate));
                assert.deepStrictEqual(observedCandidates, [firstCandidate]);

                options.lineCallback?.(JSON.stringify(secondCandidate));
                options.exitCallback?.(0);

                assert.deepStrictEqual(await firstDiscovery, [firstCandidate, secondCandidate]);
                assert.deepStrictEqual(await secondDiscovery, [firstCandidate, secondCandidate]);
                assert.deepStrictEqual(observedCandidates, [firstCandidate, secondCandidate]);
                assert.strictEqual(spawnStub.callCount, 1);
            }
            finally {
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
                    ['ls', '--format', 'json', '--nologo', '--stream'],
                    ['extension', 'get-apphosts', '--nologo'],
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

        test('does not time out a slow but active aspire ls --stream discovery', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(vscode.workspace, 'getConfiguration').returns({
                get: <T>(key: string, defaultValue: T) => key === 'appHostDiscoveryTimeoutMs' ? 5000 as T : defaultValue,
            } as vscode.WorkspaceConfiguration);
            const clock = sandbox.useFakeTimers();
            let killed = false;
            let capturedOptions: cliModule.SpawnProcessOptions | undefined;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                capturedOptions = options;
                return {
                    killed: false,
                    kill: () => { killed = true; return true; },
                } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const discovery = service.discover(workspaceFolder);
                await waitForMicrotasks();

                const emit = (dir: string) => capturedOptions?.lineCallback?.(JSON.stringify({
                    path: buildPath('workspace', dir, 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }));

                // Each candidate arrives within the 5s idle window, but the total run (12s) far
                // exceeds it. The streaming path's inactivity watchdog must re-arm on every line so a
                // healthy slow stream is never killed by the total-duration timeout it replaced.
                emit('First');
                await clock.tickAsync(4_000);
                emit('Second');
                await clock.tickAsync(4_000);
                emit('Third');
                await clock.tickAsync(4_000);
                capturedOptions?.exitCallback?.(0);

                const result = await discovery;

                assert.strictEqual(killed, false);
                assert.deepStrictEqual(result.map(candidate => candidate.path), [
                    buildPath('workspace', 'First', 'AppHost.csproj'),
                    buildPath('workspace', 'Second', 'AppHost.csproj'),
                    buildPath('workspace', 'Third', 'AppHost.csproj'),
                ]);
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

        test('invokes onCandidate once per streamed aspire ls candidate', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                emitLsStream(options, [
                    {
                        path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', 'Worker', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'running',
                    },
                ]);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const observed: unknown[] = [];
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')), false, undefined, candidate => {
                    observed.push(candidate);
                });

                const expected = [
                    {
                        path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', 'Worker', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'running',
                    },
                ];
                assert.deepStrictEqual(observed, expected);
                assert.deepStrictEqual(result, expected);
            }
            finally {
                service.dispose();
            }
        });

        test('continues discovery when an onCandidate callback throws', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                emitLsStream(options, [
                    {
                        path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', 'Worker', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'running',
                    },
                ]);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                let callbackCount = 0;
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')), false, undefined, () => {
                    callbackCount++;
                    throw new Error('callback boom');
                });

                assert.strictEqual(callbackCount, 2);
                assert.deepStrictEqual(result, [
                    {
                        path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', 'Worker', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'running',
                    },
                ]);
            }
            finally {
                service.dispose();
            }
        });

        test('probes aspire ls with --stream when a no-op candidate callback is provided', async () => {
            stubFileSystemWatchers(sandbox);
            const observedArgs: string[][] = [];
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
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
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.strictEqual(spawnStub.callCount, 1);
                assert.deepStrictEqual(observedArgs, [['ls', '--format', 'json', '--nologo', '--stream']]);
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

        test('retries buffered aspire ls and disables streaming when an older CLI rejects --stream', async () => {
            stubFileSystemWatchers(sandbox);
            const candidate = {
                path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const observedArgs: string[][] = [];
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
                if (args.includes('--stream')) {
                    options?.stderrCallback?.("Unrecognized command or argument '--stream'.");
                    options?.exitCallback?.(1);
                }
                else {
                    options?.stdoutCallback?.(JSON.stringify([candidate]));
                    options?.exitCallback?.(0);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
                const observedCandidates: CandidateAppHostDisplayInfo[] = [];

                const firstResult = await service.discover(workspaceFolder, false, undefined, candidate => observedCandidates.push(candidate));
                assert.deepStrictEqual(firstResult, [candidate]);
                assert.deepStrictEqual(observedCandidates, []);

                const secondResult = await service.discover(workspaceFolder, true);
                assert.deepStrictEqual(secondResult, [candidate]);
                assert.deepStrictEqual(observedArgs, [
                    ['ls', '--format', 'json', '--nologo', '--stream'],
                    ['ls', '--format', 'json', '--nologo'],
                    ['ls', '--format', 'json', '--nologo'],
                ]);
            }
            finally {
                service.dispose();
            }
        });

        test('retries aspire ls without nologo when an older CLI rejects it', async () => {
            stubFileSystemWatchers(sandbox);
            const appHostPath = buildPath('workspace', 'AppHost', 'AppHost.csproj');
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                if (args.includes('--nologo')) {
                    options?.stderrCallback?.("Unrecognized command or argument '--nologo'.");
                    options?.exitCallback?.(1);
                } else {
                    options?.stdoutCallback?.(JSON.stringify([{
                        path: appHostPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]));
                    options?.exitCallback?.(0);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ls', '--format', 'json', '--nologo', '--stream']);
                assert.deepStrictEqual(spawnStub.secondCall.args[2], ['ls', '--format', 'json', '--stream']);
                assert.deepStrictEqual(result, [{
                    path: appHostPath,
                    language: 'csharp',
                    status: 'buildable',
                }]);
            }
            finally {
                service.dispose();
            }
        });

        test('adapts legacy get-apphosts candidates as buildable C# AppHosts', async () => {
            stubFileSystemWatchers(sandbox);
            const appHostPath = buildPath('workspace', 'AppHost', 'AppHost.csproj');
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                if (args[0] === 'ls') {
                    options?.stderrCallback?.('aspire ls unavailable');
                    options?.exitCallback?.(1);
                }
                else {
                    options?.stdoutCallback?.(JSON.stringify({
                        selected_project_file: appHostPath,
                        all_project_file_candidates: [appHostPath],
                    }));
                    options?.exitCallback?.(0);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.deepStrictEqual(result, [{
                    path: appHostPath,
                    language: 'csharp',
                    status: 'buildable',
                    selected: true,
                }]);
            }
            finally {
                service.dispose();
            }
        });

        test('filters aspire ls candidates in excluded directories', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([
                    {
                        path: buildPath('workspace', '.agents', 'skills', 'demo', 'snippets', 'apphost.ts'),
                        language: 'typescript/nodejs',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'buildable',
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

        test('filters path-scoped agent skill candidates but keeps other .github apphosts', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([
                    {
                        path: buildPath('workspace', '.github', 'skills', 'demo', 'snippets', 'apphost.ts'),
                        language: 'typescript/nodejs',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', '.opencode', 'skill', 'demo', 'snippets', 'apphost.ts'),
                        language: 'typescript/nodejs',
                        status: 'buildable',
                    },
                    {
                        path: buildPath('workspace', '.github', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'buildable',
                    },
                ]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.deepStrictEqual(result, [{
                    path: buildPath('workspace', '.github', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]);
            }
            finally {
                service.dispose();
            }
        });

        test('does not include configured apphost candidate in excluded directories', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const configPath = path.join(tempDir, 'aspire.config.json');
                fs.writeFileSync(configPath, JSON.stringify({
                    appHost: {
                        path: '.agents/skills/demo/snippets/apphost.ts',
                    },
                }));
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('aspire.config.json')
                        ? [vscode.Uri.file(configPath)]
                        : [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                    options?.stdoutCallback?.('[]');
                    options?.exitCallback?.(0);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));
                    assert.deepStrictEqual(result, []);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('keeps aspire ls candidate that resolves outside the workspace folder', async () => {
            stubFileSystemWatchers(sandbox);
            // Configured / CLI-sourced AppHost paths can legitimately live outside the workspace folder.
            // The candidate filter must keep them; only the stricter scan/watcher path treats
            // out-of-workspace URIs as excluded. Reverting the candidate filter to the strict variant
            // (excludeOutsideWorkspace=true) would prune this candidate and fail this test.
            const outsideCandidatePath = buildPath('outside-workspace', 'AppHost', 'AppHost.csproj');
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([
                    {
                        path: outsideCandidatePath,
                        language: 'csharp',
                        status: 'buildable',
                    },
                ]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.deepStrictEqual(result, [{
                    path: outsideCandidatePath,
                    language: 'csharp',
                    status: 'buildable',
                }]);
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

        test('retries legacy fallback without nologo when an older CLI rejects it', async () => {
            stubFileSystemWatchers(sandbox);
            const appHostPath = buildPath('workspace', 'AppHost', 'AppHost.csproj');
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                if (args[0] === 'ls') {
                    options?.stderrCallback?.('aspire ls failed');
                    options?.exitCallback?.(1);
                } else if (args.includes('--nologo')) {
                    options?.stderrCallback?.("Unrecognized command or argument '--nologo'.");
                    options?.exitCallback?.(1);
                } else {
                    options?.stdoutCallback?.(JSON.stringify({
                        selected_project_file: appHostPath,
                        all_project_file_candidates: [appHostPath],
                    }));
                    options?.exitCallback?.(0);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.deepStrictEqual(spawnStub.getCall(1).args[2], ['extension', 'get-apphosts', '--nologo']);
                assert.deepStrictEqual(spawnStub.getCall(2).args[2], ['extension', 'get-apphosts']);
                assert.deepStrictEqual(result, [{
                    path: appHostPath,
                    language: 'csharp',
                    status: 'buildable',
                    selected: true,
                }]);
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

        test('falls back to FSharp and Visual Basic project files when CLI discovery fails', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const fsharpAppHostProjectPath = path.join(tempDir, 'FSharpAppHost', 'FSharpAppHost.fsproj');
                const visualBasicAppHostProjectPath = path.join(tempDir, 'VisualBasicAppHost', 'VisualBasicAppHost.vbproj');
                fs.mkdirSync(path.dirname(fsharpAppHostProjectPath), { recursive: true });
                fs.mkdirSync(path.dirname(visualBasicAppHostProjectPath), { recursive: true });
                fs.writeFileSync(fsharpAppHostProjectPath, `<Project Sdk="Aspire.AppHost.Sdk/13.5.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);
                fs.writeFileSync(visualBasicAppHostProjectPath, `<Project Sdk="Aspire.AppHost.Sdk/13.5.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    if (pattern.endsWith('*.fsproj')) {
                        return [vscode.Uri.file(fsharpAppHostProjectPath)];
                    }

                    if (pattern.endsWith('*.vbproj')) {
                        return [vscode.Uri.file(visualBasicAppHostProjectPath)];
                    }

                    return [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                    options?.stderrCallback?.(`${args.join(' ')} failed`);
                    options?.exitCallback?.(1);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result, [
                        {
                            path: vscode.Uri.file(fsharpAppHostProjectPath).fsPath,
                            language: 'fsharp',
                            status: 'buildable',
                        },
                        {
                            path: vscode.Uri.file(visualBasicAppHostProjectPath).fsPath,
                            language: 'visualbasic',
                            status: 'buildable',
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

        test('workspace project fallback recognizes AppHost-specific project forms', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const sdkElementProjectPath = path.join(tempDir, 'SdkElementAppHost', 'SdkElementAppHost.csproj');
                const packageReferenceProjectPath = path.join(tempDir, 'PackageAppHost', 'PackageAppHost.csproj');
                const propertyProjectPath = path.join(tempDir, 'PropertyAppHost', 'PropertyAppHost.csproj');
                const hostingOnlyProjectPath = path.join(tempDir, 'HostingOnlyLibrary', 'HostingOnlyLibrary.csproj');
                fs.mkdirSync(path.dirname(sdkElementProjectPath), { recursive: true });
                fs.mkdirSync(path.dirname(packageReferenceProjectPath), { recursive: true });
                fs.mkdirSync(path.dirname(propertyProjectPath), { recursive: true });
                fs.mkdirSync(path.dirname(hostingOnlyProjectPath), { recursive: true });
                fs.writeFileSync(sdkElementProjectPath, `<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.0" />
</Project>
`);
                fs.writeFileSync(packageReferenceProjectPath, `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="8.2.1" />
  </ItemGroup>
</Project>
`);
                fs.writeFileSync(propertyProjectPath, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsAspireHost>true</IsAspireHost>
  </PropertyGroup>
</Project>
`);
                fs.writeFileSync(hostingOnlyProjectPath, `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting" Version="8.2.1" />
  </ItemGroup>
</Project>
`);
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('*.csproj')
                        ? [
                            vscode.Uri.file(hostingOnlyProjectPath),
                            vscode.Uri.file(packageReferenceProjectPath),
                            vscode.Uri.file(propertyProjectPath),
                            vscode.Uri.file(sdkElementProjectPath),
                        ]
                        : [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                    options?.stderrCallback?.(`${args.join(' ')} failed`);
                    options?.exitCallback?.(1);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result.map(candidate => candidate.path), [
                        vscode.Uri.file(packageReferenceProjectPath).fsPath,
                        vscode.Uri.file(propertyProjectPath).fsPath,
                        vscode.Uri.file(sdkElementProjectPath).fsPath,
                    ]);
                    assert.deepStrictEqual(result.map(candidate => candidate.language), ['csharp', 'csharp', 'csharp']);
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
                const projectPaths = Array.from({ length: appHostDiscoveryFindFilesMaxResults + 1 }, (_, index) => '/Project' + index + '/Project' + index + '.csproj');
                const appHostProjectPath = projectPaths[projectPaths.length - 1];
                const projectContentsByPath = new Map<string, string>();
                for (const projectPath of projectPaths) {
                    const projectContents = projectPath === appHostProjectPath
                        ? '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />'
                        : '<Project Sdk="Microsoft.NET.Sdk" />';
                    projectContentsByPath.set(projectPath, projectContents);
                }
                const fileSystemProvider = new InMemoryProjectFileSystemProvider(projectContentsByPath);
                const fileSystemRegistration = vscode.workspace.registerFileSystemProvider('aspire-discovery-test', fileSystemProvider, { isCaseSensitive: true });
                findFilesStub.callsFake(async (include: vscode.GlobPattern, _exclude?: vscode.GlobPattern | null, maxResults?: number) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    if (!pattern.endsWith('*.csproj')) {
                        return [];
                    }

                    const uris = projectPaths.map(projectPath => vscode.Uri.from({ scheme: 'aspire-discovery-test', path: projectPath }));
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
                        path: vscode.Uri.from({ scheme: 'aspire-discovery-test', path: appHostProjectPath }).fsPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]);
                }
                finally {
                    fileSystemRegistration.dispose();
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
                assert.ok(excludePattern.includes('**/.agents/**'));
                assert.ok(excludePattern.includes('**/.github/skills/**'));
                assert.ok(excludePattern.includes('**/.opencode/skill/**'));
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

        test('configured C# AppHost candidate resolves editor source file', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const configPath = path.join(tempDir, 'aspire.config.json');
                const appHostProjectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
                const appHostProgramPath = path.join(tempDir, 'AppHost', 'Program.cs');

                fs.mkdirSync(path.dirname(appHostProjectPath), { recursive: true });
                fs.writeFileSync(configPath, JSON.stringify({ appHost: { path: 'AppHost/AppHost.csproj' } }));
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('aspire.config.json')
                        ? [vscode.Uri.file(configPath)]
                        : [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                    options?.stdoutCallback?.('[]');
                    options?.exitCallback?.(0);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));
                    const sourceFileCandidate = await service.tryFindCandidateForEditorFile(appHostProgramPath, makeWorkspaceFolder(tempDir));

                    assert.strictEqual(result.length, 1);
                    assert.strictEqual(path.normalize(result[0].path).toLowerCase(), path.normalize(appHostProjectPath).toLowerCase());
                    assert.strictEqual(result[0].language, 'csharp');
                    assert.strictEqual(result[0].status, 'buildable');
                    assert.strictEqual(result[0].selected, true);
                    assert.strictEqual(path.normalize(sourceFileCandidate?.path ?? '').toLowerCase(), path.normalize(appHostProjectPath).toLowerCase());
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
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

        test('does not pick an arbitrary workspace default when multiple buildable candidates are selected', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                const workspaceFolder = makeWorkspaceFolder(tempDir);
                findFilesStub.resolves([]);

                const firstAppHostPath = path.join(tempDir, 'First', 'AppHost.csproj');
                const secondAppHostPath = path.join(tempDir, 'Second', 'AppHost.csproj');
                const selectedPath = await selectWorkspaceAppHostPath(workspaceFolder, [
                    {
                        path: firstAppHostPath,
                        language: 'csharp',
                        status: 'buildable',
                        selected: true,
                    },
                    {
                        path: secondAppHostPath,
                        language: 'csharp',
                        status: 'buildable',
                        selected: true,
                    },
                ]);

                assert.strictEqual(selectedPath, undefined);
            }
            finally {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('does not serialize an arbitrary selected_project_file when multiple buildable candidates are selected', () => {
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const result = getWorkspaceAppHostProjectSearchResult(workspaceFolder, [
                {
                    path: buildPath('workspace', 'First', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                    selected: true,
                },
                {
                    path: buildPath('workspace', 'Second', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                    selected: true,
                },
            ]);

            assert.strictEqual(result.selected_project_file, null);
            assert.deepStrictEqual(result.all_project_file_candidates, [
                buildPath('workspace', 'First', 'AppHost.csproj'),
                buildPath('workspace', 'Second', 'AppHost.csproj'),
            ]);
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

function emitLsStream(options: cliModule.SpawnProcessOptions | undefined, entries: readonly unknown[]): void {
    for (const entry of entries) {
        options?.lineCallback?.(JSON.stringify(entry));
    }

    options?.exitCallback?.(0);
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

class InMemoryProjectFileSystemProvider implements vscode.FileSystemProvider {
    private readonly _onDidChangeFile = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
    readonly onDidChangeFile = this._onDidChangeFile.event;

    public constructor(private readonly _files: Map<string, string>) {
    }

    public watch(): vscode.Disposable {
        return { dispose: () => { } };
    }

    public stat(uri: vscode.Uri): vscode.FileStat {
        const contents = this._files.get(uri.path);
        if (contents === undefined) {
            throw vscode.FileSystemError.FileNotFound(uri);
        }

        return {
            type: vscode.FileType.File,
            ctime: 0,
            mtime: 0,
            size: Buffer.byteLength(contents, 'utf8'),
        };
    }

    public readDirectory(): [string, vscode.FileType][] {
        return [];
    }

    public createDirectory(uri: vscode.Uri): void {
        throw vscode.FileSystemError.NoPermissions(uri);
    }

    public readFile(uri: vscode.Uri): Uint8Array {
        const contents = this._files.get(uri.path);
        if (contents === undefined) {
            throw vscode.FileSystemError.FileNotFound(uri);
        }

        return Buffer.from(contents, 'utf8');
    }

    public writeFile(uri: vscode.Uri): void {
        throw vscode.FileSystemError.NoPermissions(uri);
    }

    public delete(uri: vscode.Uri): void {
        throw vscode.FileSystemError.NoPermissions(uri);
    }

    public rename(_oldUri: vscode.Uri, newUri: vscode.Uri): void {
        throw vscode.FileSystemError.NoPermissions(newUri);
    }
}
