/// <reference types="mocha" />

import * as assert from 'assert';
import fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { EventEmitter } from 'events';
import { ChildProcess, type ChildProcessWithoutNullStreams } from 'child_process';
import { PassThrough } from 'stream';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createWithAspireCommand } from '../commands/createWithAspire';
import * as cliModule from '../utils/process/cliProcess';
import { AppHostDiscoveryCliUnavailableError, AppHostDiscoveryService, CandidateAppHostDisplayInfo, findCandidateForEditorFile, findConfiguredAppHostPaths, getDebugTargetForCandidate, getWorkspaceAppHostProjectSearchResult, isSameFileSystemEntry, selectWorkspaceAppHostPath } from '../utils/appHostDiscovery';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as configInfoProvider from '../utils/configInfoProvider';
import { lsJsonStreamCapability } from '../types/configInfo';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';
import { appHostDiscoveryFindFilesMaxResults } from '../utils/workspaceFileSearch';
import { workspaceFolderCliPathTarget, getCliPathTargetKey, type CliPathResolutionTarget } from '../utils/cliPathVariables';

import { removeDirectorySafely } from './testHelpers';
interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];

    public telemetryLevel: 'all' | 'error' | 'crash' | 'off' = 'all';

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        // Extension code now bypasses this path; recording here would only
        // see a regression to the prefixed channel. Kept as a typed no-op
        // so the fake still satisfies the TelemetryReporter shape.
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }
    sendRawTelemetryEvent(): void { /* not used here */ }
    dispose(): Promise<void> { return Promise.resolve(); }
}

suite('AppHost discovery', () => {
    suite('filesystem identity comparison', () => {
        let sandbox: sinon.SinonSandbox;

        setup(() => {
            sandbox = sinon.createSandbox();
            sandbox.stub(process, 'platform').value('win32');
        });

        teardown(() => {
            sandbox.restore();
        });

        test('keeps case-only Windows paths distinct when stable identities differ', () => {
            const left = path.join('workspace', 'Foo');
            const right = path.join('workspace', 'foo');
            const identities = new Map([
                [path.resolve(left), { dev: 1n, ino: 100n }],
                [path.resolve(right), { dev: 1n, ino: 101n }],
            ]);

            assert.strictEqual(isSameFileSystemEntry(left, right, filePath => identities.get(filePath)), false);
        });

        test('matches case-only Windows paths when stable identities are equal', () => {
            const left = path.join('workspace', 'Foo');
            const right = path.join('workspace', 'foo');
            const identity = { dev: 1n, ino: 100n };

            assert.strictEqual(isSameFileSystemEntry(left, right, () => identity), true);
        });

        test('falls back to Windows path comparison when identity is unavailable', () => {
            const left = path.join('workspace', 'Foo');
            const right = path.join('workspace', 'foo');
            const identities = new Map([
                [path.resolve(left), { dev: 1n, ino: 100n }],
            ]);

            assert.strictEqual(isSameFileSystemEntry(left, right, filePath => identities.get(filePath)), true);
        });
    });

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

    test('keeps Rust AppHost candidate as source file', () => {
        const appHostPath = buildPath('workspace', 'AppHost', 'apphost.rs');

        const candidate = findCandidateForEditorFile(appHostPath, [{
            path: appHostPath,
            language: 'rust',
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
        let getConfigInfoStub: sinon.SinonStub;

        setup(() => {
            sandbox = sinon.createSandbox();
            findFilesStub = sandbox.stub(vscode.workspace, 'findFiles').resolves([]);
            getConfigInfoStub = sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
                capabilities: [lsJsonStreamCapability],
            } as any);
        });

        teardown(() => {
            sandbox.restore();
        });

        test('does not force refresh discovery after cached negative editor lookup', async () => {
            stubFileSystemWatchers(sandbox);
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                emitLsOutput(options, [{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]);
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
                emitLsOutput(options, [{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.strictEqual(fake.events.length, 1);
                const event = fake.events[0];
                assert.strictEqual(event.name, 'aspire/vscode/apphost/discovery/result');
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
                assert.strictEqual(event.name, 'aspire/vscode/apphost/discovery/result');
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
                emitLsOutput(options, [{
                    path: buildPath('workspace', 'NestedAppHost', 'apphost.ts'),
                    language: 'typescript/nodejs',
                    status: 'buildable',
                }]);
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
                assert.strictEqual(spawnStub.callCount, 1);

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

        test('forgetting a workspace folder retires its watchers and cache without cancelling subscribers', async () => {
            const watcherDisposals: sinon.SinonSpy[] = [];
            sandbox.stub(vscode.workspace, 'createFileSystemWatcher').callsFake(() => {
                const dispose = sinon.spy();
                watcherDisposals.push(dispose);
                return {
                    onDidCreate: () => ({ dispose: () => { } }),
                    onDidChange: () => ({ dispose: () => { } }),
                    onDidDelete: () => ({ dispose: () => { } }),
                    dispose,
                } as unknown as vscode.FileSystemWatcher;
            });
            const processKills: sinon.SinonSpy[] = [];
            const spawnOptions: Array<Parameters<typeof emitLsOutput>[0]> = [];
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                spawnOptions.push(options);
                const kill = sinon.spy();
                processKills.push(kill);
                return { kill } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const firstDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 1);
                const initialWatcherCount = watcherDisposals.length;
                assert.ok(initialWatcherCount > 0);

                service.forgetWorkspaceFolder(workspaceFolder);
                assert.ok(watcherDisposals.every(dispose => dispose.calledOnce));
                assert.strictEqual(processKills[0].called, false);

                emitLsOutput(spawnOptions[0], []);
                await firstDiscovery;

                const secondDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 2);
                assert.strictEqual(watcherDisposals.length, initialWatcherCount * 2);
                emitLsOutput(spawnOptions[1], []);
                await secondDiscovery;
            }
            finally {
                service.dispose();
            }
        });

        test('forgetting one workspace folder preserves other folder caches', async () => {
            stubFileSystemWatchers(sandbox);
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                emitLsOutput(options, []);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const rootA = makeWorkspaceFolder(buildPath('workspace', 'root-a'));
            const rootB = makeWorkspaceFolder(buildPath('workspace', 'root-b'));

            try {
                await service.discover(rootA);
                await service.discover(rootB);
                assert.strictEqual(spawnStub.callCount, 2);

                service.forgetWorkspaceFolder(rootB);

                await service.discover(rootA);
                assert.strictEqual(spawnStub.callCount, 2);

                await service.discover(rootB);
                assert.strictEqual(spawnStub.callCount, 3);
                assert.strictEqual(spawnStub.thirdCall.args[3]?.workingDirectory, rootB.uri.fsPath);
            }
            finally {
                service.dispose();
            }
        });

        test('watches guest AppHost filenames', async () => {
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
                assert.ok(watchedPatterns.includes('**/apphost.rs'));
                // Case matters: watcher globs are case-sensitive on Linux and the file is AppHost.java.
                assert.ok(watchedPatterns.includes('**/AppHost.java'));
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
            const childProcess = Object.assign(new EventEmitter(), {
                killed: false,
                exitCode: null,
                signalCode: null,
                kill: sandbox.stub(),
            });
            childProcess.kill.callsFake(() => {
                childProcess.killed = true;
                childProcess.emit('exit', null);
                return true;
            });
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

                await assert.rejects(cancelledDiscovery, vscode.CancellationError);
                assert.strictEqual(childProcess.kill.callCount, 0);

                emitLsOutput(options, [{
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }]);

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

        test('forced refresh does not reject existing shared discovery callers', async () => {
            stubFileSystemWatchers(sandbox);
            const spawnOptions: cliModule.SpawnProcessOptions[] = [];
            const childProcesses: Array<{ kill: sinon.SinonStub }> = [];
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                assert.ok(options);
                spawnOptions.push(options);
                const childProcess = { kill: sandbox.stub().returns(true) };
                childProcesses.push(childProcess);
                return childProcess as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const originalCandidate = {
                path: buildPath('workspace', 'Original', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const refreshedCandidate = {
                path: buildPath('workspace', 'Refreshed', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };

            try {
                const originalDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 1);

                const refreshedDiscovery = service.discover(workspaceFolder, true);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 2);
                assert.strictEqual(childProcesses[0].kill.callCount, 0);

                emitLsOutput(spawnOptions[1], [refreshedCandidate]);
                assert.deepStrictEqual(await refreshedDiscovery, [refreshedCandidate]);
                assert.deepStrictEqual(await service.discover(workspaceFolder), [refreshedCandidate]);
                assert.strictEqual(spawnStub.callCount, 2);

                emitLsOutput(spawnOptions[0], [originalCandidate]);
                assert.deepStrictEqual(await originalDiscovery, [originalCandidate]);
                assert.strictEqual(childProcesses[0].kill.callCount, 0);
            }
            finally {
                service.dispose();
            }
        });

        test('dispose cancels force-replaced discovery before CLI startup', async () => {
            stubFileSystemWatchers(sandbox);
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess');
            const terminalProvider = {
                resolveAspireCliPath: () => new Promise<never>(() => { }),
                createEnvironment: () => ({}),
            } as unknown as AspireTerminalProvider;
            const service = new AppHostDiscoveryService(terminalProvider);
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const originalDiscovery = service.discover(workspaceFolder);
            await waitForMicrotasks();
            const refreshedDiscovery = service.discover(workspaceFolder, true);
            await waitForMicrotasks();

            service.dispose();

            await assert.rejects(originalDiscovery, /disposed/);
            await assert.rejects(refreshedDiscovery, /disposed/);
            assert.strictEqual(spawnStub.callCount, 0);
        });

        test('late callers receive prior and future streamed AppHosts', async () => {
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
            const excludedCandidate = {
                path: buildPath('workspace', '.agents', 'skills', 'demo', 'snippets', 'apphost.ts'),
                language: 'typescript/nodejs',
                status: 'buildable',
            };

            try {
                const firstDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.ok(options);

                options.lineCallback?.(JSON.stringify(firstCandidate));
                options.lineCallback?.(JSON.stringify(excludedCandidate));

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

        test('file changes leave active discovery running and invalidate its result', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            let hangCli = true;
            let activeDiscoveryOptions: Parameters<typeof cliModule.spawnCliProcess>[3] | undefined;
            let activeDiscoveryKill: sinon.SinonStub | undefined;
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args = [], options) => {
                const kill = sandbox.stub().returns(true);
                const childProcess = {
                    killed: false,
                    kill,
                };

                if (hangCli) {
                    activeDiscoveryOptions = options;
                    activeDiscoveryKill = kill;
                } else {
                    options?.stdoutCallback?.('[]');
                    options?.exitCallback?.(0);
                }

                return childProcess as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const firstDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0]();
                await clock.tickAsync(250);

                assert.strictEqual(activeDiscoveryKill?.callCount, 0);
                activeDiscoveryOptions?.stdoutCallback?.('[]');
                activeDiscoveryOptions?.exitCallback?.(0);
                assert.deepStrictEqual(await firstDiscovery, []);

                hangCli = false;
                assert.deepStrictEqual(await service.discover(workspaceFolder), []);
                assert.strictEqual(spawnStub.callCount, 2);
            }
            finally {
                service.dispose();
                clock.restore();
            }
        });

        test('new callers wait for one fresh discovery after stale active discovery completes', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            const discoveryOptions: cliModule.SpawnProcessOptions[] = [];
            const discoveryKills: sinon.SinonStub[] = [];
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args = [], options) => {
                const kill = sandbox.stub().returns(true);
                discoveryOptions.push(options!);
                discoveryKills.push(kill);
                return {
                    killed: false,
                    kill,
                } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const staleCandidate = {
                path: buildPath('workspace', 'Old', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const freshCandidate = {
                path: buildPath('workspace', 'New', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };

            try {
                const firstDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 1);

                watcherCallbacks[0]();
                await clock.tickAsync(250);

                const secondDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 1);
                assert.deepStrictEqual(discoveryKills.map(kill => kill.callCount), [0]);

                watcherCallbacks[0]();
                await clock.tickAsync(250);
                const thirdDiscovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.strictEqual(spawnStub.callCount, 1);

                discoveryOptions[0].lineCallback?.(JSON.stringify(staleCandidate));
                discoveryOptions[0].exitCallback?.(0);
                assert.deepStrictEqual(await firstDiscovery, [staleCandidate]);
                await waitForMicrotasks();

                assert.strictEqual(spawnStub.callCount, 2);
                assert.deepStrictEqual(discoveryKills.map(kill => kill.callCount), [0, 0]);

                discoveryOptions[1].lineCallback?.(JSON.stringify(freshCandidate));
                discoveryOptions[1].exitCallback?.(0);
                assert.deepStrictEqual(await Promise.all([secondDiscovery, thirdDiscovery]), [
                    [freshCandidate],
                    [freshCandidate],
                ]);

                assert.deepStrictEqual(await service.discover(workspaceFolder), [freshCandidate]);
                assert.strictEqual(spawnStub.callCount, 2);
            }
            finally {
                service.dispose();
                clock.restore();
            }
        });

        test('repeated project changes do not restart active discovery', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            const childProcess = Object.assign(new EventEmitter(), {
                killed: false,
                exitCode: null,
                signalCode: null,
                kill: sandbox.stub(),
            });
            childProcess.kill.callsFake(() => {
                childProcess.killed = true;
                childProcess.emit('exit', null);
                return true;
            });
            sandbox.stub(cliModule, 'spawnCliProcess').returns(childProcess as any);
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            let candidateChangeCount = 0;
            const subscription = service.onDidChangeCandidates(() => candidateChangeCount++);
            const discovery = service.discover(workspaceFolder).catch(error => error);

            try {
                await waitForMicrotasks();

                for (let i = 0; i < 10; i++) {
                    watcherCallbacks[0]();
                    await clock.tickAsync(500);
                    assert.strictEqual(childProcess.kill.callCount, 0);
                }

                assert.strictEqual(childProcess.kill.callCount, 0);
                assert.strictEqual(candidateChangeCount, 10);
            }
            finally {
                subscription.dispose();
                service.dispose();
                await discovery;
                clock.restore();
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
                await assert.rejects(service.discover(workspaceFolder, false, cancellationSource.token), vscode.CancellationError);
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
                const childProcess = Object.assign(new EventEmitter(), {
                    killed: false,
                    exitCode: null,
                    signalCode: null,
                    kill: sandbox.stub(),
                });
                childProcess.kill.callsFake(() => {
                    childProcess.killed = true;
                    killedArgs.push(args);
                    childProcess.emit('exit', null);
                    return true;
                });
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
                await waitForMicrotasks();
                await clock.tickAsync(5_000);

                await assert.rejects(discovery, /timed out after 5 seconds/);
                assert.deepStrictEqual(killedArgs, [
                    ['ls', '--format', 'json', '--stream', '--nologo'],
                    ['ls', '--format', 'json', '--nologo'],
                    ['extension', 'get-apphosts', '--nologo'],
                ]);

                hangCli = false;
                const retryResult = await service.discover(workspaceFolder);
                assert.deepStrictEqual(retryResult, []);
                assert.strictEqual(spawnStub.callCount, 4);
            }
            finally {
                service.dispose();
                clock.restore();
            }
        });

        test('streamed discovery can exceed the inactivity timeout', async () => {
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
                // exceeds it. The inactivity watchdog must re-arm without interfering with the
                // separate five-minute maximum streaming runtime.
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

        test('streamed discovery stops at the maximum runtime', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(vscode.workspace, 'getConfiguration').returns({
                get: <T>(key: string, defaultValue: T) => key === 'appHostDiscoveryTimeoutMs' ? 5000 as T : defaultValue,
            } as vscode.WorkspaceConfiguration);
            const clock = sandbox.useFakeTimers();
            const killedArgs: string[][] = [];
            let streamOptions: cliModule.SpawnProcessOptions | undefined;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                const childProcess = Object.assign(new EventEmitter(), {
                    killed: false,
                    exitCode: null,
                    signalCode: null,
                    kill: sandbox.stub(),
                });
                childProcess.kill.callsFake(() => {
                    childProcess.killed = true;
                    killedArgs.push(args);
                    childProcess.emit('exit', null);
                    return true;
                });

                if (args.includes('--stream')) {
                    streamOptions = options;
                }
                else {
                    options?.stderrCallback?.('legacy discovery unavailable');
                    options?.exitCallback?.(1);
                }

                return childProcess as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                const discovery = service.discover(workspaceFolder);
                const expectedRejection = assert.rejects(discovery, /exceeded the maximum streaming runtime of 300 seconds/);
                await waitForMicrotasks();
                assert.ok(streamOptions);

                const activityTimer = setInterval(() => {
                    streamOptions?.stderrCallback?.('still discovering projects');
                }, 4_000);
                await clock.tickAsync(5 * 60 * 1000);
                clearInterval(activityTimer);

                await expectedRejection;
                assert.deepStrictEqual(killedArgs, [
                    ['ls', '--format', 'json', '--stream', '--nologo'],
                ]);
            }
            finally {
                service.dispose();
                clock.restore();
            }
        });

        test('stderr activity resets the discovery inactivity timeout', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(vscode.workspace, 'getConfiguration').returns({
                get: <T>(key: string, defaultValue: T) => key === 'appHostDiscoveryTimeoutMs' ? 5000 as T : defaultValue,
            } as vscode.WorkspaceConfiguration);
            const clock = sandbox.useFakeTimers();
            const killedArgs: string[][] = [];
            let streamOptions: cliModule.SpawnProcessOptions | undefined;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                const childProcess = {
                    killed: false,
                    kill: sandbox.stub().callsFake(() => {
                        childProcess.killed = true;
                        killedArgs.push(args);
                        return true;
                    }),
                };

                if (args.includes('--stream')) {
                    streamOptions = options;
                }
                else {
                    options?.stderrCallback?.('legacy discovery unavailable');
                    options?.exitCallback?.(1);
                }

                return childProcess as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const candidate = {
                path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };

            try {
                const discovery = service.discover(workspaceFolder);
                await waitForMicrotasks();
                assert.ok(streamOptions);

                await clock.tickAsync(4_000);
                streamOptions.stderrCallback?.('still discovering projects');
                await clock.tickAsync(2_000);
                streamOptions.lineCallback?.(JSON.stringify(candidate));
                streamOptions.exitCallback?.(0);

                assert.deepStrictEqual(await discovery, [candidate]);
                assert.deepStrictEqual(killedArgs, []);
            }
            finally {
                service.dispose();
                clock.restore();
            }
        });

        test('keeps valid aspire ls candidates when future entries have unexpected shape', async () => {
            getConfigInfoStub.resolves({ capabilities: [] } as any);
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                emitLsOutput(options, [
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
                ]);
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

        test('streamed AppHosts preserve arrival order', async () => {
            stubFileSystemWatchers(sandbox);
            const zetaCandidate = {
                path: buildPath('workspace', 'Zeta', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const alphaCandidate = {
                path: buildPath('workspace', 'Alpha', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                emitLsStream(options, [zetaCandidate, alphaCandidate]);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const observed: CandidateAppHostDisplayInfo[] = [];
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')), false, undefined, candidate => {
                    observed.push(candidate);
                });

                assert.deepStrictEqual(observed, [zetaCandidate, alphaCandidate]);
                assert.deepStrictEqual(result, [alphaCandidate, zetaCandidate]);
            }
            finally {
                service.dispose();
            }
        });

        test('candidate callback failures do not stop discovery', async () => {
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

        test('streaming capability enables incremental AppHost discovery', async () => {
            stubFileSystemWatchers(sandbox);
            const observedArgs: string[][] = [];
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
                options?.lineCallback?.(JSON.stringify({
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                }));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const result = await service.discover(makeWorkspaceFolder(buildPath('workspace')));

                assert.strictEqual(spawnStub.callCount, 1);
                assert.deepStrictEqual(observedArgs, [['ls', '--format', 'json', '--stream', '--nologo']]);
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

        test('missing streaming capability uses buffered AppHost discovery', async () => {
            getConfigInfoStub.resolves({ capabilities: [] } as any);
            stubFileSystemWatchers(sandbox);
            const candidate = {
                path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const observedArgs: string[][] = [];
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
                options?.stdoutCallback?.(JSON.stringify([candidate]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                assert.deepStrictEqual(await service.discover(makeWorkspaceFolder(buildPath('workspace'))), [candidate]);
                assert.deepStrictEqual(observedArgs, [['ls', '--format', 'json', '--nologo']]);
            }
            finally {
                service.dispose();
            }
        });

        test('unavailable capabilities use buffered AppHost discovery', async () => {
            getConfigInfoStub.resolves(undefined);
            stubFileSystemWatchers(sandbox);
            const candidate = {
                path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const observedArgs: string[][] = [];
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
                options?.stdoutCallback?.(JSON.stringify([candidate]));
                options?.exitCallback?.(0);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                assert.deepStrictEqual(await service.discover(makeWorkspaceFolder(buildPath('workspace'))), [candidate]);
                assert.deepStrictEqual(observedArgs, [['ls', '--format', 'json', '--nologo']]);
            }
            finally {
                service.dispose();
            }
        });

        test('reprobes streaming capability after CLI path changes', async () => {
            getConfigInfoStub.onFirstCall().resolves({ capabilities: [] } as any);
            getConfigInfoStub.onSecondCall().resolves({ capabilities: [lsJsonStreamCapability] } as any);
            stubFileSystemWatchers(sandbox);
            let cliPath = 'old-aspire';
            const terminalProvider = makeTerminalProvider();
            sandbox.stub(terminalProvider, 'getAspireCliExecutablePath').callsFake(async () => cliPath);
            const observedCalls: Array<{ cliPath: string; args: string[] }> = [];
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, command, args = [], options) => {
                observedCalls.push({ cliPath: command, args });
                const candidate = {
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                };
                emitLsOutput(options, [candidate]);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(terminalProvider);

            try {
                await service.discover(makeWorkspaceFolder(buildPath('workspace-one')));
                cliPath = 'new-aspire';
                await service.discover(makeWorkspaceFolder(buildPath('workspace-two')));

                assert.deepStrictEqual(observedCalls, [
                    { cliPath: 'old-aspire', args: ['ls', '--format', 'json', '--nologo'] },
                    { cliPath: 'new-aspire', args: ['ls', '--format', 'json', '--stream', '--nologo'] },
                ]);
                assert.deepStrictEqual(getConfigInfoStub.getCalls().map(call => ({ ...call.args[0], target: getCliPathTargetKey(call.args[0].target) })), [
                    { suppressErrors: true, forceRefresh: false, cliPath: 'old-aspire', target: getCliPathTargetKey(workspaceFolderCliPathTarget(makeWorkspaceFolder(buildPath('workspace-one')))) },
                    { suppressErrors: true, forceRefresh: false, cliPath: 'new-aspire', target: getCliPathTargetKey(workspaceFolderCliPathTarget(makeWorkspaceFolder(buildPath('workspace-two')))) },
                ]);
            }
            finally {
                service.dispose();
            }
        });

        test('force refresh retries an unavailable streaming capability probe', async () => {
            getConfigInfoStub.onFirstCall().resolves(null);
            getConfigInfoStub.onSecondCall().resolves({ capabilities: [lsJsonStreamCapability] } as any);
            stubFileSystemWatchers(sandbox);
            const observedArgs: string[][] = [];
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
                const candidate = {
                    path: buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                    language: 'csharp',
                    status: 'buildable',
                };
                emitLsOutput(options, [candidate]);
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                await service.discover(workspaceFolder);
                await service.discover(workspaceFolder, true);

                assert.deepStrictEqual(observedArgs, [
                    ['ls', '--format', 'json', '--nologo'],
                    ['ls', '--format', 'json', '--stream', '--nologo'],
                ]);
                assert.deepStrictEqual(getConfigInfoStub.getCalls().map(call => call.args[0]), [
                    { suppressErrors: true, forceRefresh: false, cliPath: 'aspire', target: workspaceFolderCliPathTarget(workspaceFolder) },
                    { suppressErrors: true, forceRefresh: true, cliPath: 'aspire', target: workspaceFolderCliPathTarget(workspaceFolder) },
                ]);
            }
            finally {
                service.dispose();
            }
        });

        test('generic CLI errors do not disable streamed discovery', async () => {
            const watcherCallbacks = stubFileSystemWatchers(sandbox);
            const clock = sandbox.useFakeTimers();
            const streamCandidate = {
                path: buildPath('workspace', 'Stream', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const bufferedCandidate = {
                path: buildPath('workspace', 'Buffered', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const observedArgs: string[][] = [];
            let streamAttempt = 0;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
                if (args[0] === 'ls' && args.includes('--stream')) {
                    if (streamAttempt++ === 0) {
                        options?.stderrCallback?.('ls --format json --stream failed');
                        options?.exitCallback?.(1);
                    }
                    else {
                        options?.lineCallback?.(JSON.stringify(streamCandidate));
                        options?.exitCallback?.(0);
                    }
                }
                else if (args[0] === 'ls') {
                    options?.stdoutCallback?.(JSON.stringify([bufferedCandidate]));
                    options?.exitCallback?.(0);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                assert.deepStrictEqual(await service.discover(workspaceFolder), [bufferedCandidate]);

                watcherCallbacks[0]();
                await clock.tickAsync(250);
                assert.deepStrictEqual(await service.discover(workspaceFolder), [streamCandidate]);

                assert.deepStrictEqual(observedArgs, [
                    ['ls', '--format', 'json', '--stream', '--nologo'],
                    ['ls', '--format', 'json', '--nologo'],
                    ['ls', '--format', 'json', '--stream', '--nologo'],
                ]);
            }
            finally {
                service.dispose();
                clock.restore();
            }
        });

        test('retries aspire ls without nologo when an older CLI rejects it', async () => {
            stubFileSystemWatchers(sandbox);
            const appHostPath = buildPath('workspace', 'AppHost', 'AppHost.csproj');
            const candidate = {
                path: appHostPath,
                language: 'csharp',
                status: 'buildable',
            };
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                if (args.includes('--nologo')) {
                    options?.stdoutCallback?.(`${JSON.stringify(candidate)}\n`);
                    options?.lineCallback?.(JSON.stringify(candidate));
                    options?.stderrCallback?.("Unrecognized command or argument '--nologo'.");
                    options?.exitCallback?.(1);
                } else {
                    emitLsStream(options, [candidate]);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const observed: CandidateAppHostDisplayInfo[] = [];
                const result = await service.discover(
                    makeWorkspaceFolder(buildPath('workspace')),
                    false,
                    undefined,
                    candidate => observed.push(candidate));

                assert.deepStrictEqual(spawnStub.firstCall.args[2], ['ls', '--format', 'json', '--stream', '--nologo']);
                assert.deepStrictEqual(spawnStub.secondCall.args[2], ['ls', '--format', 'json', '--stream']);
                assert.deepStrictEqual(observed, [candidate]);
                assert.deepStrictEqual(result, [candidate]);
            }
            finally {
                service.dispose();
            }
        });

        test('stream failures do not expose candidate output', async () => {
            stubFileSystemWatchers(sandbox);
            const candidate = {
                path: buildPath('workspace', 'Private', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                if (args.includes('--stream')) {
                    options?.stdoutCallback?.(`${JSON.stringify(candidate)}\n`);
                    options?.lineCallback?.(JSON.stringify(candidate));
                    options?.exitCallback?.(1);
                } else if (args[0] === 'ls') {
                    options?.stderrCallback?.('buffered discovery unavailable');
                    options?.exitCallback?.(1);
                } else {
                    options?.stderrCallback?.('legacy discovery unavailable');
                    options?.exitCallback?.(1);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                await assert.rejects(
                    service.discover(makeWorkspaceFolder(buildPath('workspace'))),
                    {
                        message: 'aspire ls discovery failed: aspire ls streaming discovery failed: exit code 1\naspire ls buffered fallback failed: buffered discovery unavailable\naspire extension get-apphosts fallback failed: legacy discovery unavailable',
                    });
            }
            finally {
                service.dispose();
            }
        });

        test('truncated streamed output falls back to buffered discovery instead of completing partially', async () => {
            stubFileSystemWatchers(sandbox);
            const streamedCandidate = {
                path: buildPath('workspace', 'Partial', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const bufferedCandidate = {
                path: buildPath('workspace', 'Fallback', 'apphost.ts'),
                language: 'typescript/nodejs',
                status: 'buildable',
                selected: true,
            };
            const observedArgs: string[][] = [];
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                observedArgs.push(args);
                if (args.includes('--stream')) {
                    options?.lineCallback?.(JSON.stringify(streamedCandidate));
                    options?.lineCallback?.('{"path":');
                } else if (args[0] === 'ls') {
                    options?.stdoutCallback?.(JSON.stringify([bufferedCandidate]));
                    options?.exitCallback?.(0);
                }
                return { kill: () => { } } as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const observed: CandidateAppHostDisplayInfo[] = [];
                const result = await service.discover(
                    makeWorkspaceFolder(buildPath('workspace')),
                    false,
                    undefined,
                    candidate => observed.push(candidate));
                assert.deepStrictEqual(observed, [streamedCandidate]);
                assert.deepStrictEqual(result, [bufferedCandidate]);
                assert.deepStrictEqual(observedArgs, [
                    ['ls', '--format', 'json', '--stream', '--nologo'],
                    ['ls', '--format', 'json', '--nologo'],
                ]);
            }
            finally {
                service.dispose();
            }
        });

        test('late stream callbacks do not report candidates after buffered fallback begins', async () => {
            stubFileSystemWatchers(sandbox);
            const lateStreamCandidate = {
                path: buildPath('workspace', 'Late', 'AppHost.csproj'),
                language: 'csharp',
                status: 'buildable',
            };
            const bufferedCandidate = {
                path: buildPath('workspace', 'Fallback', 'apphost.ts'),
                language: 'typescript/nodejs',
                status: 'buildable',
            };
            let streamOptions: cliModule.SpawnProcessOptions | undefined;
            let bufferedOptions: cliModule.SpawnProcessOptions | undefined;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                const childProcess = Object.assign(new EventEmitter(), {
                    killed: false,
                    exitCode: null,
                    signalCode: null as NodeJS.Signals | null,
                    kill: sandbox.stub(),
                });
                childProcess.kill.callsFake(() => {
                    childProcess.killed = true;
                    childProcess.signalCode = 'SIGTERM';
                    childProcess.emit('exit', null, childProcess.signalCode);
                    childProcess.emit('close', null, childProcess.signalCode);
                    return true;
                });

                if (args.includes('--stream')) {
                    streamOptions = options;
                }
                else if (args[0] === 'ls') {
                    bufferedOptions = options;
                }
                return childProcess as any;
            });
            const service = new AppHostDiscoveryService(makeTerminalProvider());

            try {
                const observed: CandidateAppHostDisplayInfo[] = [];
                const discovery = service.discover(
                    makeWorkspaceFolder(buildPath('workspace')),
                    false,
                    undefined,
                    candidate => observed.push(candidate));
                await waitForMicrotasks();

                assert.ok(streamOptions);
                streamOptions.lineCallback?.('{"path":');
                await waitForMicrotasks();

                assert.ok(bufferedOptions);
                streamOptions.lineCallback?.(JSON.stringify(lateStreamCandidate));
                assert.deepStrictEqual(observed, []);

                bufferedOptions.stdoutCallback?.(JSON.stringify([bufferedCandidate]));
                bufferedOptions.exitCallback?.(0);
                assert.deepStrictEqual(await discovery, [bufferedCandidate]);
                assert.deepStrictEqual(observed, []);
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

        for (const selectionKind of ['enumerated', 'outside', 'alias'] as const) {
            test(`preserves ${selectionKind} legacy CLI selection instead of overriding it from configuration`, async () => {
                const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                try {
                    stubFileSystemWatchers(sandbox);
                    const workspaceRoot = path.join(fixtureRoot, 'workspace');
                    fs.mkdirSync(workspaceRoot);
                    const workspaceFolder = makeWorkspaceFolder(workspaceRoot);
                    const configuredPath = path.join(workspaceFolder.uri.fsPath, 'AppHost.csproj');
                    const selectedPath = vscode.Uri.file(path.join(fixtureRoot, 'external', 'AppHost.csproj')).fsPath;
                    const configPath = path.join(workspaceFolder.uri.fsPath, 'aspire.config.json');
                    fs.mkdirSync(path.dirname(selectedPath));
                    fs.writeFileSync(selectedPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                    let cliSelectedPath = selectedPath;
                    if (selectionKind === 'alias') {
                        const aliasDirectory = path.join(fixtureRoot, 'alias');
                        fs.symlinkSync(path.dirname(selectedPath), aliasDirectory, 'junction');
                        cliSelectedPath = vscode.Uri.file(path.join(aliasDirectory, 'AppHost.csproj')).fsPath;
                    }
                    fs.writeFileSync(configuredPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                    fs.writeFileSync(configPath, JSON.stringify({ appHost: { path: 'AppHost.csproj' } }));
                    findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                        const pattern = typeof include === 'string' ? include : include.pattern;
                        return pattern.endsWith('aspire.config.json') ? [vscode.Uri.file(configPath)] : [];
                    });
                    sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                        if (args[0] === 'ls') {
                            options?.stderrCallback?.('aspire ls unavailable');
                            options?.exitCallback?.(1);
                        }
                        else {
                            options?.stdoutCallback?.(JSON.stringify({
                                selected_project_file: cliSelectedPath,
                                all_project_file_candidates: selectionKind === 'outside' ? [configuredPath] : [configuredPath, selectedPath],
                            }));
                            options?.exitCallback?.(0);
                        }
                        return { kill: () => true } as ReturnType<typeof cliModule.spawnCliProcess>;
                    });
                    const service = new AppHostDiscoveryService(makeTerminalProvider());
                    try {
                        assert.deepStrictEqual(await service.discover(workspaceFolder), [{
                            path: selectedPath,
                            language: 'csharp',
                            status: 'buildable',
                            selected: true,
                        }, {
                            path: configuredPath,
                            language: 'csharp',
                            status: 'buildable',
                        }]);
                    }
                    finally {
                        service.dispose();
                    }
                }
                finally {
                    removeDirectorySafely(fixtureRoot);
                }
            });
        }

        test('filters aspire ls candidates in excluded directories', async () => {
            stubFileSystemWatchers(sandbox);
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                emitLsOutput(options, [
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
                ]);
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
                emitLsOutput(options, [
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
                ]);
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
                removeDirectorySafely(tempDir);
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
                emitLsOutput(options, [
                    {
                        path: outsideCandidatePath,
                        language: 'csharp',
                        status: 'buildable',
                    },
                ]);
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
                    /aspire ls discovery failed: aspire ls streaming discovery failed: ls --format json --stream failed\naspire ls buffered fallback failed: ls --format json failed\naspire extension get-apphosts fallback failed: extension get-apphosts failed/);
            }
            finally {
                service.dispose();
            }
        });

        test('distinguishes an unavailable CLI after a completed empty workspace search', async () => {
            stubFileSystemWatchers(sandbox);
            const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
            terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
            const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess');
            const service = new AppHostDiscoveryService(terminalProvider);
            const folder = makeWorkspaceFolder(buildPath('workspace'));

            try {
                await assert.rejects(service.discover(folder), AppHostDiscoveryCliUnavailableError);

                assert.ok(terminalProvider.resolveAspireCliPath.calledOnceWithExactly(workspaceFolderCliPathTarget(folder)));
                assert.strictEqual(spawnStub.called, false);
                assert.strictEqual(getConfigInfoStub.called, false);
            }
            finally {
                service.dispose();
            }
        });

        for (const [fileName, language] of [
            ['apphost.cs', 'csharp'],
            ['apphost.ts', 'typescript'],
            ['apphost.mts', 'typescript'],
            ['apphost.cts', 'typescript'],
            ['apphost.js', 'typescript'],
            ['apphost.mjs', 'typescript'],
            ['apphost.cjs', 'typescript'],
            ['apphost.rs', 'rust'],
            ['AppHost.java', 'java'],
        ]) {
            test(`finds ${fileName} without an installed CLI`, async () => {
                stubFileSystemWatchers(sandbox);
                const appHostUri = vscode.Uri.file(buildPath('workspace', 'AppHost', fileName));
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.includes(`**/${fileName}`) ? [appHostUri] : [];
                });
                const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
                terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                const service = new AppHostDiscoveryService(terminalProvider);

                try {
                    assert.deepStrictEqual(await service.discover(makeWorkspaceFolder(buildPath('workspace'))), [{
                        path: appHostUri.fsPath,
                        language,
                        status: 'possibly-unbuildable',
                    }]);
                }
                finally {
                    service.dispose();
                }
            });
        }

        test('keeps a project AppHost distinct from its source file without an installed CLI', async () => {
            stubFileSystemWatchers(sandbox);
            const projectUri = vscode.Uri.file(buildPath('workspace', 'AppHost', 'AppHost.csproj')).with({ scheme: 'aspire-discovery-test' });
            const sourceUri = vscode.Uri.file(buildPath('workspace', 'AppHost', 'apphost.cs')).with({ scheme: 'aspire-discovery-test' });
            const fileSystemRegistration = vscode.workspace.registerFileSystemProvider(
                'aspire-discovery-test',
                new InMemoryProjectFileSystemProvider(new Map([[projectUri.path, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />']])),
                { isCaseSensitive: true });
            findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                const pattern = typeof include === 'string' ? include : include.pattern;
                if (pattern.endsWith('*.csproj')) {
                    return [projectUri];
                }
                return pattern.includes('**/apphost.cs') ? [sourceUri] : [];
            });
            const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
            terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
            const service = new AppHostDiscoveryService(terminalProvider);

            try {
                assert.deepStrictEqual(await service.discover(makeWorkspaceFolder(buildPath('workspace'))), [{
                    path: projectUri.fsPath,
                    language: 'csharp',
                    status: 'buildable',
                }]);
            }
            finally {
                service.dispose();
                fileSystemRegistration.dispose();
            }
        });

        test('keeps configured AppHost presence without an installed CLI', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const configUri = vscode.Uri.file(path.join(tempDir, 'aspire.config.json'));
                const appHostPath = path.join(tempDir, 'custom-host.mts');
                fs.writeFileSync(appHostPath, 'export {};');
                fs.writeFileSync(configUri.fsPath, JSON.stringify({ appHost: { path: appHostPath } }));
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('aspire.config.json') ? [configUri] : [];
                });
                const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
                terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                const service = new AppHostDiscoveryService(terminalProvider);

                try {
                    assert.deepStrictEqual(await service.discover(makeWorkspaceFolder(tempDir)), [{
                        path: appHostPath,
                        language: 'typescript',
                        status: 'possibly-unbuildable',
                        selected: true,
                    }]);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                removeDirectorySafely(tempDir);
            }
        });

        for (const format of ['current', 'legacy']) {
            for (const projectFirst of [false, true]) {
                for (const sourceKind of ['canonical', 'configured', 'missing']) {
                    test(`completes offline file checks despite unreadable projects (${format}, ${projectFirst ? 'project' : 'source'} first, ${sourceKind} source)`, async () => {
                        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                        try {
                            stubFileSystemWatchers(sandbox);
                            const folder = makeWorkspaceFolder(tempDir);
                            sandbox.stub(vscode.workspace, 'workspaceFolders').value([folder]);
                            const projectUri = vscode.Uri.file(path.join(tempDir, 'Unreadable.csproj')).with({ scheme: 'aspire-discovery-test' });
                            const sourcePath = vscode.Uri.file(path.join(tempDir, sourceKind === 'canonical' ? 'apphost.mts' : 'custom-host.mts')).fsPath;
                            if (sourceKind !== 'missing') {
                                fs.writeFileSync(sourcePath, 'export {};');
                            }
                            const files = new Map<string, string | Error>([[projectUri.path, vscode.FileSystemError.NoPermissions(projectUri)]]);
                            const configuredPaths = projectFirst ? [projectUri.fsPath, sourcePath] : [sourcePath, projectUri.fsPath];
                            const configUris = configuredPaths.map((configuredPath, index) => {
                                const uri = vscode.Uri.file(format === 'current'
                                    ? path.join(tempDir, `config-${index}`, 'aspire.config.json')
                                    : path.join(tempDir, `config-${index}`, '.aspire', 'settings.json')).with({ scheme: 'aspire-discovery-test' });
                                files.set(uri.path, JSON.stringify(format === 'current'
                                    ? { appHost: { path: configuredPath } }
                                    : { appHostPath: configuredPath }));
                                return uri;
                            });
                            const fileSystemRegistration = vscode.workspace.registerFileSystemProvider(
                                'aspire-discovery-test',
                                new InMemoryProjectFileSystemProvider(files),
                                { isCaseSensitive: true });
                            const configPattern = format === 'current' ? '**/aspire.config.json' : '**/.aspire/settings.json';
                            findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                                const pattern = typeof include === 'string' ? include : include.pattern;
                                return pattern === configPattern ? configUris
                                    : pattern.endsWith('*.csproj') ? [projectUri]
                                        : sourceKind === 'canonical' && pattern.includes('**/apphost.mts') ? [vscode.Uri.file(sourcePath)]
                                            : [];
                            });
                            const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
                            terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                            const showErrorStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
                            const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => (await items)[0]);
                            sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
                            const service = new AppHostDiscoveryService(terminalProvider);

                            try {
                                if (sourceKind === 'missing') {
                                    await assert.rejects(service.discover(folder), error =>
                                        error instanceof Error
                                        && !(error instanceof AppHostDiscoveryCliUnavailableError)
                                        && error.message.includes('Failed to read possible AppHost project'));
                                }
                                else {
                                    assert.deepStrictEqual(await service.discover(folder), [{
                                        path: sourcePath,
                                        language: 'typescript',
                                        status: 'possibly-unbuildable',
                                        selected: true,
                                    }]);
                                }

                                await createWithAspireCommand(service);

                                assert.deepStrictEqual((await showQuickPickStub.firstCall.args[0]).map(item => 'command' in item ? item.command : undefined),
                                    ['aspire-vscode.new']);
                                assert.strictEqual(showErrorStub.callCount, sourceKind === 'missing' ? 1 : 0);
                            }
                            finally {
                                service.dispose();
                                fileSystemRegistration.dispose();
                            }
                        }
                        finally {
                            removeDirectorySafely(tempDir);
                        }
                    });
                }
            }
        }

        for (const resultKind of ['project', 'configured-source', 'empty', 'canceled', 'disposed']) {
            test(`preserves ${resultKind} results when offline source search fails`, async () => {
                const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                try {
                    stubFileSystemWatchers(sandbox);
                    const folder = makeWorkspaceFolder(tempDir);
                    const projectUri = vscode.Uri.file(path.join(tempDir, 'AppHost.csproj'));
                    const sourceUri = vscode.Uri.file(path.join(tempDir, 'custom-host.mts'));
                    const configUri = vscode.Uri.file(path.join(tempDir, 'aspire.config.json'));
                    const hasProject = ['project', 'canceled', 'disposed'].includes(resultKind);
                    if (hasProject) {
                        fs.writeFileSync(projectUri.fsPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                    }
                    if (resultKind === 'configured-source') {
                        fs.writeFileSync(sourceUri.fsPath, 'export {};');
                        fs.writeFileSync(configUri.fsPath, JSON.stringify({ appHost: { path: sourceUri.fsPath } }));
                    }
                    const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
                    terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                    const service = new AppHostDiscoveryService(terminalProvider);
                    findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                        const pattern = typeof include === 'string' ? include : include.pattern;
                        if (pattern.includes('**/apphost.mts')) {
                            if (resultKind === 'canceled') {
                                throw new vscode.CancellationError();
                            }
                            if (resultKind === 'disposed') {
                                service.dispose();
                            }
                            throw new Error('source file search failed');
                        }
                        return pattern.endsWith('*.csproj') && hasProject ? [projectUri]
                            : pattern.endsWith('aspire.config.json') && resultKind === 'configured-source' ? [configUri]
                                : [];
                    });

                    try {
                        if (resultKind === 'canceled') {
                            await assert.rejects(service.discover(folder), vscode.CancellationError);
                        }
                        else if (resultKind === 'disposed') {
                            await assert.rejects(service.discover(folder), /disposed/);
                        }
                        else if (resultKind === 'empty') {
                            await assert.rejects(service.discover(folder), error =>
                                error instanceof Error
                                && !(error instanceof AppHostDiscoveryCliUnavailableError)
                                && error.message.includes('source file search failed'));
                        }
                        else {
                            assert.deepStrictEqual(await service.discover(folder), [resultKind === 'project' ? {
                                path: projectUri.fsPath,
                                language: 'csharp',
                                status: 'buildable',
                            } : {
                                path: sourceUri.fsPath,
                                language: 'typescript',
                                status: 'possibly-unbuildable',
                                selected: true,
                            }]);
                        }
                    }
                    finally {
                        service.dispose();
                    }
                }
                finally {
                    removeDirectorySafely(tempDir);
                }
            });
        }

        for (const target of [
            { name: 'missing source', fileName: 'missing-host.mts', contents: undefined, directory: false },
            { name: 'source directory', fileName: 'custom-host.mts', contents: undefined, directory: true },
            { name: 'missing project', fileName: 'missing-host.csproj', contents: undefined, directory: false },
            { name: 'project directory', fileName: 'custom-host.csproj', contents: undefined, directory: true },
            { name: 'ordinary project', fileName: 'custom-host.csproj', contents: '<Project Sdk="Microsoft.NET.Sdk" />', directory: false },
            { name: 'unsupported file', fileName: 'custom-host.txt', contents: 'not an AppHost', directory: false },
        ]) {
            test(`does not infer AppHost presence from a configured ${target.name} without an installed CLI`, async () => {
                const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                try {
                    stubFileSystemWatchers(sandbox);
                    const appHostPath = path.join(tempDir, target.fileName);
                    if (target.directory) {
                        fs.mkdirSync(appHostPath);
                    }
                    else if (target.contents !== undefined) {
                        fs.writeFileSync(appHostPath, target.contents);
                    }
                    const configUri = vscode.Uri.file(path.join(tempDir, 'aspire.config.json'));
                    fs.writeFileSync(configUri.fsPath, JSON.stringify({ appHost: { path: appHostPath } }));
                    findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                        const pattern = typeof include === 'string' ? include : include.pattern;
                        return pattern.endsWith('aspire.config.json') ? [configUri] : [];
                    });
                    const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
                    terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                    const service = new AppHostDiscoveryService(terminalProvider);

                    try {
                        await assert.rejects(service.discover(makeWorkspaceFolder(tempDir)), AppHostDiscoveryCliUnavailableError);
                    }
                    finally {
                        service.dispose();
                    }
                }
                finally {
                    removeDirectorySafely(tempDir);
                }
            });
        }

        for (const format of ['current', 'legacy']) {
            for (const cliAvailable of [false, true]) {
                for (const excludedFirst of [false, true]) {
                    test(`uses ${cliAvailable ? 'authoritative CLI' : 'verified file'} discovery with ${format} configuration and ${excludedFirst ? 'excluded' : 'valid'} path first`, async () => {
                        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                        try {
                            stubFileSystemWatchers(sandbox);
                            const folder = makeWorkspaceFolder(tempDir);
                            sandbox.stub(vscode.workspace, 'workspaceFolders').value([folder]);
                            const appHostPath = path.join(tempDir, 'AppHost', 'custom-host.mts');
                            const excludedPath = path.join(tempDir, '.agents', 'skills', 'demo', 'apphost.mts');
                            const configuredPaths = excludedFirst ? [excludedPath, appHostPath] : [appHostPath, excludedPath];
                            const configUris = configuredPaths.map((configuredPath, index) => {
                                fs.mkdirSync(path.dirname(configuredPath), { recursive: true });
                                fs.writeFileSync(configuredPath, 'export {};');
                                const uri = vscode.Uri.file(format === 'current'
                                    ? path.join(tempDir, `config-${index}`, 'aspire.config.json')
                                    : path.join(tempDir, `config-${index}`, '.aspire', 'settings.json'));
                                fs.mkdirSync(path.dirname(uri.fsPath), { recursive: true });
                                fs.writeFileSync(uri.fsPath, JSON.stringify(format === 'current'
                                    ? { appHost: { path: configuredPath } }
                                    : { appHostPath: configuredPath }));
                                return uri;
                            });
                            const configPattern = format === 'current' ? '**/aspire.config.json' : '**/.aspire/settings.json';
                            findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                                const pattern = typeof include === 'string' ? include : include.pattern;
                                return pattern === configPattern ? configUris
                                    : pattern.includes('**/apphost.mts') ? [vscode.Uri.file(excludedPath)]
                                        : [];
                            });
                            const terminalProvider = makeTerminalProvider();
                            if (!cliAvailable) {
                                sandbox.stub(terminalProvider, 'resolveAspireCliPath').resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                            }
                            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                                emitLsOutput(options, []);
                                return makeCompletedCliProcess();
                            });
                            const showErrorStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
                            const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => (await items)[0]);
                            sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
                            const service = new AppHostDiscoveryService(terminalProvider);

                            try {
                                assert.deepStrictEqual(await service.discover(folder), cliAvailable ? [] : [{
                                    path: appHostPath,
                                    language: 'typescript',
                                    status: 'possibly-unbuildable',
                                    selected: true,
                                }]);

                                await createWithAspireCommand(service);

                                assert.deepStrictEqual((await showQuickPickStub.firstCall.args[0]).map(item => 'command' in item ? item.command : undefined),
                                    cliAvailable ? ['aspire-vscode.new', 'aspire-vscode.init'] : ['aspire-vscode.new']);
                                assert.strictEqual(showErrorStub.called, false);
                            }
                            finally {
                                service.dispose();
                            }
                        }
                        finally {
                            removeDirectorySafely(tempDir);
                        }
                    });
                }
            }
        }

        for (const candidateKind of ['empty', 'excluded', 'known']) {
            for (const scenario of [
                { name: 'failed configuration searches', searchError: new Error('configuration search failed'), contents: undefined },
                { name: 'unreadable configuration', searchError: undefined, contents: undefined },
                { name: 'malformed configuration', searchError: undefined, contents: '{invalid JSON' },
            ]) {
                test(`keeps ${candidateKind} authoritative CLI discovery with ${scenario.name}`, async () => {
                    stubFileSystemWatchers(sandbox);
                    const folder = makeWorkspaceFolder(buildPath('workspace'));
                    sandbox.stub(vscode.workspace, 'workspaceFolders').value([folder]);
                    const configUri = vscode.Uri.file(buildPath('workspace', 'aspire.config.json')).with({ scheme: 'aspire-discovery-test' });
                    const files = new Map<string, string>();
                    if (scenario.contents !== undefined) {
                        files.set(configUri.path, scenario.contents);
                    }
                    const fileSystemRegistration = vscode.workspace.registerFileSystemProvider(
                        'aspire-discovery-test',
                        new InMemoryProjectFileSystemProvider(files),
                        { isCaseSensitive: true });
                    findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                        const pattern = typeof include === 'string' ? include : include.pattern;
                        if (!pattern.endsWith('aspire.config.json')) {
                            return [];
                        }
                        if (scenario.searchError) {
                            throw scenario.searchError;
                        }
                        return [configUri];
                    });
                    const candidates: CandidateAppHostDisplayInfo[] = candidateKind === 'empty' ? [] : [{
                        path: candidateKind === 'excluded'
                            ? buildPath('workspace', '.agents', 'skills', 'demo', 'AppHost.csproj')
                            : buildPath('workspace', 'AppHost', 'AppHost.csproj'),
                        language: 'csharp',
                        status: 'possibly-unbuildable',
                    }];
                    const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                        emitLsOutput(options, candidates);
                        return makeCompletedCliProcess();
                    });
                    const showErrorStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
                    const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => (await items)[0]);
                    const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
                    const service = new AppHostDiscoveryService(makeTerminalProvider());
                    try {
                        assert.deepStrictEqual(await service.discover(folder), candidateKind === 'known' ? candidates : []);

                        await createWithAspireCommand(service);

                        assert.deepStrictEqual((await showQuickPickStub.firstCall.args[0]).map(item => 'command' in item ? item.command : undefined),
                            candidateKind === 'known' ? ['aspire-vscode.new'] : ['aspire-vscode.new', 'aspire-vscode.init']);
                        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
                        assert.strictEqual(showErrorStub.called, false);
                        assert.strictEqual(spawnStub.callCount, 1);
                    }
                    finally {
                        service.dispose();
                        fileSystemRegistration.dispose();
                    }
                });
            }
        }

        for (const format of ['current', 'legacy']) {
            for (const cliAvailable of [false, true]) {
                for (const target of [
                    { name: 'source', fileName: 'custom-host.mts', contents: 'export {};', language: 'typescript', status: 'possibly-unbuildable' },
                    { name: 'project', fileName: 'custom-host.csproj', contents: '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />', language: 'csharp', status: 'buildable' },
                ]) {
                    test(`completes ${format} ${target.name} configuration checks beyond the bounded search when the CLI is ${cliAvailable ? 'available' : 'unavailable'}`, async () => {
                        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                        try {
                            stubFileSystemWatchers(sandbox);
                            const workspaceRoot = path.join(tempDir, 'workspace');
                            fs.mkdirSync(workspaceRoot);
                            const folder = makeWorkspaceFolder(workspaceRoot);
                            sandbox.stub(vscode.workspace, 'workspaceFolders').value([folder]);
                            const appHostPath = path.join(tempDir, target.fileName);
                            fs.writeFileSync(appHostPath, target.contents);
                            const configUris = Array.from({ length: appHostDiscoveryFindFilesMaxResults + 1 }, (_, index) => {
                                const uri = vscode.Uri.file(format === 'current'
                                    ? path.join(workspaceRoot, `config-${index}`, 'aspire.config.json')
                                    : path.join(workspaceRoot, `config-${index}`, '.aspire', 'settings.json'));
                                fs.mkdirSync(path.dirname(uri.fsPath), { recursive: true });
                                fs.writeFileSync(uri.fsPath, index === appHostDiscoveryFindFilesMaxResults
                                    ? JSON.stringify(format === 'current' ? { appHost: { path: appHostPath } } : { appHostPath })
                                    : '{}');
                                return uri;
                            });
                            const configPattern = format === 'current' ? '**/aspire.config.json' : '**/.aspire/settings.json';
                            findFilesStub.callsFake(async (include: vscode.GlobPattern, _exclude, maxResults) => {
                                const pattern = typeof include === 'string' ? include : include.pattern;
                                return pattern === configPattern ? configUris.slice(0, maxResults) : [];
                            });
                            const terminalProvider = makeTerminalProvider();
                            if (!cliAvailable) {
                                sandbox.stub(terminalProvider, 'resolveAspireCliPath').resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                            }
                            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                                emitLsOutput(options, []);
                                return makeCompletedCliProcess();
                            });
                            const showErrorStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
                            const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => (await items)[0]);
                            sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
                            const service = new AppHostDiscoveryService(terminalProvider);

                            try {
                                assert.deepStrictEqual(await service.discover(folder), cliAvailable ? [] : [{
                                    path: appHostPath,
                                    language: target.language,
                                    status: target.status,
                                }]);
                                assert.ok(findFilesStub.getCalls().some(call => {
                                    const include: vscode.GlobPattern = call.args[0];
                                    const pattern = typeof include === 'string' ? include : include.pattern;
                                    return pattern === configPattern && call.args[2] === (cliAvailable ? appHostDiscoveryFindFilesMaxResults : undefined);
                                }));

                                await createWithAspireCommand(service);

                                assert.deepStrictEqual((await showQuickPickStub.firstCall.args[0]).map(item => 'command' in item ? item.command : undefined),
                                    cliAvailable ? ['aspire-vscode.new', 'aspire-vscode.init'] : ['aspire-vscode.new']);
                                assert.strictEqual(showErrorStub.called, false);
                            }
                            finally {
                                service.dispose();
                            }
                        }
                        finally {
                            removeDirectorySafely(tempDir);
                        }
                    });
                }
            }
        }

        for (const failure of [new Error('workspace search failed'), undefined]) {
            test(`does not treat ${failure ? 'a failed' : 'an undefined rejection from'} workspace search as CLI unavailability`, async () => {
                stubFileSystemWatchers(sandbox);
                findFilesStub.callsFake(async () => { throw failure; });
                const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
                terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                const service = new AppHostDiscoveryService(terminalProvider);

                try {
                    await assert.rejects(service.discover(makeWorkspaceFolder(buildPath('workspace'))), error =>
                        error instanceof Error
                        && !(error instanceof AppHostDiscoveryCliUnavailableError)
                        && error.message.includes('workspace file fallback failed:'));
                }
                finally {
                    service.dispose();
                }
            });
        }

        for (const scenario of [
            { name: 'unreadable projects', fileName: 'AppHost.csproj', patternSuffix: '*.csproj', contents: undefined },
            { name: 'unreadable configuration', fileName: 'aspire.config.json', patternSuffix: 'aspire.config.json', contents: undefined },
            { name: 'malformed configuration', fileName: 'aspire.config.json', patternSuffix: 'aspire.config.json', contents: '{invalid JSON' },
        ]) {
            test(`does not treat ${scenario.name} as CLI unavailability`, async () => {
                stubFileSystemWatchers(sandbox);
                const fileUri = vscode.Uri.file(buildPath('workspace', scenario.fileName)).with({ scheme: 'aspire-discovery-test' });
                const files = new Map<string, string>();
                if (scenario.contents !== undefined) {
                    files.set(fileUri.path, scenario.contents);
                }
                const fileSystemRegistration = vscode.workspace.registerFileSystemProvider(
                    'aspire-discovery-test',
                    new InMemoryProjectFileSystemProvider(files),
                    { isCaseSensitive: true });
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith(scenario.patternSuffix) ? [fileUri] : [];
                });
                const terminalProvider = sandbox.createStubInstance(AspireTerminalProvider);
                terminalProvider.resolveAspireCliPath.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
                const service = new AppHostDiscoveryService(terminalProvider);

                try {
                    await assert.rejects(service.discover(makeWorkspaceFolder(buildPath('workspace'))), error =>
                        error instanceof Error
                        && !(error instanceof AppHostDiscoveryCliUnavailableError)
                        && error.message.includes('workspace file fallback failed:'));
                }
                finally {
                    service.dispose();
                    fileSystemRegistration.dispose();
                }
            });
        }

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

                assert.deepStrictEqual(spawnStub.getCall(0).args[2], ['ls', '--format', 'json', '--stream', '--nologo']);
                assert.deepStrictEqual(spawnStub.getCall(1).args[2], ['ls', '--format', 'json', '--nologo']);
                assert.deepStrictEqual(spawnStub.getCall(2).args[2], ['extension', 'get-apphosts', '--nologo']);
                assert.deepStrictEqual(spawnStub.getCall(3).args[2], ['extension', 'get-apphosts']);
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

        test('falls back to project files when CLI path resolution times out', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                sandbox.stub(vscode.workspace, 'getConfiguration').returns({
                    get: <T>(key: string, defaultValue: T) => key === 'appHostDiscoveryTimeoutMs' ? 1000 as T : defaultValue,
                } as vscode.WorkspaceConfiguration);
                const appHostProjectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
                fs.mkdirSync(path.dirname(appHostProjectPath), { recursive: true });
                fs.writeFileSync(appHostProjectPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('*.csproj') ? [vscode.Uri.file(appHostProjectPath)] : [];
                });
                const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess');
                const getCliPath = sandbox.stub().returns(new Promise<never>(() => { }));
                const terminalProvider = {
                    resolveAspireCliPath: getCliPath,
                    createEnvironment: () => ({}),
                } as unknown as AspireTerminalProvider;
                const service = new AppHostDiscoveryService(terminalProvider);

                try {
                    assert.deepStrictEqual(await service.discover(makeWorkspaceFolder(tempDir)), [{
                        path: vscode.Uri.file(appHostProjectPath).fsPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]);
                    assert.strictEqual(getCliPath.callCount, 1);
                    assert.strictEqual(spawnStub.callCount, 0);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                removeDirectorySafely(tempDir);
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
                removeDirectorySafely(tempDir);
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
                removeDirectorySafely(tempDir);
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
                removeDirectorySafely(tempDir);
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
                removeDirectorySafely(tempDir);
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
                removeDirectorySafely(tempDir);
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
                const cancelledResult = assert.rejects(cancelledDiscovery, vscode.CancellationError);
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
                    emitLsOutput(options, [{
                        path: appHostProjectPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]);
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
                removeDirectorySafely(tempDir);
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
                    emitLsOutput(options, [
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
                    ]);
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
                removeDirectorySafely(tempDir);
            }
        });

        test('does not re-add configured AppHost path that no longer exists on disk', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const configPath = path.join(tempDir, 'aspire.config.json');
                const realAppHostPath = path.join(tempDir, 'aspire-apphost.csproj');

                fs.writeFileSync(realAppHostPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                fs.writeFileSync(configPath, JSON.stringify({ appHost: { path: 'aspire_apphost.csproj' } }));
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('aspire.config.json')
                        ? [vscode.Uri.file(configPath)]
                        : [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                    emitLsOutput(options, [
                        {
                            path: realAppHostPath,
                            language: 'csharp',
                            status: 'buildable',
                        },
                    ]);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result, [
                        {
                            path: realAppHostPath,
                            language: 'csharp',
                            status: 'buildable',
                        },
                    ]);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                removeDirectorySafely(tempDir);
            }
        });

        for (const discoveryMode of ['streaming', 'buffered', 'buffered-after-stream-error', 'legacy'] as const) {
            for (const hasCandidates of [true, false]) {
                test(`does not append configured files after successful ${discoveryMode} discovery with ${hasCandidates ? 'candidates' : 'no candidates'}`, async () => {
                    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                    try {
                        stubFileSystemWatchers(sandbox);
                        getConfigInfoStub.resolves({
                            capabilities: discoveryMode === 'buffered' ? [] : [lsJsonStreamCapability],
                        });
                        const workspaceFolder = makeWorkspaceFolder(tempDir);
                        const configPath = path.join(tempDir, 'aspire.config.json');
                        const configuredAppHostPath = path.join(workspaceFolder.uri.fsPath, 'AppHost', 'AppHost.csproj');
                        const candidates: CandidateAppHostDisplayInfo[] = hasCandidates ? [{
                            path: path.join(workspaceFolder.uri.fsPath, 'Other', 'AppHost.csproj'),
                            language: 'csharp',
                            status: 'buildable',
                        }] : [];

                        fs.mkdirSync(path.dirname(configuredAppHostPath), { recursive: true });
                        fs.writeFileSync(configuredAppHostPath, discoveryMode === 'legacy'
                            ? '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />'
                            : '<Project Sdk="Microsoft.NET.Sdk" />');
                        fs.writeFileSync(configPath, JSON.stringify({ appHost: { path: 'AppHost/AppHost.csproj' } }));
                        findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                            const pattern = typeof include === 'string' ? include : include.pattern;
                            return pattern.endsWith('aspire.config.json')
                                ? [vscode.Uri.file(configPath)]
                                : [];
                        });
                        const spawnStub = sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
                            if (discoveryMode === 'legacy' && args[0] === 'extension') {
                                options?.stdoutCallback?.(JSON.stringify({
                                    selected_project_file: null,
                                    all_project_file_candidates: candidates.map(candidate => candidate.path),
                                }));
                                options?.exitCallback?.(0);
                            }
                            else if (discoveryMode === 'legacy' || (discoveryMode === 'buffered-after-stream-error' && args.includes('--stream'))) {
                                options?.stderrCallback?.('streaming discovery unavailable');
                                options?.exitCallback?.(1);
                            }
                            else {
                                emitLsOutput(options, candidates);
                            }
                            return { kill: () => true } as ReturnType<typeof cliModule.spawnCliProcess>;
                        });
                        const service = new AppHostDiscoveryService(makeTerminalProvider());

                        try {
                            assert.deepStrictEqual(await service.discover(workspaceFolder), candidates);
                            if (discoveryMode === 'legacy') {
                                assert.deepStrictEqual(spawnStub.lastCall.args[2]?.slice(0, 2), ['extension', 'get-apphosts']);
                            }
                            else {
                                assert.ok(spawnStub.getCalls().every(call => call.args[2]?.[0] === 'ls'));
                            }
                        }
                        finally {
                            service.dispose();
                        }
                    }
                    finally {
                        removeDirectorySafely(tempDir);
                    }
                });
            }
        }

        for (const configuredTarget of ['csharp-project', 'fsharp-project', 'visualbasic-project', 'external-only-project', 'ordinary-project', 'alias', 'missing', 'directory', 'unreadable', 'unsupported-file'] as const) {
            test(`workspace file discovery validates a configured ${configuredTarget} instead of assuming it is buildable`, async () => {
                const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                try {
                    stubFileSystemWatchers(sandbox);
                    const workspaceRoot = path.join(fixtureRoot, 'workspace');
                    fs.mkdirSync(workspaceRoot);
                    const workspaceFolder = makeWorkspaceFolder(workspaceRoot);
                    const configPath = path.join(workspaceFolder.uri.fsPath, 'aspire.config.json');
                    const configuredExtension = configuredTarget === 'fsharp-project' ? '.fsproj'
                        : configuredTarget === 'visualbasic-project' ? '.vbproj'
                        : configuredTarget === 'unsupported-file' ? '.ts' : '.csproj';
                    const configuredAppHostPath = vscode.Uri.file(path.join(fixtureRoot, 'external', `AppHost${configuredExtension}`)).fsPath;
                    const otherAppHostPath = path.join(workspaceFolder.uri.fsPath, 'Other', 'AppHost.csproj');
                    const hasWorkspaceAppHost = configuredTarget !== 'external-only-project';
                    const expectedCandidates: CandidateAppHostDisplayInfo[] = hasWorkspaceAppHost ? [{
                        path: otherAppHostPath,
                        language: 'csharp',
                        status: 'buildable',
                    }] : [];

                    if (hasWorkspaceAppHost) {
                        fs.mkdirSync(path.dirname(otherAppHostPath), { recursive: true });
                        fs.writeFileSync(otherAppHostPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                    }
                    if (configuredTarget === 'directory') {
                        fs.mkdirSync(configuredAppHostPath, { recursive: true });
                    }
                    else if (configuredTarget === 'alias') {
                        fs.symlinkSync(path.dirname(otherAppHostPath), path.dirname(configuredAppHostPath), 'junction');
                        expectedCandidates[0].selected = true;
                    }
                    else if (configuredTarget !== 'missing') {
                        fs.mkdirSync(path.dirname(configuredAppHostPath), { recursive: true });
                        fs.writeFileSync(configuredAppHostPath, configuredTarget === 'ordinary-project'
                            ? '<Project Sdk="Microsoft.NET.Sdk" />'
                            : '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                    }
                    if (['csharp-project', 'fsharp-project', 'visualbasic-project', 'external-only-project'].includes(configuredTarget)) {
                        for (const candidate of expectedCandidates) {
                            candidate.selected = false;
                        }
                        expectedCandidates.unshift({
                            path: configuredAppHostPath,
                            language: configuredTarget === 'fsharp-project' ? 'fsharp'
                                : configuredTarget === 'visualbasic-project' ? 'visualbasic' : 'csharp',
                            status: 'buildable',
                            selected: true,
                        });
                    }
                    if (configuredTarget === 'unreadable') {
                        const workspaceFs = vscode.workspace.fs;
                        sandbox.stub(vscode.workspace, 'fs').value({
                            ...workspaceFs,
                            readFile: sandbox.stub().callsFake((uri: vscode.Uri) => {
                                if (isSameFileSystemEntry(uri.fsPath, configuredAppHostPath)) {
                                    throw vscode.FileSystemError.NoPermissions(uri);
                                }
                                return workspaceFs.readFile(uri);
                            }),
                        });
                    }
                    fs.writeFileSync(configPath, JSON.stringify({ appHost: { path: configuredAppHostPath } }));
                    findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                        const pattern = typeof include === 'string' ? include : include.pattern;
                        if (pattern.endsWith('aspire.config.json')) {
                            return [vscode.Uri.file(configPath)];
                        }
                        return pattern.endsWith('*.csproj') && hasWorkspaceAppHost ? [vscode.Uri.file(otherAppHostPath)] : [];
                    });
                    sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                        options?.stderrCallback?.('CLI discovery unavailable');
                        options?.exitCallback?.(1);
                        return { kill: () => true } as ReturnType<typeof cliModule.spawnCliProcess>;
                    });
                    const service = new AppHostDiscoveryService(makeTerminalProvider());

                    try {
                        assert.deepStrictEqual(await service.discover(workspaceFolder), expectedCandidates);
                    }
                    finally {
                        service.dispose();
                    }
                }
                finally {
                    removeDirectorySafely(fixtureRoot);
                }
            });
        }

        for (const collision of ['same-file', 'different-apphost', 'different-ordinary-project'] as const) {
            test(`workspace file fallback preserves configured case-only paths for ${collision}`, async () => {
                const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
                try {
                    stubFileSystemWatchers(sandbox);
                    sandbox.stub(process, 'platform').value('win32');
                    const workspaceFolder = makeWorkspaceFolder(path.join(fixtureRoot, 'Repo'));
                    const scannedPath = path.join(workspaceFolder.uri.fsPath, 'AppHost.csproj');
                    const configuredPath = vscode.Uri.file(path.join(fixtureRoot, 'repo', 'AppHost.csproj')).fsPath;
                    const configPath = path.join(workspaceFolder.uri.fsPath, 'aspire.config.json');
                    const appHostContents = '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />';
                    const scannedContents = collision === 'different-ordinary-project'
                        ? '<Project Sdk="Microsoft.NET.Sdk" />' : appHostContents;
                    const scannedFixturePath = path.join(fixtureRoot, 'scanned.csproj');
                    const configuredFixturePath = path.join(fixtureRoot, 'configured.csproj');
                    fs.writeFileSync(scannedFixturePath, scannedContents);
                    fs.writeFileSync(configuredFixturePath, appHostContents);

                    // Simulate per-directory Windows case sensitivity without changing the
                    // host's NTFS settings. Real fixture files supply the native identities.
                    const scannedStats = fs.statSync(scannedFixturePath, { bigint: true });
                    const configuredStats = collision === 'same-file'
                        ? scannedStats : fs.statSync(configuredFixturePath, { bigint: true });
                    const statSync = fs.statSync;
                    sandbox.stub(fs, 'statSync').callsFake((filePath, options) => {
                        if (typeof filePath === 'string') {
                            const resolvedPath = path.resolve(filePath);
                            if (resolvedPath === path.resolve(scannedPath)) {
                                return scannedStats;
                            }
                            if (resolvedPath === path.resolve(configuredPath)) {
                                return configuredStats;
                            }
                        }
                        return statSync(filePath, options);
                    });
                    assert.strictEqual(isSameFileSystemEntry(scannedPath, configuredPath), collision === 'same-file');

                    const contentsByPath = new Map([
                        [configPath, JSON.stringify({ appHost: { path: configuredPath } })],
                        [scannedPath, scannedContents],
                        [configuredPath, appHostContents],
                    ]);
                    const workspaceFs = vscode.workspace.fs;
                    sandbox.stub(vscode.workspace, 'fs').value({
                        ...workspaceFs,
                        readFile: sandbox.stub().callsFake((uri: vscode.Uri) => {
                            const contents = contentsByPath.get(uri.fsPath);
                            return contents === undefined
                                ? workspaceFs.readFile(uri) : Promise.resolve(Buffer.from(contents, 'utf8'));
                        }),
                    });
                    findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                        const pattern = typeof include === 'string' ? include : include.pattern;
                        if (pattern.endsWith('aspire.config.json')) {
                            return [vscode.Uri.file(configPath)];
                        }
                        return pattern.endsWith('*.csproj') ? [vscode.Uri.file(scannedPath)] : [];
                    });
                    sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                        options?.stderrCallback?.('CLI discovery unavailable');
                        options?.exitCallback?.(1);
                        return { kill: () => true } as ReturnType<typeof cliModule.spawnCliProcess>;
                    });
                    const expectedCandidates: CandidateAppHostDisplayInfo[] = collision === 'same-file' ? [{
                        path: scannedPath,
                        language: 'csharp',
                        status: 'buildable',
                        selected: true,
                    }] : [
                        ...(collision === 'different-apphost' ? [{
                            path: scannedPath,
                            language: 'csharp',
                            status: 'buildable',
                            selected: false,
                        }] : []),
                        {
                            path: configuredPath,
                            language: 'csharp',
                            status: 'buildable',
                            selected: true,
                        },
                    ];
                    const service = new AppHostDiscoveryService(makeTerminalProvider());

                    try {
                        assert.deepStrictEqual(await service.discover(workspaceFolder), expectedCandidates);
                    }
                    finally {
                        service.dispose();
                    }
                }
                finally {
                    removeDirectorySafely(fixtureRoot);
                }
            });
        }

        test('does not duplicate configured candidate when differently cased paths identify the same filesystem entry', async () => {
            const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-apphost-discovery-'));
            try {
                stubFileSystemWatchers(sandbox);
                const configPath = path.join(tempDir, 'aspire.config.json');
                const discoveredDirectory = path.join(tempDir, 'AppHost');
                const configuredDirectory = path.join(tempDir, 'apphost');
                const discoveredPath = path.join(discoveredDirectory, 'AppHost.csproj');
                const configuredPath = path.join(configuredDirectory, 'AppHost.csproj');

                fs.mkdirSync(discoveredDirectory);
                fs.writeFileSync(discoveredPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');
                if (!fs.existsSync(configuredDirectory)) {
                    // Case-sensitive test hosts need a case-variant alias to reproduce the same
                    // native identity that default case-insensitive APFS provides directly.
                    fs.symlinkSync(discoveredDirectory, configuredDirectory, 'junction');
                }
                fs.writeFileSync(configPath, JSON.stringify({ appHost: { path: 'apphost/AppHost.csproj' } }));
                findFilesStub.callsFake(async (include: vscode.GlobPattern) => {
                    const pattern = typeof include === 'string' ? include : include.pattern;
                    return pattern.endsWith('aspire.config.json')
                        ? [vscode.Uri.file(configPath)]
                        : [];
                });
                sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                    emitLsOutput(options, [{
                        path: discoveredPath,
                        language: 'csharp',
                        status: 'buildable',
                    }]);
                    return { kill: () => { } } as any;
                });
                const service = new AppHostDiscoveryService(makeTerminalProvider());

                try {
                    const result = await service.discover(makeWorkspaceFolder(tempDir));

                    assert.deepStrictEqual(result, [{
                        path: discoveredPath,
                        language: 'csharp',
                        status: 'buildable',
                        selected: true,
                    }]);
                    const discoveredStat = fs.statSync(discoveredPath, { bigint: true });
                    const configuredStat = fs.statSync(configuredPath, { bigint: true });
                    assert.strictEqual(configuredStat.dev, discoveredStat.dev);
                    assert.strictEqual(configuredStat.ino, discoveredStat.ino);
                }
                finally {
                    service.dispose();
                }
            }
            finally {
                removeDirectorySafely(tempDir);
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
                removeDirectorySafely(tempDir);
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
                removeDirectorySafely(tempDir);
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

    suite('workspace-folder CLI resolution target', () => {
        let sandbox: sinon.SinonSandbox;

        setup(() => {
            sandbox = sinon.createSandbox();
            stubFileSystemWatchers(sandbox);
        });

        teardown(() => {
            sandbox.restore();
        });

        test('resolves the CLI path and capability probe with the workspace folder target', async () => {
            const workspaceFolder = makeWorkspaceFolder(buildPath('workspace'));
            const resolveAspireCliPathStub = sandbox.stub().resolves({ cliPath: '/repo/a/bin/aspire', available: true, source: 'configured' });
            const terminalProvider = {
                resolveAspireCliPath: resolveAspireCliPathStub,
                createEnvironment: () => ({}),
            } as unknown as AspireTerminalProvider;
            const getConfigInfoStub = sandbox.stub().resolves({ capabilities: [] });
            const fakeConfigInfoProvider = { getConfigInfo: getConfigInfoStub } as unknown as configInfoProvider.ConfigInfoProvider;
            sandbox.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                options?.stdoutCallback?.(JSON.stringify([]));
                options?.exitCallback?.(0);
                return { kill: () => true } as any;
            });
            const service = new AppHostDiscoveryService(terminalProvider, fakeConfigInfoProvider);

            try {
                await service.discover(workspaceFolder);

                assert.ok(resolveAspireCliPathStub.calledOnceWith(workspaceFolderCliPathTarget(workspaceFolder)));
                assert.strictEqual(getConfigInfoStub.calledOnce, true);
                const configInfoOptions = getConfigInfoStub.firstCall.args[0];
                assert.strictEqual(configInfoOptions.cliPath, '/repo/a/bin/aspire');
                assert.deepStrictEqual(configInfoOptions.target, workspaceFolderCliPathTarget(workspaceFolder));
            }
            finally {
                service.dispose();
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
    const terminalProvider = {
        getAspireCliExecutablePath: async () => 'aspire',
        resolveAspireCliPath: async (target: CliPathResolutionTarget) => ({
            cliPath: await terminalProvider.getAspireCliExecutablePath(target),
            available: true,
            source: 'path',
        }),
        createEnvironment: () => ({}),
    } as unknown as AspireTerminalProvider;
    return terminalProvider;
}

function makeCancellationToken(): vscode.CancellationToken {
    return {
        isCancellationRequested: false,
        onCancellationRequested: () => ({ dispose: () => { } }),
    };
}

async function waitForMicrotasks(): Promise<void> {
    // Flush enough microtask ticks for the multi-await discovery startup path
    // (CLI path -> config-info capability probe -> aspire ls) to reach process spawn.
    // Avoid a real timer because several tests install sinon fake timers.
    for (let i = 0; i < 20; i++) {
        await Promise.resolve();
    }
}

function emitLsStream(options: cliModule.SpawnProcessOptions | undefined, entries: readonly unknown[]): void {
    for (const entry of entries) {
        options?.lineCallback?.(JSON.stringify(entry));
    }

    options?.exitCallback?.(0);
}

function makeCompletedCliProcess(): ChildProcessWithoutNullStreams {
    const stdio: ChildProcessWithoutNullStreams['stdio'] = [new PassThrough(), new PassThrough(), new PassThrough(), undefined, undefined];
    return Object.assign(new ChildProcess(), {
        stdin: stdio[0],
        stdout: stdio[1],
        stderr: stdio[2],
        stdio,
        exitCode: 0,
    });
}

function emitLsOutput(options: cliModule.SpawnProcessOptions | undefined, entries: readonly unknown[]): void {
    if (options?.lineCallback) {
        emitLsStream(options, entries);
        return;
    }

    options?.stdoutCallback?.(JSON.stringify(entries));
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

    public constructor(private readonly _files: ReadonlyMap<string, string | Error>) {
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
            size: contents instanceof Error ? 0 : Buffer.byteLength(contents, 'utf8'),
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
        if (contents instanceof Error) {
            throw contents;
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
