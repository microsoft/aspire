import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as ts from 'typescript';
import * as vm from 'vm';
import { runE2eTeardown } from '../test-e2e/helpers/fixtures';
import type { RunProcessOptions } from '../test-e2e/helpers/process';
import type { ProcessEntry } from '../test-e2e/helpers/processArguments';
import type { AspireExtensionE2EControlCommand } from '../types/extensionApi';
import { commandLineArgumentEquals } from './helpers/processArguments';

suite('Launch profiles E2E fixture', () => {
    test('selects Deno before scaffolding without creating a brownfield package', async () => {
        const harness = createHarness();
        harness.onInit = directory => {
            assert.deepStrictEqual(JSON.parse(harness.readFile(path.join(directory, 'deno.json'))), {});
            assert.strictEqual(harness.files.has(path.join(directory, 'package.json')), false);
        };

        await harness.runDenoTest();

        assert.deepStrictEqual(harness.processCalls, [{
            file: 'aspire-test',
            args: ['init', '--language', 'typescript', '--non-interactive', '--suppress-agent-init'],
            cwd: harness.denoDirectory,
        }]);
        assert.ok(harness.files.has(harness.denoAppHostPath));
        assert.strictEqual(harness.selectedAppHost, harness.denoAppHostPath);
    });

    for (const scaffoldWritten of [false, true]) {
        test(`cleans up failed initialization ${scaffoldWritten ? 'after' : 'before'} scaffolding without contacting the bridge`, async () => {
            const harness = createHarness();
            const initError = new Error('aspire init timed out');
            harness.onInit = directory => {
                if (scaffoldWritten) {
                    harness.files.set(path.join(directory, 'apphost.mts'), '');
                }
                throw initError;
            };

            await assert.rejects(harness.runDenoTest(), error => error === initError);
            await harness.runTeardown();

            assert.deepStrictEqual(harness.commands, []);
            assert.deepStrictEqual(harness.stateWaits, []);
            assert.strictEqual(harness.files.has(harness.denoAppHostPath), false);
            assert.ok(harness.removedPaths.includes(harness.denoDirectory));
        });
    }

    test('waits for the CLI and unregistered Deno process before deleting a failed startup fixture', async () => {
        const harness = createHarness();
        const startupError = new Error('debug AppHost startup timed out');
        harness.onStartup = () => { throw startupError; };
        let finishExit!: () => void;
        let startedWaiting!: () => void;
        const exit = new Promise<void>(resolve => { finishExit = resolve; });
        const waiting = new Promise<void>(resolve => { startedWaiting = resolve; });
        harness.onProcessExit = async () => {
            startedWaiting();
            await exit;
        };
        await assert.rejects(harness.runDenoTest(), error => error === startupError);

        const teardown = harness.runTeardown();
        try {
            await Promise.race([
                waiting,
                teardown.then(() => { throw new Error('Teardown completed before waiting for owned processes.'); }),
            ]);
            assert.deepStrictEqual(harness.waitedPids, [101, 202]);
            assert.strictEqual(harness.removedPaths.includes(harness.denoDirectory), false);
        }
        finally {
            finishExit();
        }
        await teardown;

        assert.deepStrictEqual(harness.commands.map(command => command.name), ['debugAppHost', 'stopDebugging']);
        assert.ok(harness.removedPaths.includes(harness.denoDirectory));
        assert.strictEqual(harness.selectedAppHost, undefined);
    });

    test('resets lifecycle tracking before a subsequent initialization failure', async () => {
        const harness = createHarness();
        await harness.runDenoTest();
        await harness.runTeardown();
        harness.commands.length = 0;
        harness.waitedPids.length = 0;
        const initError = new Error('subsequent aspire init failed');
        harness.onInit = () => { throw initError; };

        await assert.rejects(harness.runDenoTest(), error => error === initError);
        await harness.runTeardown();

        assert.deepStrictEqual(harness.commands, []);
        assert.deepStrictEqual(harness.waitedPids, []);
        assert.strictEqual(harness.files.has(harness.denoAppHostPath), false);
    });

    test('matches fixture process paths case-insensitively on Windows without matching neighboring paths', async () => {
        const harness = createHarness('win32');
        harness.processEntries = [
            { pid: 101, commandLine: '', arguments: ['aspire', 'run', '--apphost', harness.denoAppHostPath.toLowerCase()] },
            { pid: 202, commandLine: '', arguments: ['deno', 'run', harness.denoAppHostPath.toUpperCase()] },
            { pid: 303, commandLine: '', arguments: ['deno', 'run', `${harness.denoAppHostPath}.backup`] },
            { pid: 404, commandLine: '', arguments: ['unrelated', `prefix${harness.denoAppHostPath}`] },
        ];
        await harness.runDenoTest();

        await harness.runTeardown();

        assert.deepStrictEqual(harness.waitedPids, [101, 202]);
        assert.ok(harness.removedPaths.includes(harness.denoDirectory));
    });

    for (const failurePhase of ['process discovery', 'stop', 'process exit'] as const) {
        test(`retains the fixture and restores selection when ${failurePhase} fails`, async () => {
            const harness = createHarness();
            const failure = new Error(`${failurePhase} failed`);
            await harness.runDenoTest();
            if (failurePhase === 'process discovery') {
                harness.onProcessDiscovery = () => { throw failure; };
            }
            else if (failurePhase === 'stop') {
                harness.onStop = () => { throw failure; };
            }
            else {
                harness.onProcessExit = async () => { throw failure; };
            }

            await assert.rejects(harness.runTeardown(), new RegExp(failure.message));

            assert.deepStrictEqual(harness.commands.map(command => command.name), ['debugAppHost', 'stopDebugging']);
            assert.strictEqual(harness.selectedAppHost, undefined);
            assert.strictEqual(harness.removedPaths.includes(harness.denoDirectory), false);
            assert.ok(harness.files.has(harness.denoAppHostPath));
        });
    }
});

type FixtureCallback = (this: { timeout(milliseconds: number): void }) => void | Promise<void>;

function createHarness(platform = process.platform) {
    const workspaceRoot = path.resolve('launch-profile-fixture');
    const denoDirectory = path.join(workspaceRoot, 'DenoAppHost');
    const denoAppHostPath = path.join(denoDirectory, 'apphost.mts');
    const files = new Map<string, string>();
    const directories = new Set<string>();
    const tests = new Map<string, FixtureCallback>();
    const setups: FixtureCallback[] = [];
    const teardowns: FixtureCallback[] = [];
    const context = { timeout: (_milliseconds: number) => { } };
    const harness = {
        denoDirectory,
        denoAppHostPath,
        files,
        commands: [] as AspireExtensionE2EControlCommand[],
        stateWaits: [] as string[],
        processCalls: [] as { file: string; args: string[]; cwd: string | undefined }[],
        removedPaths: [] as string[],
        waitedPids: [] as number[],
        processEntries: [
            { pid: 101, commandLine: '', arguments: ['aspire', 'run', '--apphost', denoAppHostPath] },
            { pid: 202, commandLine: '', arguments: ['deno', 'run', '--inspect-wait=127.0.0.1:12345', '-A', denoAppHostPath] },
        ] as ProcessEntry[],
        selectedAppHost: undefined as string | undefined,
        onInit: (_directory: string) => { },
        onStartup: () => { },
        onStop: () => { },
        onProcessDiscovery: () => { },
        onProcessExit: async () => { },
        readFile(filePath: string): string {
            const content = files.get(filePath);
            assert.ok(content !== undefined, `Expected fixture file ${filePath}`);
            return content;
        },
        async runDenoTest(): Promise<void> {
            for (const setup of setups) {
                await setup.call(context);
            }
            const test = tests.get('debugs a Deno AppHost through the built-in JavaScript debugger');
            assert.ok(test);
            await test.call(context);
        },
        async runTeardown(): Promise<void> {
            for (const teardown of teardowns) {
                await teardown.call(context);
            }
        },
    };
    const removePath = (targetPath: string) => {
        harness.removedPaths.push(targetPath);
        for (const filePath of files.keys()) {
            if (filePath === targetPath || filePath.startsWith(targetPath + path.sep)) {
                files.delete(filePath);
            }
        }
        directories.delete(targetPath);
    };

    // Execute the real spec callbacks with controlled external operations. This exercises failed
    // setup and teardown ordering without starting VS Code or waiting for the E2E timeouts.
    const sourcePath = path.resolve(__dirname, '..', '..', 'src', 'test-e2e', 'launchProfiles.e2e.test.ts');
    const source = ts.createSourceFile(sourcePath, fs.readFileSync(sourcePath, 'utf8'), ts.ScriptTarget.Latest, true);
    const body = source.statements.filter(statement => !ts.isImportDeclaration(statement))
        .map(statement => statement.getText(source)).join('\n');
    const compiled = ts.transpileModule(body, {
        compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
    }).outputText;
    vm.runInNewContext(compiled, {
        assert,
        path,
        process: { platform },
        fs: {
            mkdirSync: (directory: string) => directories.add(directory),
            existsSync: (filePath: string) => files.has(filePath) || directories.has(filePath),
            readFileSync: harness.readFile,
            rmSync: removePath,
            rmdirSync: removePath,
        },
        suite: (_name: string, callback: FixtureCallback) => callback.call(context),
        setup: (callback: FixtureCallback) => setups.push(callback),
        teardown: (callback: FixtureCallback) => teardowns.push(callback),
        test: (name: string, callback: FixtureCallback) => tests.set(name, callback),
        getWorkspaceRoot: () => workspaceRoot,
        getPrimaryAppHostProjectPath: () => path.join(workspaceRoot, 'AppHost', 'AppHost.csproj'),
        getCliPath: () => 'aspire-test',
        runProcess: async (file: string, args: string[], options: RunProcessOptions) => {
            harness.processCalls.push({ file, args: [...args], cwd: options.cwd });
            if (args[0] === 'init') {
                assert.ok(options.cwd);
                harness.onInit(options.cwd);
                files.set(path.join(options.cwd, 'apphost.mts'), '');
                files.set(path.join(options.cwd, 'package.json'), '{}');
            }
        },
        writeFileWithRetry: (filePath: string, content: string) => files.set(filePath, content),
        writeWorkspaceAppHostConfigForPath: (appHostPath: string) => { harness.selectedAppHost = appHostPath; },
        restoreWorkspaceAppHostConfig: () => { harness.selectedAppHost = undefined; },
        openAspireView: async () => { },
        waitForSelectedWorkspaceAppHost: async () => { },
        getCommandInvocationCount: () => 0,
        executeE2eControlCommand: async (command: AspireExtensionE2EControlCommand) => {
            harness.commands.push(command);
            if (command.name === 'stopDebugging') {
                harness.onStop();
            }
            return { result: {} };
        },
        waitForCommandOutcome: async () => { },
        waitForDebugSessionStartup: async () => { harness.onStartup(); },
        waitForNoDebugSessions: async () => { harness.stateWaits.push('debug sessions'); },
        waitForNoRunningAppHost: async () => { harness.stateWaits.push('running AppHost'); },
        stopAppHostIfRunning: async () => { harness.commands.push({ name: 'stopDebugging' }); },
        listProcessEntries: async (commandLineHint?: string): Promise<ProcessEntry[]> => {
            harness.onProcessDiscovery();
            return commandLineHint === undefined
                ? harness.processEntries
                : harness.processEntries.filter(entry => entry.arguments.includes(commandLineHint));
        },
        commandLineArgumentEquals: (actual: string, expected: string) => commandLineArgumentEquals(actual, expected, platform),
        waitForKnownProcessExit: async (pid: number) => {
            harness.waitedPids.push(pid);
            await harness.onProcessExit();
        },
        removePath,
        runE2eTeardown,
    });

    assert.strictEqual(tests.size, 3);
    return harness;
}
