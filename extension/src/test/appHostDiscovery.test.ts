/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliModule from '../debugger/languages/cli';
import { AppHostDiscoveryService, findCandidateForEditorFile, findConfiguredAppHostPaths, getDebugTargetForCandidate, selectWorkspaceAppHostPath } from '../utils/appHostDiscovery';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';

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

                await service.discover(workspaceFolder);
                assert.strictEqual(spawnStub.callCount, 1);
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

        test('configured AppHost path search excludes git worktree folders', async () => {
            await findConfiguredAppHostPaths(makeWorkspaceFolder(buildPath('workspace')));

            const excludePatterns = findFilesStub.getCalls().map(call => String(call.args[1]));
            assert.ok(excludePatterns.length > 0);
            assert.ok(excludePatterns.every(pattern => pattern.includes('**/.worktrees/**')));
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
