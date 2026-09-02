import * as assert from 'assert';
import childProcess = require('child_process');
import { EventEmitter } from 'events';
import * as nodePath from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createProjectDebuggerExtension, createProjectResourceAttachProvider, DotNetService, projectDebuggerExtension, quoteCommandLineArgument } from '../debugger/languages/dotnet';
import { AspireExtendedDebugConfiguration, AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, ProjectLaunchConfiguration } from '../dcp/types';
import * as io from '../utils/io';
import { createDebugSessionConfiguration, ResourceDebuggerExtension } from '../debugger/debuggerExtensions';
import type { ResourceAttachProvider } from '../debugger/resourceDebugContracts';
import { AppHostParentOutputFilter, AspireDebugSession } from '../debugger/AspireDebugSession';
import * as hotReload from '../debugger/hotReload';
import * as cliProcess from '../utils/process/cliProcess';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    LaunchedChildProcessResolver,
    type LaunchedChildProcess,
    type LaunchedChildProcessClock,
    type LaunchedChildProcessQuery,
} from '../debugger/launchedChildProcessDiscovery';
import * as cliPathModule from '../utils/cliPath';
import * as cliPathEnvironmentModule from '../utils/cliPathEnvironment';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

import { removeDirectorySafely } from './testHelpers';
class TestDotNetService {
    private _hasDevKit: boolean;

    public getDotNetAttachTargetInfoStub: sinon.SinonStub;
    public getDotNetTargetPathStub: sinon.SinonStub;
    public buildDotNetProjectStub: sinon.SinonStub;

    // `dotnet run-api` output returned for file-based (.cs) apps. Tests override this with a serialized
    // RunCommand payload; the default empty string mirrors the not-configured case.
    public runApiOutput: string = '';
    public runApiEnvironment: NodeJS.ProcessEnv | undefined;

    constructor(outputPath: string, rejectBuild: Error | null, hasDevKit: boolean) {
        this.getDotNetAttachTargetInfoStub = sinon.stub();
        this.getDotNetAttachTargetInfoStub.resolves({ targetPath: outputPath, useAppHost: true });

        this.getDotNetTargetPathStub = sinon.stub();
        this.getDotNetTargetPathStub.resolves(outputPath);

        this.buildDotNetProjectStub = sinon.stub();
        if (rejectBuild) {
            this.buildDotNetProjectStub.rejects(rejectBuild);
        } else {
            this.buildDotNetProjectStub.resolves();
        }

        this._hasDevKit = hasDevKit;
    }

    getDotNetAttachTargetInfo(projectFile: string, configuration?: string, cancellationToken?: vscode.CancellationToken, framework?: string): Promise<{ targetPath: string, targetName?: string, useAppHost: boolean }> {
        return framework
            ? this.getDotNetAttachTargetInfoStub(projectFile, configuration, cancellationToken, framework)
            : cancellationToken
                ? this.getDotNetAttachTargetInfoStub(projectFile, configuration, cancellationToken)
                : this.getDotNetAttachTargetInfoStub(projectFile, configuration);
    }

    getDotNetTargetPath(projectFile: string): Promise<string> {
        return this.getDotNetTargetPathStub(projectFile);
    }

    buildDotNetProject(projectFile: string): Promise<void> {
        return this.buildDotNetProjectStub(projectFile);
    }

    getAndActivateDevKit(): Promise<boolean> {
        return Promise.resolve(this._hasDevKit);
    }

    getDotNetRunApiOutput(projectPath: string, environment?: NodeJS.ProcessEnv): Promise<string> {
        this.runApiEnvironment = environment;
        return Promise.resolve(this.runApiOutput);
    }
}

function createMsbuildProcess(): {
    process: childProcess.ChildProcessWithoutNullStreams;
    stdout: EventEmitter & { setEncoding(encoding: string): void };
    stderr: EventEmitter & { setEncoding(encoding: string): void };
    kill: sinon.SinonStub;
} {
    const process = new EventEmitter() as unknown as childProcess.ChildProcessWithoutNullStreams;
    const stdout = Object.assign(new EventEmitter(), {
        setEncoding: (_encoding: string) => { },
    });
    const stderr = Object.assign(new EventEmitter(), {
        setEncoding: (_encoding: string) => { },
    });
    const kill = sinon.stub().callsFake((signal?: NodeJS.Signals | number) => {
        (process as unknown as { killed: boolean }).killed = true;
        process.emit('close', null, signal);
        return true;
    });
    Object.assign(process, {
        exitCode: null,
        signalCode: null,
        killed: false,
        pid: 1234,
        kill,
        stdout,
        stderr,
    });

    return { process, stdout, stderr, kill };
}

interface TestLaunchedChildProcess {
    readonly pid: number;
    readonly parentPid: number;
    readonly executable: string;
    readonly command: string;
    readonly commandLineArguments?: readonly string[];
}

interface TestLaunchedChildProcessIdentity {
    readonly requiresDirectChild?: boolean;
    isLauncher(process: TestLaunchedChildProcess): boolean;
    isCandidate(process: TestLaunchedChildProcess): boolean;
}

interface TestLaunchedChildProcessResolver {
    resolveProcessId(
        launcherPid: number,
        identity: TestLaunchedChildProcessIdentity,
        cancellationToken?: vscode.CancellationToken,
    ): Promise<number>;
}

class StaticLaunchedChildProcessQuery implements LaunchedChildProcessQuery {
    constructor(private readonly _processes: readonly LaunchedChildProcess[]) {
    }

    async listProcesses(): Promise<readonly LaunchedChildProcess[]> {
        return this._processes;
    }
}

const immediateProcessClock: LaunchedChildProcessClock = {
    now: () => 0,
    sleep: async () => { },
};

function createLaunchedProcess(pid: number, parentPid: number, executable: string, command = executable): LaunchedChildProcess {
    return { pid, parentPid, executable, command };
}

type TestResource = Parameters<ResourceAttachProvider['createDebugConfiguration']>[0];

function createProjectResource(
    properties: TestResource['properties'],
    name = 'api',
    displayName = 'API',
): TestResource {
    return {
        name,
        displayName,
        resourceType: 'Project',
        state: 'Running',
        properties,
    };
}

function createAttachProvider(
    dotNetService: TestDotNetService,
    childProcessResolver: TestLaunchedChildProcessResolver,
    fileSystem?: { realpath(path: string): Promise<string> },
): ResourceAttachProvider {
    const factory = createProjectResourceAttachProvider as unknown as (
        dotNetServiceProducer: () => TestDotNetService,
        resolver: TestLaunchedChildProcessResolver,
        fileSystem?: { realpath(path: string): Promise<string> },
    ) => ResourceAttachProvider;

    return factory(() => dotNetService, childProcessResolver, fileSystem);
}

suite('Dotnet Debugger Extension Tests', () => {
    let getHotReloadDiagnostics: sinon.SinonStub;
    let logHotReloadDiagnostics: sinon.SinonStub;
    let showHotReloadDisabledAdvisory: sinon.SinonStub;

    setup(() => {
        getHotReloadDiagnostics = sinon.stub(hotReload, 'getHotReloadDiagnostics').returns({
            devKitInstalled: true,
            workspaceTrusted: true,
            settingContributed: true,
            settingEnabled: false,
            reloadOnSaveEnabled: true
        });
        logHotReloadDiagnostics = sinon.stub(hotReload, 'logHotReloadDiagnostics');
        showHotReloadDisabledAdvisory = sinon.stub(hotReload, 'showHotReloadDisabledAdvisoryIfNeeded').resolves();
    });

    teardown(() => sinon.restore());

    function createDebuggerExtension(outputPath: string, rejectBuild: Error | null, hasDevKit: boolean, doesOutputFileExist: boolean): { dotNetService: TestDotNetService, extension: ResourceDebuggerExtension, attachProvider: ResourceAttachProvider, doesFileExistStub: sinon.SinonStub } {
        const fakeDotNetService = new TestDotNetService(outputPath, rejectBuild, hasDevKit);
        const childProcessResolver: TestLaunchedChildProcessResolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        return {
            dotNetService: fakeDotNetService,
            extension: createProjectDebuggerExtension(() => fakeDotNetService),
            attachProvider: createProjectResourceAttachProvider(() => fakeDotNetService, childProcessResolver),
            doesFileExistStub: sinon.stub(io, 'doesFileExist').resolves(doesOutputFileExist),
        };
    }

    test('attach configuration resolves the evaluated framework-dependent TargetPath child PID', async () => {
        const { dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/EvaluatedAssemblyName.dll', null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({
            targetPath: '/repo/bin/Debug/net10.0/EvaluatedAssemblyName.dll',
            useAppHost: false,
        });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        const configuration = await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
            'project.launchCommand': 'run',
        }));

        assert.deepStrictEqual(configuration, {
            type: 'coreclr',
            request: 'attach',
            name: 'Attach debugger: API',
            processId: 4321,
        });
        assert.strictEqual(resolver.resolveProcessId.firstCall.args[0], 1234);
        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isLauncher({
            pid: 1234,
            parentPid: 1,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet run --project /repo/api/Api.csproj',
        }), true);
        assert.strictEqual(processIdentity.isLauncher({
            pid: 1234,
            parentPid: 1,
            executable: '/usr/local/share',
            command: '/usr/local/share/dotnet/dotnet run --project /repo/api/Api.csproj',
        }), false);
        assert.strictEqual(processIdentity.requiresDirectChild, true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec /repo/bin/Debug/net10.0/EvaluatedAssemblyName.dll',
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec /repo/bin/Debug/net10.0/Api.dll',
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec /repo/OneDrive - Microsoft/über-long-path/My Attach Service.dll "" --urls http://localhost:5000',
        }), false);
    });

    test('watch attach permits a transitive TargetPath descendant', async () => {
        const targetPath = '/repo/bin/Debug/net10.0/Api.dll';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: false });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
            'project.launchCommand': 'watch',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.requiresDirectChild, false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 5678,
            executable: '/usr/local/share/dotnet/dotnet',
            command: `dotnet exec ${targetPath}`,
        }), true);
    });

    test('matches a spaced evaluated TargetPath from the raw framework-dependent command without matching a prefix sibling', async () => {
        const targetPath = '/repo/OneDrive - Microsoft/über-long-path/My Attach Service.dll';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: false });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: `dotnet exec ${targetPath} "" --urls http://localhost:5000`,
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: `dotnet exec ${targetPath}.bak`,
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: `dotnet run ${targetPath}`,
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4324,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: `dotnet exec /repo/bin/Debug/net10.0/Other.dll ${targetPath}`,
        }), false);
    });

    test('matches a structured framework-dependent TargetPath without accepting other dotnet children', async () => {
        const targetPath = '/repo/OneDrive - Microsoft/über-long-path/My Attach Service.dll';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: false });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec malformed-posix-command',
            commandLineArguments: ['dotnet', 'exec', targetPath, '', '--urls', 'http://localhost:5000'],
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec malformed-posix-command',
            commandLineArguments: ['dotnet', 'exec', `${targetPath}.bak`],
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec malformed-posix-command',
            commandLineArguments: [
                'dotnet',
                'exec',
                '/repo/bin/Debug/net10.0/Other.dll',
                targetPath,
            ],
        }), false);
    });

    test('older redacted framework-dependent snapshots match TargetName instead of the default TargetPath', async () => {
        const { dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({
            targetPath: '/repo/bin/Debug/net10.0/Api.dll',
            targetName: 'Api',
            useAppHost: false,
        });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'executable.args': null,
            'project.path': '/repo/api/Api.csproj',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.requiresDirectChild, true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec /repo/bin/Release/net10.0/Api.dll --urls http://localhost:5000',
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec "/repo/bin/Release/net10.0/Api.dll" --urls http://localhost:5000',
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec /repo/bin/Release/net10.0/Api.Worker.dll',
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4324,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec /repo/bin/Release/net10.0/Other.dll /repo/app-arguments/Api.dll',
        }), false);
    });

    test('older structured framework-dependent snapshots use the first DLL target after dotnet exec', async () => {
        const { dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({
            targetPath: '/repo/bin/Debug/net10.0/Api.dll',
            targetName: 'Api',
            useAppHost: false,
        });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'executable.args': null,
            'project.path': '/repo/api/Api.csproj',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec malformed-posix-command',
            commandLineArguments: [
                'dotnet',
                'exec',
                '/repo/bin/Release/net10.0/Other.dll',
                '/repo/app-arguments/Api.dll',
            ],
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/usr/local/share/dotnet/dotnet',
            command: 'dotnet exec malformed-posix-command',
            commandLineArguments: [
                'dotnet',
                'exec',
                '/repo/bin/Release/net10.0/Api.dll',
                '/repo/app-arguments/Other.dll',
            ],
        }), true);
    });

    test('older redacted apphost snapshots match the TargetName executable basename', async () => {
        const { dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({
            targetPath: '/repo/bin/Debug/net10.0/Api.dll',
            targetName: 'Api',
            useAppHost: true,
        });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'executable.args': null,
            'project.path': '/repo/api/Api.csproj',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: '/repo/bin/Release/net10.0/Api',
            command: '/repo/bin/Release/net10.0/Api',
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/repo/bin/Release/net10.0/Api.Worker',
            command: '/repo/bin/Release/net10.0/Api.Worker',
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: '/repo/bin/Release/net10.0/api',
            command: '/repo/bin/Release/net10.0/api',
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4324,
            parentPid: 1234,
            executable: 'C:\\repo\\bin\\Release\\net10.0\\API.EXE',
            command: 'C:\\repo\\bin\\Release\\net10.0\\API.EXE',
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4325,
            parentPid: 1234,
            executable: 'API.EXE',
            command: 'API.EXE',
        }), true);
    });

    test('older TargetName fallback remains scoped to the selected launcher tree', async () => {
        const targetPath = '/repo/bin/Debug/net10.0/Api.dll';
        const dotNetService = new TestDotNetService(targetPath, null, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({
            targetPath,
            targetName: 'Api',
            useAppHost: false,
        });
        const resolver = new LaunchedChildProcessResolver(
            new StaticLaunchedChildProcessQuery([
                createLaunchedProcess(1234, 1, '/usr/local/share/dotnet/dotnet', 'dotnet run --project /repo/api/Api.csproj'),
                createLaunchedProcess(4321, 1234, '/usr/local/share/dotnet/dotnet', 'dotnet exec /repo/bin/Release/net10.0/Api.dll'),
                createLaunchedProcess(5678, 1, '/usr/local/share/dotnet/dotnet', 'dotnet run --project /repo/api/Api.csproj'),
                createLaunchedProcess(8765, 5678, '/usr/local/share/dotnet/dotnet', 'dotnet exec /repo/bin/Release/net10.0/Api.dll'),
            ]),
            immediateProcessClock,
            { timeoutMs: 20, retryDelayMs: 10 });
        const attachProvider = createAttachProvider(dotNetService, resolver);

        const configuration = await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'executable.args': null,
            'project.path': '/repo/api/Api.csproj',
        }));

        assert.strictEqual(configuration.processId, 4321);
    });

    test('older TargetName fallback fails closed when MSBuild omits TargetName', async () => {
        const { dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({
            targetPath: '/repo/bin/Debug/net10.0/Api.dll',
            useAppHost: false,
        });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await assert.rejects(
            attachProvider.createDebugConfiguration(createProjectResource({
                'executable.pid': '1234',
                'executable.path': 'dotnet',
                'executable.args': null,
                'project.path': '/repo/api/Api.csproj',
            })),
            (error: unknown) => error instanceof Error
                && error.message === 'This resource cannot be attached to a debugger.');

        assert.strictEqual(resolver.resolveProcessId.called, false);
    });

    test('older TargetName fallback rejects present invalid safe metadata', async () => {
        const dotNetService = new TestDotNetService('/repo/bin/Debug/net10.0/Api.dll', null, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({
            targetPath: '/repo/bin/Debug/net10.0/Api.dll',
            targetName: 'Api',
            useAppHost: false,
        });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);
        const invalidProperties = [
            { label: 'empty configuration', propertyName: 'project.configuration', propertyValue: '' },
            { label: 'null target framework', propertyName: 'project.targetFramework', propertyValue: null },
            { label: 'non-string configuration', propertyName: 'project.configuration', propertyValue: 42 },
        ] as const;

        for (const [index, { label, propertyName, propertyValue }] of invalidProperties.entries()) {
            const resourceName = `api-${index}`;
            await assert.rejects(
                attachProvider.createDebugConfiguration(createProjectResource({
                    'executable.pid': '1234',
                    'executable.path': 'dotnet',
                    'executable.args': null,
                    'project.path': '/repo/api/Api.csproj',
                    [propertyName]: propertyValue,
                }, resourceName)),
                (error: unknown) => error instanceof Error
                    && error.message === `Invalid launch configuration for ${resourceName}.`,
                label);
        }

        assert.strictEqual(dotNetService.getDotNetAttachTargetInfoStub.called, false);
        assert.strictEqual(resolver.resolveProcessId.called, false);
    });

    test('attach configuration resolves an apphost child by its evaluated executable identity', async () => {
        sinon.stub(process, 'platform').value('linux');
        const targetPath = '/repo/OneDrive - Microsoft/über-long-path/My Attach Service';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: true });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        const configuration = await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': 1234,
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        assert.strictEqual(configuration.processId, 4321);
        assert.strictEqual(configuration.processName, undefined);
        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: targetPath,
            command: `"${targetPath}" "" --urls http://localhost:5000`,
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/repo/OneDrive',
            command: `"${targetPath} Worker"`,
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: '/repo/OneDrive',
            command: `${targetPath} Worker`,
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4324,
            parentPid: 1234,
            executable: '/repo/OneDrive',
            command: `"${targetPath}"`,
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4325,
            parentPid: 1234,
            executable: `${targetPath} Worker`,
            command: `${targetPath} Worker`,
        }), false);
    });

    test('matches a long macOS apphost path only through the exact executable identity', async () => {
        sinon.stub(process, 'platform').value('darwin');
        const targetPath = '/repo/OneDrive - Microsoft/über-long-path/My Attach Service With A Long Name';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: true });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': 1234,
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: targetPath,
            command: `${targetPath} --urls http://localhost:5000`,
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/repo/OneDrive -',
            command: targetPath,
        }), false);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: `${targetPath} Worker`,
            command: targetPath,
        }), false);
    });

    test('preserves raw and full-realpath apphost candidates', async () => {
        const targetPath = '/workspace/link/bin/Debug/net10.0/Api.dll';
        const appHostPath = '/workspace/link/bin/Debug/net10.0/Api';
        const appHostExePath = `${appHostPath}.exe`;
        const canonicalAppHostPath = '/workspace/physical/bin/Debug/net10.0/Api';
        const canonicalAppHostExePath = `${canonicalAppHostPath}.exe`;
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const realpath = sinon.stub().callsFake(async (candidate: string) => {
            if (candidate === appHostPath) {
                return canonicalAppHostPath;
            }
            if (candidate === appHostExePath) {
                return canonicalAppHostExePath;
            }

            throw new Error('ENOENT');
        });
        const attachProvider = createAttachProvider(dotNetService, resolver, { realpath });

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/workspace/link/api/Api.csproj',
        }));

        const appHostIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: canonicalAppHostPath,
            command: canonicalAppHostPath,
        }), true);
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: appHostPath,
            command: appHostPath,
        }), true);
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4323,
            parentPid: 1234,
            executable: canonicalAppHostExePath,
            command: canonicalAppHostExePath,
        }), true);
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4324,
            parentPid: 1234,
            executable: appHostExePath,
            command: appHostExePath,
        }), true);
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4325,
            parentPid: 1234,
            executable: '/workspace/other/bin/Debug/net10.0/Api',
            command: '/workspace/other/bin/Debug/net10.0/Api',
        }), false);
        assert.ok(realpath.calledWithExactly(appHostPath));
        assert.ok(realpath.calledWithExactly(appHostExePath));
    });

    test('matches a deleted apphost from a symlinked TargetPath directory', async () => {
        const targetPath = '/workspace/link/bin/Debug/net10.0/Api.dll';
        const appHostPath = '/workspace/link/bin/Debug/net10.0/Api';
        const appHostExePath = `${appHostPath}.exe`;
        const targetDirectory = nodePath.dirname(targetPath);
        const canonicalTargetDirectory = '/workspace/physical/bin/Debug/net10.0';
        const canonicalAppHostPath = nodePath.join(canonicalTargetDirectory, nodePath.basename(appHostPath));
        const canonicalAppHostExePath = nodePath.join(canonicalTargetDirectory, nodePath.basename(appHostExePath));
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const realpath = sinon.stub().callsFake(async (candidate: string) => {
            if (candidate === targetDirectory) {
                return canonicalTargetDirectory;
            }

            throw new Error('ENOENT');
        });
        const attachProvider = createAttachProvider(dotNetService, resolver, { realpath });

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/workspace/link/api/Api.csproj',
        }));

        const appHostIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: canonicalAppHostPath,
            command: canonicalAppHostPath,
        }), true);
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: canonicalAppHostExePath,
            command: canonicalAppHostExePath,
        }), true);
        assert.ok(realpath.calledWithExactly(appHostPath));
        assert.ok(realpath.calledWithExactly(targetDirectory));
    });

    test('does not match a same-named apphost outside the canonical TargetPath directory', async () => {
        const targetPath = '/workspace/link/bin/Debug/net10.0/Api.dll';
        const targetDirectory = nodePath.dirname(targetPath);
        const canonicalTargetDirectory = '/workspace/physical/bin/Debug/net10.0';
        const unrelatedAppHostPath = '/workspace/other/bin/Debug/net10.0/Api';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const realpath = sinon.stub().callsFake(async (candidate: string) => {
            if (candidate === targetDirectory) {
                return canonicalTargetDirectory;
            }

            throw new Error('ENOENT');
        });
        const attachProvider = createAttachProvider(dotNetService, resolver, { realpath });

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/workspace/link/api/Api.csproj',
        }));

        const appHostIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: unrelatedAppHostPath,
            command: unrelatedAppHostPath,
        }), false);
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/workspace/physical/bin/Debug/net10.0/Api Replica',
            command: '/workspace/physical/bin/Debug/net10.0/Api Replica',
        }), false);
    });

    test('falls back to raw apphost candidates when canonical TargetPath directory lookup fails', async () => {
        const targetPath = '/workspace/link/bin/Debug/net10.0/Api.dll';
        const appHostPath = '/workspace/link/bin/Debug/net10.0/Api';
        const appHostExePath = `${appHostPath}.exe`;
        const targetDirectory = nodePath.dirname(targetPath);
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const realpath = sinon.stub().rejects(new Error('ENOENT'));
        const attachProvider = createAttachProvider(dotNetService, resolver, { realpath });

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/workspace/link/api/Api.csproj',
        }));

        const appHostIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: appHostPath,
            command: appHostPath,
        }), true);
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: appHostExePath,
            command: appHostExePath,
        }), true);
        assert.ok(realpath.calledWithExactly(appHostPath));
        assert.ok(realpath.calledWithExactly(appHostExePath));
        assert.ok(realpath.calledWithExactly(targetDirectory));
    });

    test('normalizes Windows executable and command identities before matching', async () => {
        const targetPath = 'C:\\Repo\\My Attach Service.dll';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: true });
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        const appHostIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(appHostIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: 'c:/repo/MY ATTACH SERVICE.EXE',
            command: 'not-used-for-apphost',
        }), true);

        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath: 'C:\\Repo\\Api.dll', useAppHost: false });
        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        const frameworkDependentIdentity = resolver.resolveProcessId.secondCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(frameworkDependentIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: 'c:/Program Files/dotnet/DOTNET.EXE',
            command: 'dotnet exec c:/REPO\\api.DLL',
        }), true);
    });

    test('attach configuration derives the default apphost identity from TargetPath', async () => {
        const { dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);
        const resolver = {
            resolveProcessId: sinon.stub().resolves(4321),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': 1234,
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        const processIdentity = resolver.resolveProcessId.firstCall.args[1] as TestLaunchedChildProcessIdentity;
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4321,
            parentPid: 1234,
            executable: '/repo/bin/Debug/net10.0/Api',
            command: '/repo/bin/Debug/net10.0/Api --urls http://localhost:5000',
        }), true);
        assert.strictEqual(processIdentity.isCandidate({
            pid: 4322,
            parentPid: 1234,
            executable: '/repo/bin/Debug/net10.0/Api.dll',
            command: '/repo/bin/Debug/net10.0/Api.dll',
        }), false);
    });

    test('attach configuration scopes replicas with the same TargetPath to their launcher PIDs', async () => {
        const { dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Replica.dll', null, true, true);
        const resolver = {
            resolveProcessId: sinon.stub()
                .onFirstCall().resolves(4321)
                .onSecondCall().resolves(4322),
        };
        const attachProvider = createAttachProvider(dotNetService, resolver);
        const resource = (pid: number) => createProjectResource({
            'executable.pid': pid,
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }, `api-${pid}`);

        const firstConfiguration = await attachProvider.createDebugConfiguration(resource(1234));
        const secondConfiguration = await attachProvider.createDebugConfiguration(resource(5678));

        assert.strictEqual(firstConfiguration.processId, 4321);
        assert.strictEqual(secondConfiguration.processId, 4322);
        assert.deepStrictEqual(resolver.resolveProcessId.firstCall.args.slice(0, 1), [1234]);
        assert.deepStrictEqual(resolver.resolveProcessId.secondCall.args.slice(0, 1), [5678]);
    });

    test('attach configuration fails closed for missing and ambiguous framework-dependent children', async () => {
        const targetPath = '/repo/bin/Debug/net10.0/Api.dll';
        const createProvider = (processes: readonly LaunchedChildProcess[]) => {
            const dotNetService = new TestDotNetService(targetPath, null, true);
            dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: false });
            const resolver = new LaunchedChildProcessResolver(
                new StaticLaunchedChildProcessQuery(processes),
                immediateProcessClock,
                { timeoutMs: 20, retryDelayMs: 10 });
            return createProjectResourceAttachProvider(() => dotNetService, resolver);
        };
        const resource = createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        });
        const noChild = createProvider([
            createLaunchedProcess(1234, 1, '/usr/local/share/dotnet/dotnet', 'dotnet run --project /repo/api/Api.csproj'),
            createLaunchedProcess(4321, 1234, '/usr/local/share/dotnet/dotnet', 'dotnet exec /repo/bin/Debug/net10.0/Other.dll'),
        ]);
        const ambiguousChildren = createProvider([
            createLaunchedProcess(1234, 1, '/usr/local/share/dotnet/dotnet', 'dotnet run --project /repo/api/Api.csproj'),
            createLaunchedProcess(4321, 1234, '/usr/local/share/dotnet/dotnet', `dotnet exec ${targetPath}`),
            createLaunchedProcess(4322, 1234, '/usr/local/share/dotnet/dotnet', `dotnet exec ${targetPath}`),
        ]);

        await assert.rejects(noChild.createDebugConfiguration(resource));
        await assert.rejects(ambiguousChildren.createDebugConfiguration(resource));
    });

    test('attach configuration resolves same-name framework-dependent replicas within each launcher tree', async () => {
        const targetPath = '/repo/bin/Debug/net10.0/Api.dll';
        const { dotNetService } = createDebuggerExtension(targetPath, null, true, true);
        dotNetService.getDotNetAttachTargetInfoStub.resolves({ targetPath, useAppHost: false });
        const resolver = new LaunchedChildProcessResolver(
            new StaticLaunchedChildProcessQuery([
                createLaunchedProcess(1234, 1, '/usr/local/share/dotnet/dotnet', 'dotnet run --project /repo/api/Api.csproj'),
                createLaunchedProcess(4321, 1234, '/usr/local/share/dotnet/dotnet', `dotnet exec ${targetPath}`),
                createLaunchedProcess(5678, 1, '/usr/local/share/dotnet/dotnet', 'dotnet run --project /repo/api/Api.csproj'),
                createLaunchedProcess(8765, 5678, '/usr/local/share/dotnet/dotnet', `dotnet exec ${targetPath}`),
            ]),
            immediateProcessClock,
            { timeoutMs: 20, retryDelayMs: 10 });
        const attachProvider = createAttachProvider(dotNetService, resolver);
        const resource = (pid: number) => createProjectResource({
            'executable.pid': pid,
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }, `api-${pid}`);

        assert.strictEqual((await attachProvider.createDebugConfiguration(resource(1234))).processId, 4321);
        assert.strictEqual((await attachProvider.createDebugConfiguration(resource(5678))).processId, 8765);
    });

    test('attach configuration uses the resolved project TargetPath child process ID', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/FromTargetPath.dll', null, true, true);

        const configuration = await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
        }));

        assert.strictEqual(configuration.type, 'coreclr');
        assert.strictEqual(configuration.request, 'attach');
        assert.strictEqual(configuration.name, 'Attach debugger: API');
        assert.strictEqual(configuration.processId, 4321);
        assert.strictEqual(configuration.processName, undefined);
        assert.ok(dotNetService.getDotNetAttachTargetInfoStub.calledOnceWithExactly('/repo/api/Api.csproj', undefined));
        assert.strictEqual(dotNetService.getDotNetTargetPathStub.called, false);
    });

    test('attach configuration uses safe properties when executable arguments are redacted', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Release/net10.0/ReleaseApi.dll', null, true, true);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'executable.args': null,
            'project.path': '/repo/api/Api.csproj',
            'project.configuration': 'Release',
            'project.targetFramework': 'net10.0',
            'project.launchCommand': 'run',
        }));

        assert.ok(dotNetService.getDotNetAttachTargetInfoStub.calledOnceWithExactly(
            '/repo/api/Api.csproj', 'Release', undefined, 'net10.0'));
    });

    test('attach configuration prefers safe properties and does not parse executable arguments', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Release/net10.0/ReleaseApi.dll', null, true, true);

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'executable.args': ['run', '--configuration', 'Debug', '--framework', 'net9.0'],
            'project.path': '/repo/api/Api.csproj',
            'project.configuration': 'Release',
            'project.targetFramework': 'net10.0',
            'project.launchCommand': 'run',
        }));

        await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '5678',
            'executable.path': 'dotnet',
            'executable.args': ['run', '--configuration', 'Debug', '--framework', 'net9.0'],
            'project.path': '/repo/api/Api.csproj',
            'project.launchCommand': 'run',
        }, 'args-only', 'Args only'));

        assert.deepStrictEqual(dotNetService.getDotNetAttachTargetInfoStub.firstCall.args, [
            '/repo/api/Api.csproj', 'Release', undefined, 'net10.0',
        ]);
        assert.deepStrictEqual(dotNetService.getDotNetAttachTargetInfoStub.secondCall.args, [
            '/repo/api/Api.csproj', undefined,
        ]);
        assert.strictEqual(dotNetService.getDotNetTargetPathStub.called, false);
    });

    test('attach configuration rejects present invalid launch command metadata', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);
        const cases = [
            { label: 'unsupported publish marker', launchCommand: 'publish' },
            { label: 'present null marker fails before older fallback', launchCommand: null },
        ] as const;

        for (const { label, launchCommand } of cases) {
            dotNetService.getDotNetAttachTargetInfoStub.resetHistory();
            dotNetService.getDotNetTargetPathStub.resetHistory();

            await assert.rejects(
                attachProvider.createDebugConfiguration(createProjectResource({
                    'executable.pid': '1234',
                    'executable.path': 'dotnet',
                    'executable.args': null,
                    'project.path': '/repo/api/Api.csproj',
                    'project.launchCommand': launchCommand,
                })),
                (error: unknown) => error instanceof Error
                    && error.message === 'Invalid launch configuration for api.',
                label);

            assert.strictEqual(dotNetService.getDotNetAttachTargetInfoStub.called, false, label);
            assert.strictEqual(dotNetService.getDotNetTargetPathStub.called, false, label);
        }
    });

    test('attach configuration passes cancellation to target discovery', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);
        const cancellation = new vscode.CancellationTokenSource();

        try {
            await attachProvider.createDebugConfiguration(createProjectResource({
                'executable.pid': '1234',
                'executable.path': 'dotnet',
                'project.path': '/repo/api/Api.csproj',
            }), cancellation.token);

            assert.ok(dotNetService.getDotNetAttachTargetInfoStub.calledOnceWithExactly(
                '/repo/api/Api.csproj',
                undefined,
                cancellation.token));
        }
        finally {
            cancellation.dispose();
        }
    });

    test('target discovery cancels and terminates its specific msbuild process', async () => {
        const dotNetService = new DotNetService(undefined);
        const msbuildProcess = createMsbuildProcess();
        sinon.stub(childProcess, 'spawn').returns(msbuildProcess.process);
        const terminate = sinon.stub(cliProcess, 'terminateCliProcess').resolves();
        const cancellation = new vscode.CancellationTokenSource();

        try {
            const targetDiscovery = dotNetService.getDotNetAttachTargetInfo('/repo/api/Api.csproj', undefined, cancellation.token);
            cancellation.cancel();
            const outcome = await Promise.race([
                targetDiscovery.then(
                    () => 'completed',
                    error => error instanceof vscode.CancellationError ? 'cancelled' : 'failed'),
                new Promise<'timedOut'>(resolve => setTimeout(() => resolve('timedOut'), 100)),
            ]);

            assert.strictEqual(outcome, 'cancelled');
            assert.ok(terminate.calledOnceWithExactly(
                msbuildProcess.process,
                'dotnet msbuild target discovery',
                { force: true, suppressTimeoutWarning: true }));
        }
        finally {
            cancellation.dispose();
        }
    });

    test('target discovery drains bounded stderr and includes probe output on failure', async () => {
        const dotNetService = new DotNetService(undefined);
        const msbuildProcess = createMsbuildProcess();
        sinon.stub(childProcess, 'spawn').callsFake(() => {
            queueMicrotask(() => {
                msbuildProcess.stdout.emit('data', 'stdout-marker');
                msbuildProcess.stderr.emit('data', 'x'.repeat(128 * 1024));
                msbuildProcess.stderr.emit('data', 'stderr-marker');
                msbuildProcess.process.emit('close', 1);
            });
            return msbuildProcess.process;
        });

        await assert.rejects(
            dotNetService.getDotNetAttachTargetInfo('/repo/api/Api.csproj'),
            (error: unknown) => error instanceof Error
                && error.message.includes('stdout-marker')
                && error.message.includes('stderr-marker')
                && error.message.length < 132 * 1024);
    });

    test('target discovery terminates its specific msbuild probe when it times out', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const dotNetService = new DotNetService(undefined);
        const msbuildProcess = createMsbuildProcess();
        sinon.stub(childProcess, 'spawn').returns(msbuildProcess.process);
        const terminate = sinon.stub(cliProcess, 'terminateCliProcess').resolves();

        try {
            const targetDiscovery = dotNetService.getDotNetAttachTargetInfo('/repo/api/Api.csproj');
            const completion = targetDiscovery.then(
                () => 'completed',
                error => error instanceof Error && /timed out/.test(error.message) ? 'timedOut' : 'failed');

            await clock.tickAsync(10_000);

            assert.strictEqual(await Promise.race([completion, Promise.resolve('pending')]), 'timedOut');
            assert.ok(terminate.calledOnceWithExactly(
                msbuildProcess.process,
                'dotnet msbuild target discovery',
                { force: true, suppressTimeoutWarning: true }));
        }
        finally {
            clock.restore();
        }
    });

    test('target discovery does not arm a timeout after it has been cancelled', async () => {
        const clock = sinon.useFakeTimers();
        const dotNetService = new DotNetService(undefined);
        const msbuildProcess = createMsbuildProcess();
        sinon.stub(childProcess, 'spawn').returns(msbuildProcess.process);
        const terminate = sinon.stub(cliProcess, 'terminateCliProcess').resolves();
        const cancellationToken: vscode.CancellationToken = {
            isCancellationRequested: true,
            onCancellationRequested: listener => {
                listener(undefined);
                return new vscode.Disposable(() => { });
            },
        };

        try {
            const targetDiscovery = dotNetService.getDotNetAttachTargetInfo('/repo/api/Api.csproj', undefined, cancellationToken);

            await assert.rejects(targetDiscovery, error => error instanceof vscode.CancellationError);
            await clock.tickAsync(10_000);

            assert.ok(terminate.calledOnceWithExactly(
                msbuildProcess.process,
                'dotnet msbuild target discovery',
                { force: true, suppressTimeoutWarning: true }));
        }
        finally {
            clock.restore();
        }
    });

    test('target discovery returns TargetName for framework-dependent projects', async () => {
        const dotNetService = new DotNetService(undefined);
        const msbuildProcess = createMsbuildProcess();
        const spawn = sinon.stub(childProcess, 'spawn').callsFake(() => {
            queueMicrotask(() => {
                msbuildProcess.stdout.emit('data', JSON.stringify({
                    Properties: {
                        TargetPath: '/repo/bin/Release/net10.0/ReleaseApi.dll',
                        TargetName: ' ReleaseApi ',
                        UseAppHost: 'false',
                    },
                }));
                msbuildProcess.process.emit('close', 0);
            });
            return msbuildProcess.process;
        });

        const targetInfo = await dotNetService.getDotNetAttachTargetInfo('/repo/api/Api.csproj', 'Release');

        assert.deepStrictEqual(targetInfo, {
            targetPath: '/repo/bin/Release/net10.0/ReleaseApi.dll',
            targetName: 'ReleaseApi',
            useAppHost: false,
        });
        assert.deepStrictEqual(spawn.firstCall.args[1], [
            'msbuild',
            '/repo/api/Api.csproj',
            '-nologo',
            '-getProperty:TargetPath',
            '-getProperty:TargetName',
            '-getProperty:UseAppHost',
            '-v:q',
            '-property:GenerateFullPaths=true',
            '-property:Configuration=Release',
        ]);
    });

    test('attach configuration rejects file-based project resources', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/Api.dll', null, true, true);

        await assert.rejects(
            attachProvider.createDebugConfiguration(createProjectResource({
                'executable.pid': '1234',
                'executable.path': 'dotnet',
                'project.path': '/repo/api/Api.cs',
                'secret.snapshot.property': 'top-secret',
            })),
            (error: unknown) => {
                assert.ok(error instanceof Error);
                assert.strictEqual(error.message, 'Invalid launch configuration for api.');
                assert.strictEqual(error.name, 'ResourceAttachConfigurationError');
                assert.strictEqual(
                    (error as Error & { errorKind?: string }).errorKind,
                    'resourceNotAttachable');
                return true;
            });

        assert.strictEqual(dotNetService.getDotNetTargetPathStub.called, false);
    });

    test('attach configuration keeps parented project resources attachable', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/FromTargetPath.dll', null, true, true);

        const configuration = await attachProvider.createDebugConfiguration(createProjectResource({
            'executable.pid': '1234',
            'executable.path': 'dotnet',
            'project.path': '/repo/api/Api.csproj',
            'resource.launchConfigurationType': 'project',
            'resource.parentName': 'group',
        }, 'api-grouped'));

        assert.strictEqual(configuration.processId, 4321);
        assert.strictEqual(configuration.processName, undefined);
        assert.ok(dotNetService.getDotNetAttachTargetInfoStub.calledOnceWithExactly('/repo/api/Api.csproj', undefined));
        assert.strictEqual(dotNetService.getDotNetTargetPathStub.called, false);
    });

    test('attach configuration rejects parented MAUI platform resources', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/FromTargetPath.dll', null, true, true);

        await assert.rejects(
            attachProvider.createDebugConfiguration(createProjectResource({
                'executable.pid': '1234',
                'executable.path': 'dotnet',
                'project.path': '/repo/maui/MauiApp.csproj',
                'resource.launchConfigurationType': 'maui',
                'resource.parentName': 'mauiapp',
            }, 'mauiapp-android-emulator', 'MAUI')),
            (error: unknown) => error instanceof Error
                && error.name === 'ResourceAttachConfigurationError'
                && (error as Error & { errorKind?: string }).errorKind === 'resourceNotAttachable');

        assert.strictEqual(dotNetService.getDotNetTargetPathStub.called, false);
    });

    test('attach configuration rejects parented resources without explicit launch metadata', async () => {
        const { attachProvider, dotNetService } = createDebuggerExtension('/repo/bin/Debug/net10.0/FromTargetPath.dll', null, true, true);

        await assert.rejects(
            attachProvider.createDebugConfiguration(createProjectResource({
                'executable.pid': '1234',
                'executable.path': 'dotnet',
                'project.path': '/repo/api/Api.csproj',
                'resource.parentName': 'group',
            }, 'legacy-parented', 'Legacy parented project')),
            (error: unknown) => error instanceof Error
                && error.name === 'ResourceAttachConfigurationError'
                && (error as Error & { errorKind?: string }).errorKind === 'resourceNotAttachable');

        assert.strictEqual(dotNetService.getDotNetTargetPathStub.called, false);
    });
    function restoreEnvironmentVariable(name: string, value: string | undefined): void {
        if (value === undefined) {
            delete process.env[name];
            return;
        }

        process.env[name] = value;
    }

    for (const [projectFile, language] of [
        ['/workspace/AppHost.fsproj', 'F#'],
        ['/workspace/AppHost.vbproj', 'Visual Basic'],
    ]) {
        test(`${language} AppHost tracker routes output through the AppHost coordinator`, async () => {
            const parentDebugSession = {
                id: 'aspire-session',
                type: 'aspire',
                name: 'Aspire',
                workspaceFolder: undefined,
                configuration: {
                    type: 'aspire',
                    request: 'launch',
                    name: 'Aspire',
                    program: projectFile,
                },
                customRequest: sinon.stub(),
                getDebugProtocolBreakpoint: sinon.stub(),
            } as unknown as vscode.DebugSession;
            const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
            const outputCoordinator = (aspireDebugSession as unknown as {
                _appHostLogOutput: {
                    handleDebugAdapterOutput(output: string, category: string | undefined): unknown;
                };
            })._appHostLogOutput;
            const handleOutputSpy = sinon.spy(outputCoordinator, 'handleDebugAdapterOutput');
            const trackerStub = sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
            sinon.stub(projectDebuggerExtension, 'createDebugSessionConfigurationCallback').resolves();
            sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves({
                id: 'apphost-session',
            } as unknown as Awaited<ReturnType<AspireDebugSession['startAndGetDebugSession']>>);

            await aspireDebugSession.startAppHost(projectFile, ['run', '--project', projectFile], [], true, { forceBuild: false });

            const onOutput = trackerStub.firstCall.args[1]?.onOutput;
            assert.ok(onOutput);
            onOutput('AppHost output\n', 'stdout');

            assert.strictEqual(handleOutputSpy.calledOnceWithExactly('AppHost output\n', 'stdout'), true);
        });
    }

    test('failed AppHost start writes error to debug console', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.ts'
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub()
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const outputEvents: any[] = [];
        const outputSubscription = aspireDebugSession.onDidSendMessage(message => outputEvents.push(message));
        const startError = new Error('AppHost build failed');

        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').rejects(startError);
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        await aspireDebugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        assert.ok(showErrorMessageStub.calledWith(startError.message));
        assert.ok(startError.stack);
        assert.ok(outputEvents.some(message =>
            message.type === 'event'
            && message.event === 'output'
            && message.body.category === 'stderr'
            && message.body.output.includes(startError.stack)));

        outputSubscription.dispose();
    });

    test('failed AppHost start does not duplicate already streamed build output', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.ts'
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub()
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const outputEvents: any[] = [];
        const outputSubscription = aspireDebugSession.onDidSendMessage(message => outputEvents.push(message));
        const startError = new Error('Build FAILED.');
        (startError as Error & { debugConsoleOutputAlreadyWritten?: boolean }).debugConsoleOutputAlreadyWritten = true;

        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').rejects(startError);
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        await aspireDebugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        assert.ok(showErrorMessageStub.calledWith(startError.message));
        assert.strictEqual(outputEvents.some(message =>
            message.type === 'event'
            && message.event === 'output'
            && message.body.output.includes(startError.message)), false);

        outputSubscription.dispose();
    });

    test('AppHost toolbar restart identifies the terminating Aspire session', async () => {
        const configuration: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.ts',
        };
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration,
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const trackerStub = sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves({
            id: 'apphost-session',
        } as unknown as Awaited<ReturnType<AspireDebugSession['startAndGetDebugSession']>>);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        await aspireDebugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        const restartHandler = trackerStub.firstCall.args[1]?.onRestartRequested;
        assert.ok(restartHandler);
        assert.strictEqual(restartHandler(aspireDebugSession.debugSessionId), true);
        assert.strictEqual(configuration.__aspireAppHostRestartSourceSessionId, parentDebugSession.id);

        aspireDebugSession.dispose();

        assert.strictEqual(configuration.__aspireAppHostRestartSourceSessionId, undefined);
    });

    test('AppHost toolbar restart preserves the source marker until the parent terminates', async () => {
        const configuration: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.ts',
        };
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration,
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: 'apphost-session',
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const trackerStub = sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(
            appHostDebugSession as unknown as Awaited<ReturnType<AspireDebugSession['startAndGetDebugSession']>>);
        let terminateCallback: ((session: vscode.DebugSession) => Promise<void>) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateCallback = callback as (session: vscode.DebugSession) => Promise<void>;
            return new vscode.Disposable(() => { });
        });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);

        await aspireDebugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        const restartHandler = trackerStub.firstCall.args[1]?.onRestartRequested;
        assert.ok(restartHandler);
        assert.strictEqual(restartHandler(aspireDebugSession.debugSessionId), true);
        assert.ok(terminateCallback);
        await terminateCallback(appHostDebugSession);

        assert.strictEqual(startDebuggingStub.calledOnce, true);
        const restartConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(restartConfiguration.__aspireAppHostRestartSourceSessionId, parentDebugSession.id);
    });

    test('failed AppHost toolbar restart clears the source marker', async () => {
        const configuration: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.ts',
        };
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration,
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        } as unknown as vscode.DebugSession;
        const appHostDebugSession = {
            id: 'apphost-session',
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const trackerStub = sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').resolves(
            appHostDebugSession as unknown as Awaited<ReturnType<AspireDebugSession['startAndGetDebugSession']>>);
        let terminateCallback: ((session: vscode.DebugSession) => Promise<void>) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            terminateCallback = callback as (session: vscode.DebugSession) => Promise<void>;
            return new vscode.Disposable(() => { });
        });
        sinon.stub(aspireDebugSession, 'stopDebugging').rejects(new Error('shutdown failed'));
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);

        await aspireDebugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        const restartHandler = trackerStub.firstCall.args[1]?.onRestartRequested;
        assert.ok(restartHandler);
        assert.strictEqual(restartHandler(aspireDebugSession.debugSessionId), true);
        assert.ok(terminateCallback);
        await terminateCallback(appHostDebugSession);

        assert.strictEqual(startDebuggingStub.called, false);
        assert.strictEqual(configuration.__aspireAppHostRestartSourceSessionId, undefined);
    });

    test('filters AppHost debugger noise from Aspire parent debug console', () => {
        const filter = new AppHostParentOutputFilter();

        assert.strictEqual(filter.filter("'TestShop.AppHost' (CoreCLR: clrhost): Loaded '/dotnet/System.Private.CoreLib.dll'. Skipped loading symbols.\n", 'console'), undefined);
        assert.strictEqual(filter.filter("TestShop.AppHost.dll (29067): Loaded '/usr/local/share/dotnet/shared/Microsoft.NETCore.App/8.0.14/System.Private.CoreLib.dll'. No se puede encontrar o abrir el archivo PDB.\n", 'console'), undefined);
        assert.strictEqual(filter.filter("Loaded '/dotnet/System.Net.Http.dll'. Skipped loading symbols.\n", 'console'), undefined);
        assert.strictEqual(filter.filter("Exception thrown: 'System.InvalidOperationException' in TestShop.AppHost.dll\n", 'console'), undefined);
        assert.strictEqual(filter.filter('debug adapter details\n', 'debug'), undefined);
        assert.strictEqual(filter.filter('-------------------------------------------------------------------------------\n', 'console'), undefined);
        assert.strictEqual(filter.filter('You may only use the Microsoft Visual Studio .NET/C/C++ Debugger with Visual Studio Code.\n', 'console'), undefined);
        assert.strictEqual(filter.filter('Usando la configuración de inicio de "/workspace/Properties/launchSettings.json" [perfil "https"]...\n', 'console'), undefined);
        assert.strictEqual(filter.filter("dbug: Aspire.Hosting.Health.ResourceHealthCheckService[0]\n      Resource 'apigateway' is ready.\n", 'stdout'), undefined);
        assert.strictEqual(filter.filter("Aspire.Hosting.Health.ResourceHealthCheckService: Debug: Resource 'apigateway' is ready.\n", 'stdout'), undefined);
    });

    test('keeps AppHost fatal output in Aspire parent debug console', () => {
        const filter = new AppHostParentOutputFilter();
        const criticalLog = "crit: TestShop.AppHost[0]\n      Host terminated unexpectedly.\n";
        const unhandledException = 'Unhandled exception. System.InvalidOperationException: boom\n';
        const unhandledBaseException = 'Unhandled exception. System.Exception: This code snippet is for illustrative purposes only.\n   at Program.<Main>$(String[] args) in /workspace/AppHost.cs:line 8\n';
        const javascriptException = 'Uncaught TypeError: Cannot read properties of undefined\n    at file:///workspace/apphost.js:8:3\n';
        const nodeModuleException = "Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@microsoft/aspire'\n    at packageResolve (node:internal/modules/esm/resolve:857:9)\n";
        const consoleError = 'Fatal error: unable to bind port\n';

        assert.deepStrictEqual(filter.filter(criticalLog, 'stdout'), { output: criticalLog, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(unhandledException, 'console'), { output: unhandledException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(unhandledBaseException, 'console'), { output: unhandledBaseException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(javascriptException, 'console'), { output: javascriptException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(nodeModuleException, 'console'), { output: nodeModuleException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(consoleError, 'console'), { output: consoleError, category: 'stderr' });
    });

    test('keeps AppHost warning and information output that is not debugger console chatter', () => {
        const filter = new AppHostParentOutputFilter();
        const warningLog = "warn: TestShop.AppHost[0]\n      Port is already allocated.\n";
        const normalOutput = 'Now listening on: https://localhost:5001\n';

        assert.deepStrictEqual(filter.filter(warningLog, 'stdout'), { output: warningLog, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(normalOutput, 'stdout'), { output: normalOutput, category: 'stdout' });
    });

    test('does not promote benign AppHost stdout containing words like fail/error to stderr', () => {
        const filter = new AppHostParentOutputFilter();
        const benignFailMention = 'Failed payment retry queued for processing\n';
        const benignErrorMention = 'Loaded handler from /src/error_handler/main.cs\n';
        const benignFailureMention = 'Build complete with no failures detected\n';

        assert.deepStrictEqual(filter.filter(benignFailMention, 'stdout'), { output: benignFailMention, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(benignErrorMention, 'stdout'), { output: benignErrorMention, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(benignFailureMention, 'stdout'), { output: benignFailureMention, category: 'stdout' });
    });

    test('does not classify arbitrary user stdout shaped like prefix:Level: as a structured log', () => {
        const filter = new AppHostParentOutputFilter();
        const userPrint = 'Status: Error: connection refused\n';
        const userDebugPrint = 'Note: Debug: caller line 42\n';

        assert.deepStrictEqual(filter.filter(userPrint, 'stdout'), { output: userPrint, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(userDebugPrint, 'stdout'), { output: userDebugPrint, category: 'stdout' });
    });

    test('continuation state is reset when DAP category changes between events', () => {
        const filter = new AppHostParentOutputFilter();
        // First event: a dropped trace log on stdout. Continuation state would say "drop indented lines".
        assert.strictEqual(filter.filter('trce: Some.Category[0]\n', 'stdout'), undefined);
        // A subsequent event on a different category (console) that happens to start with
        // an indented line must NOT be silently dropped as a continuation of the trace log.
        const indentedConsoleLine = '    Loaded module foo\n';
        assert.strictEqual(filter.filter(indentedConsoleLine, 'console'), undefined); // dropped because console+non-severe, not because of continuation state
        // And an indented stdout line afterwards is emitted normally instead of being dropped.
        const indentedStdoutLine = '    plain user output line\n';
        assert.deepStrictEqual(filter.filter(indentedStdoutLine, 'stdout'), { output: indentedStdoutLine, category: 'stdout' });
    });

    test('treats missing DAP category as console so debugger noise does not leak as stdout', () => {
        const filter = new AppHostParentOutputFilter();
        // Per the DAP spec a missing category should be treated as 'console'. The
        // .NET debug adapter sometimes emits output events without a category, and
        // this debugger chatter must be suppressed the same way as explicit
        // 'console'-category lines instead of being mirrored as stdout.
        const debuggerChatter = "'TestShop.AppHost' (CoreCLR: clrhost): Loaded '/dotnet/System.Private.CoreLib.dll'. Skipped loading symbols.\n";
        assert.strictEqual(filter.filter(debuggerChatter, undefined), undefined);

        // Severe runtime output without a category is still kept and promoted to stderr,
        // matching the existing 'console'-category behavior.
        const unhandledException = 'Unhandled exception. System.InvalidOperationException: boom\n';
        assert.deepStrictEqual(filter.filter(unhandledException, undefined), { output: unhandledException, category: 'stderr' });
    });

    test('project is built when C# dev kit is installed and executable not found', async () => {
        const outputPath = 'C:\\temp\\bin\\Debug\\net7.0\\TestProject.dll';
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, false);

        const projectPath = 'C:\\temp\\TestProject.csproj';
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, outputPath);
        assert.strictEqual(dotNetService.buildDotNetProjectStub.called, true);
    });

    test('project-scoped dotnet commands use the project directory as their working directory', async () => {
        const projectPath = nodePath.join(process.cwd(), '.test-temp', `dotnet-cwd-${process.pid}-${Date.now()}`, 'TestProject.csproj');
        const outputPath = nodePath.join(nodePath.dirname(projectPath), 'bin', 'Debug', 'net10.0', 'TestProject.dll');
        const projectDirectory = nodePath.dirname(projectPath);
        const buildProcess = Object.assign(new EventEmitter(), {
            stdout: new EventEmitter(),
            stderr: new EventEmitter()
        });

        const execFileStub = sinon.stub(childProcess, 'execFile').yields(null, { stdout: outputPath, stderr: '' });
        const spawnStub = sinon.stub(childProcess, 'spawn').callsFake(() => {
            setImmediate(() => buildProcess.emit('close', 0));
            return buildProcess as unknown as childProcess.ChildProcessWithoutNullStreams;
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await projectDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(msbuildCallFor(execFileStub, projectPath).args[2]?.cwd, projectDirectory);
        assert.strictEqual(buildCallFor(spawnStub, projectPath).args[2]?.cwd, projectDirectory);
    });

    test('project-scoped dotnet commands resolve the CLI using the target derived from the project path and forward only that resolved CLI', async () => {
        const projectPath = nodePath.join(process.cwd(), '.test-temp', `dotnet-cli-target-${process.pid}-${Date.now()}`, 'TestProject.csproj');
        const outputPath = nodePath.join(nodePath.dirname(projectPath), 'bin', 'Debug', 'net10.0', 'TestProject.dll');
        const projectDirectory = nodePath.dirname(projectPath);
        const buildProcess = Object.assign(new EventEmitter(), {
            stdout: new EventEmitter(),
            stderr: new EventEmitter()
        });

        const execFileStub = sinon.stub(childProcess, 'execFile').yields(null, { stdout: outputPath, stderr: '' });
        const spawnStub = sinon.stub(childProcess, 'spawn').callsFake(() => {
            setImmediate(() => buildProcess.emit('close', 0));
            return buildProcess as unknown as childProcess.ChildProcessWithoutNullStreams;
        });
        const folder = { name: 'workspace', index: 0, uri: vscode.Uri.file(projectDirectory) } as vscode.WorkspaceFolder;
        // Compare against the folder's own fsPath rather than projectDirectory: VS Code lowercases
        // the drive letter, so 'd:\...'.startsWith('D:\...') is false on Windows and the stub would
        // report no owning folder instead of the one this test is about.
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) =>
            uri.fsPath.startsWith(folder.uri.fsPath) ? folder : undefined);
        const resolveCliPathStub = sinon.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: '/resolved/aspire', available: true, source: 'configured' });
        const resolvedEnv = { MARKER: 'resolved-env' } as unknown as NodeJS.ProcessEnv;
        const createResolvedEnvStub = sinon.stub(cliPathEnvironmentModule, 'createResolvedAspireCliPathProcessEnvironment').returns(resolvedEnv);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await projectDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(resolveCliPathStub.calledWith(workspaceFolderCliPathTarget(folder)));
        assert.ok(createResolvedEnvStub.calledWith('/resolved/aspire'));
        assert.strictEqual(msbuildCallFor(execFileStub, projectPath).args[2]?.env, resolvedEnv);
        assert.strictEqual(buildCallFor(spawnStub, projectPath).args[2]?.env, resolvedEnv);
    });

    test('dotnet run-api preserves CLI resolution errors without spawning', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const resolutionError = new Error('CLI resolution failed');
        sinon.stub(cliPathModule, 'resolveCliPath').rejects(resolutionError);
        const spawnStub = sinon.stub(childProcess, 'spawn');
        const service = new DotNetService({} as AspireDebugSession);

        await assert.rejects(
            service.getDotNetRunApiOutput('/workspace/apphost.cs'),
            error => error === resolutionError);

        assert.ok(spawnStub.notCalled);
        assert.strictEqual(clock.countTimers(), 0);
    });

    test('dotnet run-api does not time out or spawn while CLI resolution is pending', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        let rejectResolution!: (reason?: unknown) => void;
        const pendingResolution = new Promise<Awaited<ReturnType<typeof cliPathModule.resolveCliPath>>>((_, reject) => {
            rejectResolution = reject;
        });
        sinon.stub(cliPathModule, 'resolveCliPath').returns(pendingResolution);
        const spawnStub = sinon.stub(childProcess, 'spawn');
        const service = new DotNetService({} as AspireDebugSession);
        let outcome: { value?: string; error?: unknown } | undefined;
        const observed = service.getDotNetRunApiOutput('/workspace/apphost.cs').then(
            value => { outcome = { value }; },
            error => { outcome = { error }; });

        await clock.tickAsync(10_001);
        const outcomeAfterTimeout = outcome;
        const spawnCalledAfterTimeout = spawnStub.called;
        const resolutionError = new Error('CLI resolution failed');
        rejectResolution(resolutionError);
        await observed;

        assert.strictEqual(outcomeAfterTimeout, undefined);
        assert.strictEqual(spawnCalledAfterTimeout, false);
        assert.strictEqual(outcome?.error, resolutionError);
        assert.ok(spawnStub.notCalled);
    });

    test('project is not built when C# dev kit is installed and executable found', async () => {
        const outputPath = 'C:\\temp\\bin\\Debug\\net7.0\\TestProject.dll';
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const projectPath = 'C:\\temp\\TestProject.csproj';
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, outputPath);
        assert.strictEqual(dotNetService.buildDotNetProjectStub.notCalled, true);
    });

    test('project debug configuration is byte-identical whether or not C# Dev Kit is installed', async () => {
        getHotReloadDiagnostics.onFirstCall().returns({
            devKitInstalled: false,
            workspaceTrusted: true,
            settingContributed: true,
            settingEnabled: false,
            reloadOnSaveEnabled: true
        });
        getHotReloadDiagnostics.onSecondCall().returns({
            devKitInstalled: true,
            workspaceTrusted: true,
            settingContributed: true,
            settingEnabled: true,
            reloadOnSaveEnabled: true
        });

        const withoutDevKit = await createProjectDebugConfiguration();
        const withDevKit = await createProjectDebugConfiguration();

        assert.deepStrictEqual(withDevKit, withoutDevKit);
    });

    test('ordinary project debug launch logs and offers the Hot Reload advisory with current diagnostics', async () => {
        const diagnostics = {
            devKitInstalled: true,
            workspaceTrusted: true,
            settingContributed: true,
            settingEnabled: false,
            reloadOnSaveEnabled: true
        };
        getHotReloadDiagnostics.returns(diagnostics);

        await createProjectDebugConfiguration({ runId: 'resource-42' });

        assert.strictEqual(getHotReloadDiagnostics.calledOnce, true);
        assert.strictEqual(logHotReloadDiagnostics.calledOnceWithExactly('C:\\temp\\TestProject.csproj (run resource-42)', diagnostics), true);
        assert.strictEqual(showHotReloadDisabledAdvisory.calledOnceWithExactly(diagnostics), true);
    });

    async function createProjectDebugConfiguration(options: { debug?: boolean; runId?: string; debugSessionId?: string; debugSession?: AspireDebugSession; isApphost?: boolean } = {}): Promise<AspireResourceExtendedDebugConfiguration> {
        const outputPath = 'C:\\temp\\bin\\Debug\\net7.0\\TestProject.dll';
        const { extension, doesFileExistStub } = createDebuggerExtension(outputPath, null, true, true);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: 'C:\\temp\\TestProject.csproj'
        };

        const debug = options.debug ?? true;
        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: options.runId ?? '1',
            debugSessionId: options.debugSessionId ?? '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch',
            noDebug: !debug
        };

        await extension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug, runId: debugConfig.runId, debugSessionId: options.debugSessionId ?? '1', isApphost: options.isApphost ?? false, debugSession: options.debugSession ?? sinon.createStubInstance(AspireDebugSession) },
            debugConfig);

        // Restored so a caller can build a second configuration in the same test; sinon refuses to
        // wrap an already-wrapped method.
        doesFileExistStub.restore();

        return debugConfig;
    }

    test('does not inspect or show Hot Reload for a noDebug launch', async () => {
        await createProjectDebugConfiguration({ debug: false });

        assert.strictEqual(getHotReloadDiagnostics.called, false);
        assert.strictEqual(showHotReloadDisabledAdvisory.called, false);
    });

    test('does not inspect or show Hot Reload for the AppHost', async () => {
        await createProjectDebugConfiguration({ isApphost: true });

        assert.strictEqual(getHotReloadDiagnostics.called, false);
        assert.strictEqual(showHotReloadDisabledAdvisory.called, false);
    });

    test('AppHost selected profile overrides inherited and old CLI environment', async () => {
        const fs = require('fs');
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const tempRoot = nodePath.join(process.cwd(), '.test-temp', `dotnet-apphost-profile-${process.pid}-${Date.now()}`);
        const projectDir = nodePath.join(tempRoot, 'AppHost');
        const propertiesDir = nodePath.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const inheritedEnvironment = {
            mode: process.env.mode,
            DOTNET_LAUNCH_PROFILE: process.env.DOTNET_LAUNCH_PROFILE,
            ASPNETCORE_URLS: process.env.ASPNETCORE_URLS,
            EXPLICIT: process.env.EXPLICIT,
            AMBIENT_ONLY: process.env.AMBIENT_ONLY,
            CLI_PRECEDENCE: process.env.CLI_PRECEDENCE,
            DEFAULT_PROFILE_ONLY: process.env.DEFAULT_PROFILE_ONLY,
            DEFAULT_PROFILE_EXPLICIT: process.env.DEFAULT_PROFILE_EXPLICIT,
            DEFAULT_PROFILE_EXPANDED: process.env.DEFAULT_PROFILE_EXPANDED,
            DEFAULT_PROFILE_SOURCE: process.env.DEFAULT_PROFILE_SOURCE,
            DEFAULT_PROFILE_DEPENDENT: process.env.DEFAULT_PROFILE_DEPENDENT,
            DEFAULT_PROFILE_RAW: process.env.DEFAULT_PROFILE_RAW,
            DEFAULT_PROFILE_CYCLE_A: process.env.DEFAULT_PROFILE_CYCLE_A,
            DEFAULT_PROFILE_CYCLE_B: process.env.DEFAULT_PROFILE_CYCLE_B,
            DEFAULT_PROFILE_SELF: process.env.DEFAULT_PROFILE_SELF,
            PROFILE_ROOT: process.env.PROFILE_ROOT,
            PROFILE_MISSING: process.env.PROFILE_MISSING
        };

        process.env.mode = 'ambient-h1';
        process.env.DOTNET_LAUNCH_PROFILE = 'h1';
        process.env.ASPNETCORE_URLS = 'http://localhost:14000';
        process.env.EXPLICIT = 'from-process';
        process.env.AMBIENT_ONLY = 'from-process';
        process.env.CLI_PRECEDENCE = 'from-process';
        process.env.DEFAULT_PROFILE_ONLY = 'from-process';
        process.env.DEFAULT_PROFILE_EXPLICIT = 'from-process';
        process.env.DEFAULT_PROFILE_EXPANDED = 'from-process';
        process.env.DEFAULT_PROFILE_SOURCE = 'from-process-source';
        process.env.DEFAULT_PROFILE_DEPENDENT = 'from-process-dependent';
        process.env.DEFAULT_PROFILE_RAW = 'from-process-raw';
        process.env.DEFAULT_PROFILE_CYCLE_A = 'ambient-a';
        process.env.DEFAULT_PROFILE_CYCLE_B = 'ambient-b';
        process.env.DEFAULT_PROFILE_SELF = 'ambient-self';
        delete process.env.PROFILE_ROOT;
        delete process.env.PROFILE_MISSING;

        try {
            const projectPath = nodePath.join(projectDir, 'AppHost.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(nodePath.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    h1: {
                        commandName: 'Project',
                        applicationUrl: 'http://localhost:15001',
                        environmentVariables: {
                            mode: '1',
                            EXPLICIT: 'from-h1',
                            DEFAULT_PROFILE_ONLY: 'from-h1',
                            DEFAULT_PROFILE_EXPLICIT: 'from-h1',
                            DEFAULT_PROFILE_EXPANDED: '%PROFILE_ROOT%/default',
                            DEFAULT_PROFILE_SOURCE: 'from-h1',
                            DEFAULT_PROFILE_DEPENDENT: '%DEFAULT_PROFILE_SOURCE%/dependent',
                            DEFAULT_PROFILE_RAW: '%DEFAULT_PROFILE_SOURCE%/raw',
                            DEFAULT_PROFILE_CYCLE_A: '%DEFAULT_PROFILE_CYCLE_B%',
                            DEFAULT_PROFILE_CYCLE_B: '%DEFAULT_PROFILE_CYCLE_A%',
                            DEFAULT_PROFILE_SELF: '%DEFAULT_PROFILE_SELF%',
                            INVALID_DEFAULT_ENV: 42
                        }
                    },
                    h2: {
                        commandName: 'Project',
                        applicationUrl: 'http://localhost:15002',
                        executablePath: false,
                        workingDirectory: true,
                        useSSL: 'ignored by the SDK',
                        environmentVariables: {
                            mode: '2',
                            EXPLICIT: 'from-h2',
                            ASPNETCORE_URLS: 'http://localhost:16002'
                        }
                    },
                    h3: {
                        commandName: 'Project',
                        commandLineArgs: '--profile-root %PROFILE_ROOT% --msbuild $(PROFILE_ROOT) --missing %PROFILE_MISSING% --overlap %PROFILE_MISSING%PROFILE_ROOT%',
                        environmentVariables: {
                            UNSELECTED_ONLY: 'from-h3',
                            PROFILE_PATH: '%PROFILE_ROOT%/config',
                            PROFILE_MSBUILD: '$(PROFILE_ROOT)/config',
                            PROFILE_UNRESOLVED: '%PROFILE_MISSING%/config',
                            PROFILE_OVERLAP: '%PROFILE_MISSING%PROFILE_ROOT%'
                        }
                    }
                }
            }));

            const outputPath = nodePath.join(projectDir, 'bin', 'Debug', 'net10.0', 'AppHost.dll');
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'h1'
            };
            const debugSessionConfig: AspireExtendedDebugConfiguration = {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: projectPath,
                debuggers: {
                    project: {
                        launchProfile: 'h3',
                        disableLaunchProfile: true
                    },
                    apphost: {
                        launchProfile: 'h2',
                        disableLaunchProfile: false
                    }
                }
            };
            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);
            fakeAspireDebugSession.configuration = debugSessionConfig;

            const debugConfig = await createDebugSessionConfiguration(
                debugSessionConfig,
                launchConfig,
                undefined,
                [
                    { name: 'MODE', value: 'cli-h1' },
                    { name: 'DOTNET_LAUNCH_PROFILE', value: 'h1' },
                    { name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' },
                    { name: 'EXPLICIT', value: 'from-cli' },
                    { name: 'CLI_PRECEDENCE', value: 'from-cli' },
                    { name: 'UNSELECTED_ONLY', value: 'from-cli' },
                    { name: 'DEFAULT_PROFILE_ONLY', value: 'from-h1' },
                    { name: 'DEFAULT_PROFILE_EXPLICIT', value: 'from-cli-explicit' },
                    { name: 'DEFAULT_PROFILE_EXPANDED', value: '/profile/root/default' },
                    { name: 'DEFAULT_PROFILE_SOURCE', value: 'from-h1' },
                    { name: 'DEFAULT_PROFILE_DEPENDENT', value: 'from-process-source/dependent' },
                    { name: 'DEFAULT_PROFILE_RAW', value: '%DEFAULT_PROFILE_SOURCE%/raw' },
                    { name: 'DEFAULT_PROFILE_CYCLE_A', value: 'ambient-b' },
                    { name: 'DEFAULT_PROFILE_CYCLE_B', value: 'ambient-a' },
                    { name: 'DEFAULT_PROFILE_SELF', value: 'explicit-self' },
                    { name: 'PROFILE_ROOT', value: '/profile/root' }
                ],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.deepStrictEqual({
                mode: debugConfig.env.mode,
                DOTNET_LAUNCH_PROFILE: debugConfig.env.DOTNET_LAUNCH_PROFILE,
                ASPNETCORE_URLS: debugConfig.env.ASPNETCORE_URLS,
                EXPLICIT: debugConfig.env.EXPLICIT,
                AMBIENT_ONLY: debugConfig.env.AMBIENT_ONLY,
                CLI_PRECEDENCE: debugConfig.env.CLI_PRECEDENCE,
                UNSELECTED_ONLY: debugConfig.env.UNSELECTED_ONLY,
                DEFAULT_PROFILE_ONLY: debugConfig.env.DEFAULT_PROFILE_ONLY,
                DEFAULT_PROFILE_EXPLICIT: debugConfig.env.DEFAULT_PROFILE_EXPLICIT,
                DEFAULT_PROFILE_EXPANDED: debugConfig.env.DEFAULT_PROFILE_EXPANDED,
                DEFAULT_PROFILE_SOURCE: debugConfig.env.DEFAULT_PROFILE_SOURCE,
                DEFAULT_PROFILE_DEPENDENT: debugConfig.env.DEFAULT_PROFILE_DEPENDENT,
                DEFAULT_PROFILE_RAW: debugConfig.env.DEFAULT_PROFILE_RAW,
                DEFAULT_PROFILE_CYCLE_A: debugConfig.env.DEFAULT_PROFILE_CYCLE_A,
                DEFAULT_PROFILE_CYCLE_B: debugConfig.env.DEFAULT_PROFILE_CYCLE_B,
                DEFAULT_PROFILE_SELF: debugConfig.env.DEFAULT_PROFILE_SELF
            }, {
                mode: '2',
                DOTNET_LAUNCH_PROFILE: 'h2',
                ASPNETCORE_URLS: 'http://localhost:16002',
                EXPLICIT: 'from-h2',
                AMBIENT_ONLY: 'from-process',
                CLI_PRECEDENCE: 'from-cli',
                UNSELECTED_ONLY: 'from-cli',
                DEFAULT_PROFILE_ONLY: 'from-process',
                DEFAULT_PROFILE_EXPLICIT: 'from-cli-explicit',
                DEFAULT_PROFILE_EXPANDED: 'from-process',
                DEFAULT_PROFILE_SOURCE: 'from-process-source',
                DEFAULT_PROFILE_DEPENDENT: 'from-process-dependent',
                DEFAULT_PROFILE_RAW: 'from-process-raw',
                DEFAULT_PROFILE_CYCLE_A: 'ambient-a',
                DEFAULT_PROFILE_CYCLE_B: 'ambient-b',
                DEFAULT_PROFILE_SELF: 'explicit-self'
            });
            assert.strictEqual(debugConfig.cwd, projectDir);
            assert.strictEqual(debugConfig.executablePath, undefined);
            assert.strictEqual(debugConfig.checkForDevCert, undefined);
            assert.deepStrictEqual(
                Object.keys(debugConfig.env).filter(name => name.toLowerCase() === 'mode'),
                ['mode']);

            const ambientUrlDebugSessionConfig: AspireExtendedDebugConfiguration = {
                ...debugSessionConfig,
                debuggers: {
                    apphost: {
                        launchProfile: 'h3'
                    }
                }
            };
            fakeAspireDebugSession.configuration = ambientUrlDebugSessionConfig;
            const ambientUrlDebugConfig = await createDebugSessionConfiguration(
                ambientUrlDebugSessionConfig,
                launchConfig,
                undefined,
                [{ name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' }],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.strictEqual(ambientUrlDebugConfig.env.ASPNETCORE_URLS, 'http://localhost:14000');

            const cliSelectedDebugSessionConfig: AspireExtendedDebugConfiguration = {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: projectPath
            };
            fakeAspireDebugSession.configuration = cliSelectedDebugSessionConfig;
            const cliSelectedLaunchConfig: ProjectLaunchConfiguration = {
                ...launchConfig,
                launch_profile: 'h3'
            };
            const cliSelectedDebugConfig = await createDebugSessionConfiguration(
                cliSelectedDebugSessionConfig,
                cliSelectedLaunchConfig,
                undefined,
                [{ name: 'PROFILE_ROOT', value: '/profile/root' }],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.strictEqual(
                cliSelectedDebugConfig.args,
                '--profile-root /profile/root --msbuild $(PROFILE_ROOT) --missing %PROFILE_MISSING% --overlap %PROFILE_MISSING%PROFILE_ROOT%');
            assert.strictEqual(cliSelectedDebugConfig.env.PROFILE_PATH, '/profile/root/config');
            assert.strictEqual(cliSelectedDebugConfig.env.PROFILE_MSBUILD, '$(PROFILE_ROOT)/config');
            assert.strictEqual(cliSelectedDebugConfig.env.PROFILE_UNRESOLVED, '%PROFILE_MISSING%/config');
            assert.strictEqual(cliSelectedDebugConfig.env.PROFILE_OVERLAP, '%PROFILE_MISSING%PROFILE_ROOT%');

            const forwardedArguments = ['--literal', '%PROFILE_ROOT%'];
            const explicitArgumentsDebugConfig = await createDebugSessionConfiguration(
                cliSelectedDebugSessionConfig,
                cliSelectedLaunchConfig,
                forwardedArguments,
                [{ name: 'PROFILE_ROOT', value: '/profile/root' }],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.deepStrictEqual(explicitArgumentsDebugConfig.args, forwardedArguments);
        } finally {
            platformStub.restore();
            for (const [name, value] of Object.entries(inheritedEnvironment)) {
                restoreEnvironmentVariable(name, value);
            }
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('AppHost explicit environment overrides the selected launch profile marker', async () => {
        const fs = require('fs');
        const tempRoot = nodePath.join(process.cwd(), '.test-temp', `dotnet-apphost-explicit-profile-${process.pid}-${Date.now()}`);
        const projectDir = nodePath.join(tempRoot, 'AppHost');
        const propertiesDir = nodePath.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        try {
            const projectPath = nodePath.join(projectDir, 'AppHost.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(nodePath.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    h1: {
                        commandName: 'Project'
                    },
                    h2: {
                        commandName: 'Project'
                    }
                }
            }));

            const outputPath = nodePath.join(projectDir, 'bin', 'Debug', 'net10.0', 'AppHost.dll');
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'h1'
            };
            const debugSessionConfig: AspireExtendedDebugConfiguration = {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: projectPath,
                debuggers: {
                    apphost: {
                        launchProfile: 'h2',
                        env: {
                            DOTNET_LAUNCH_PROFILE: 'explicit-profile'
                        }
                    }
                }
            };
            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);
            fakeAspireDebugSession.configuration = debugSessionConfig;

            const debugConfig = await createDebugSessionConfiguration(
                debugSessionConfig,
                launchConfig,
                undefined,
                [{ name: 'DOTNET_LAUNCH_PROFILE', value: 'h1' }],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.strictEqual(debugConfig.env.DOTNET_LAUNCH_PROFILE, 'explicit-profile');
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('AppHost preserves CLI environment when no default launch profile exists', async () => {
        const fs = require('fs');
        const tempRoot = nodePath.join(process.cwd(), '.test-temp', `dotnet-apphost-no-default-profile-${process.pid}-${Date.now()}`);
        const projectDir = nodePath.join(tempRoot, 'AppHost');
        const propertiesDir = nodePath.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        try {
            const projectPath = nodePath.join(projectDir, 'AppHost.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(nodePath.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    unsupported: {
                        commandName: 'Unsupported',
                        environmentVariables: {
                            mode: 'profile-value'
                        }
                    }
                }
            }));

            const outputPath = nodePath.join(projectDir, 'bin', 'Debug', 'net10.0', 'AppHost.dll');
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };
            const debugSessionConfig: AspireExtendedDebugConfiguration = {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: projectPath
            };
            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);
            fakeAspireDebugSession.configuration = debugSessionConfig;

            const debugConfig = await createDebugSessionConfiguration(
                debugSessionConfig,
                launchConfig,
                undefined,
                [
                    { name: 'mode', value: 'from-cli' },
                    { name: 'DOTNET_LAUNCH_PROFILE', value: 'from-cli' },
                    { name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' }
                ],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.strictEqual(debugConfig.env.mode, 'from-cli');
            assert.strictEqual(debugConfig.env.DOTNET_LAUNCH_PROFILE, 'from-cli');
            assert.strictEqual(debugConfig.env.ASPNETCORE_URLS, 'http://localhost:15001');

            const disabledDebugSessionConfig: AspireExtendedDebugConfiguration = {
                ...debugSessionConfig,
                debuggers: {
                    project: {
                        disableLaunchProfile: true
                    }
                }
            };
            fakeAspireDebugSession.configuration = disabledDebugSessionConfig;
            const disabledDebugConfig = await createDebugSessionConfiguration(
                disabledDebugSessionConfig,
                launchConfig,
                undefined,
                [
                    { name: 'mode', value: 'from-cli' },
                    { name: 'DOTNET_LAUNCH_PROFILE', value: 'from-cli' },
                    { name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' }
                ],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.strictEqual(disabledDebugConfig.env.mode, 'from-cli');
            assert.strictEqual(disabledDebugConfig.env.DOTNET_LAUNCH_PROFILE, undefined);
            assert.strictEqual(disabledDebugConfig.env.ASPNETCORE_URLS, 'http://localhost:15001');
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('AppHost project debugger environment applies before explicit AppHost environment', async () => {
        const fs = require('fs');
        const tempRoot = nodePath.join(process.cwd(), '.test-temp', `dotnet-apphost-project-environment-${process.pid}-${Date.now()}`);
        const projectDir = nodePath.join(tempRoot, 'AppHost');
        const propertiesDir = nodePath.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        try {
            const projectPath = nodePath.join(projectDir, 'AppHost.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(nodePath.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    h1: {
                        commandName: 'Project'
                    }
                }
            }));

            const outputPath = nodePath.join(projectDir, 'bin', 'Debug', 'net10.0', 'AppHost.dll');
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };
            const debugSessionConfig: AspireExtendedDebugConfiguration = {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: projectPath,
                debuggers: {
                    project: {
                        env: {
                            PROJECT_ONLY: 'from-project',
                            SHARED: 'from-project'
                        }
                    },
                    apphost: {
                        env: {
                            SHARED: 'from-apphost'
                        }
                    }
                }
            };
            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);
            fakeAspireDebugSession.configuration = debugSessionConfig;

            const debugConfig = await createDebugSessionConfiguration(
                debugSessionConfig,
                launchConfig,
                undefined,
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.strictEqual(debugConfig.env.PROJECT_ONLY, 'from-project');
            assert.strictEqual(debugConfig.env.SHARED, 'from-apphost');
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('AppHost disabled profile filters old CLI profile environment and preserves inherited values', async () => {
        const fs = require('fs');
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const tempRoot = nodePath.join(process.cwd(), '.test-temp', `dotnet-apphost-disabled-profile-${process.pid}-${Date.now()}`);
        const projectDir = nodePath.join(tempRoot, 'AppHost');
        const propertiesDir = nodePath.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const inheritedEnvironment = {
            mode: process.env.mode,
            DOTNET_LAUNCH_PROFILE: process.env.DOTNET_LAUNCH_PROFILE,
            ASPNETCORE_URLS: process.env.ASPNETCORE_URLS,
            EXPLICIT: process.env.EXPLICIT
        };

        process.env.mode = 'ambient-h1';
        process.env.DOTNET_LAUNCH_PROFILE = 'h1';
        process.env.ASPNETCORE_URLS = 'http://localhost:14000';
        process.env.EXPLICIT = 'from-process';

        try {
            const projectPath = nodePath.join(projectDir, 'AppHost.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(nodePath.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    h1: {
                        commandName: 'Project',
                        applicationUrl: 'http://localhost:15001',
                        environmentVariables: {
                            mode: '1'
                        }
                    }
                }
            }));

            const outputPath = nodePath.join(projectDir, 'bin', 'Debug', 'net10.0', 'AppHost.dll');
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'h1'
            };
            const debugSessionConfig: AspireExtendedDebugConfiguration = {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: projectPath,
                debuggers: {
                    apphost: {
                        disableLaunchProfile: true
                    }
                }
            };
            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);
            fakeAspireDebugSession.configuration = debugSessionConfig;

            const debugConfig = await createDebugSessionConfiguration(
                debugSessionConfig,
                launchConfig,
                undefined,
                [
                    { name: 'MODE', value: '1' },
                    { name: 'DOTNET_LAUNCH_PROFILE', value: 'h1' },
                    { name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' },
                    { name: 'EXPLICIT', value: 'from-cli' },
                    { name: 'CLI_ONLY', value: 'from-cli' }
                ],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                extension);

            assert.strictEqual(debugConfig.env.mode, 'ambient-h1');
            assert.strictEqual(debugConfig.env.DOTNET_LAUNCH_PROFILE, undefined);
            assert.strictEqual(debugConfig.env.ASPNETCORE_URLS, 'http://localhost:14000');
            assert.strictEqual(debugConfig.env.EXPLICIT, 'from-cli');
            assert.strictEqual(debugConfig.env.CLI_ONLY, 'from-cli');
            assert.deepStrictEqual(
                Object.keys(debugConfig.env).filter(name => name.toLowerCase() === 'mode'),
                ['mode']);
        } finally {
            platformStub.restore();
            for (const [name, value] of Object.entries(inheritedEnvironment)) {
                restoreEnvironmentVariable(name, value);
            }
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('advertises the coreclr project debugger and extracts project_path for .csproj and file-based .cs', () => {
        // A DotnetProjectResource (AddDotnetProject) advertises the same "project" launch capability as
        // AddProject and emits a ProjectLaunchConfiguration carrying project_path (a .csproj or a file-based
        // .cs). The extension's .NET debugger keys purely off that "project" type + project_path, so it must
        // resolve the same coreclr debugger and project file regardless of which resource produced the config.
        assert.strictEqual(projectDebuggerExtension.resourceType, 'project');
        assert.strictEqual(projectDebuggerExtension.debugAdapter, 'coreclr');
        assert.deepStrictEqual(projectDebuggerExtension.getSupportedFileTypes(), ['.cs', '.csproj']);

        const csprojConfig: ProjectLaunchConfiguration = { type: 'project', project_path: '/tmp/Worker.csproj' };
        const fileBasedConfig: ProjectLaunchConfiguration = { type: 'project', project_path: '/tmp/app.cs' };
        assert.strictEqual(projectDebuggerExtension.getProjectFile(csprojConfig), '/tmp/Worker.csproj');
        assert.strictEqual(projectDebuggerExtension.getProjectFile(fileBasedConfig), '/tmp/app.cs');
    });

    test('invalid project launch configurations do not expose arbitrary properties', async () => {
        const secret = 'top-secret';
        const invalidLaunchConfig = {
            type: 'node',
            name: 'api',
            secret,
        } as unknown as ExecutableLaunchConfiguration;
        const info = sinon.stub(extensionLogOutputChannel, 'info');

        assert.throws(
            () => projectDebuggerExtension.getProjectFile(invalidLaunchConfig),
            (error: unknown) => {
                assert.ok(error instanceof Error);
                assert.strictEqual(error.message, 'Invalid launch configuration for node.');
                return true;
            });

        await assert.rejects(
            projectDebuggerExtension.createDebugSessionConfigurationCallback!(
                invalidLaunchConfig,
                [],
                [],
                {
                    debug: true,
                    runId: '1',
                    debugSessionId: '1',
                    isApphost: false,
                    debugSession: sinon.createStubInstance(AspireDebugSession),
                },
                {
                    runId: '1',
                    debugSessionId: '1',
                    type: 'coreclr',
                    name: 'Test Debug Config',
                    request: 'launch',
                }),
            (error: unknown) => {
                assert.ok(error instanceof Error);
                assert.strictEqual(error.message, 'Invalid launch configuration for node.');
                return true;
            });

        assert.strictEqual(info.calledOnceWithExactly('The resource type was not project for node'), true);
        assert.strictEqual(info.firstCall.args.join(' ').includes(secret), false);
    });

    test('file-based AppHost follows CLI build ownership', async () => {
        const executablePath = '/tmp/obj/Debug/net10.0/apphost';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: executablePath,
            CommandLineArguments: '',
            WorkingDirectory: '',
            EnvironmentVariables: {}
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/apphost.cs'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        const createDebugConfig = (): AspireResourceExtendedDebugConfiguration => ({
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        });

        await extension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, forceBuild: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            createDebugConfig());

        assert.strictEqual(dotNetService.buildDotNetProjectStub.notCalled, true);

        await extension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            createDebugConfig());

        assert.strictEqual(dotNetService.buildDotNetProjectStub.calledOnce, true);

        await extension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, forceBuild: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            createDebugConfig());

        assert.strictEqual(dotNetService.buildDotNetProjectStub.calledTwice, true);

        await extension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, forceBuild: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            createDebugConfig());

        assert.strictEqual(dotNetService.buildDotNetProjectStub.callCount, 3);
    });

    test('file-based AppHost suppresses the CLI run hook when resolving the run command', async () => {
        const executablePath = '/tmp/obj/Debug/net10.0/apphost';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: executablePath,
            CommandLineArguments: '',
            WorkingDirectory: '/tmp',
            EnvironmentVariables: {}
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/apphost.cs'
        };
        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };
        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, forceBuild: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(dotNetService.runApiEnvironment?.ASPIRE_SUPPRESS_CLI_RUN_HOOK, 'true');
        assert.strictEqual(debugConfig.program, executablePath);
        assert.strictEqual(dotNetService.buildDotNetProjectStub.notCalled, true);
    });

    test('file-based AppHost with selected Executable profile follows CLI build ownership', async () => {
        const fs = require('fs');
        const tempRoot = nodePath.join(process.cwd(), '.test-temp', `dotnet-executable-apphost-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = nodePath.join(tempRoot, 'apphost.cs');
            fs.writeFileSync(projectPath, '// file-based AppHost');
            fs.writeFileSync(nodePath.join(tempRoot, 'apphost.run.json'), JSON.stringify({
                profiles: {
                    tool: {
                        commandName: 'Executable',
                        executablePath: 'dotnet',
                        commandLineArgs: '--info'
                    }
                }
            }));

            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };
            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch',
                launchProfile: 'tool'
            };
            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(
                launchConfig,
                undefined,
                [],
                { debug: true, forceBuild: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                debugConfig);

            assert.strictEqual(dotNetService.buildDotNetProjectStub.notCalled, true);
            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.strictEqual(debugConfig.args, '--info');
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs project launches the dotnet run-api executable under coreclr', async () => {
        // A file-based DotnetProjectResource emits a "project" launch config whose project_path is a .cs file.
        // Unlike a .csproj (launched from its build output), a .cs app has no build output path, so the
        // extension resolves the runnable program via `dotnet run-api`. This proves the file-based half of the
        // AddDotnetProject debug contract: project_path (.cs) -> run-api ExecutablePath, launched under coreclr.
        const executablePath = '/tmp/obj/Debug/net10.0/app';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: executablePath,
            CommandLineArguments: '',
            WorkingDirectory: '',
            EnvironmentVariables: { RUNAPI_ENV: 'from-run-api' }
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/app.cs'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(extension.debugAdapter, 'coreclr');
        assert.strictEqual(debugConfig.program, executablePath);
        assert.deepStrictEqual(debugConfig.args, []);
        // cwd defaults to the file's directory; run-api's WorkingDirectory (empty here) is not consumed.
        assert.strictEqual(debugConfig.cwd, '/tmp');
        // run-api's profile-derived EnvironmentVariables are dropped (only DOTNET_ROOT* would be kept), so
        // RUNAPI_ENV must not appear.
        assert.deepStrictEqual(debugConfig.env, {});
        assert.strictEqual(dotNetService.buildDotNetProjectStub.called, true);
    });

    test('file-based .cs project preserves run-api DOTNET_ROOT host variables but drops profile env', async () => {
        // `dotnet run-api` returns the SDK default launch profile's environment variables mixed with the runtime
        // host-resolution variables the SDK injects (DOTNET_ROOT / DOTNET_ROOT_<ARCH>). The profile-derived values
        // (DOTNET_LAUNCH_PROFILE, ASPNETCORE_URLS, and the profile's own env) must be dropped because the user may
        // have selected a different profile that is resolved separately, but the DOTNET_ROOT* variables must be
        // preserved or an apphost-executable build can resolve the wrong runtime or fail to start.
        const executablePath = '/tmp/obj/Debug/net10.0/app';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: executablePath,
            CommandLineArguments: '',
            WorkingDirectory: '',
            EnvironmentVariables: {
                DOTNET_ROOT: '/usr/share/dotnet',
                DOTNET_ROOT_X64: '/usr/share/dotnet/x64',
                DOTNET_LAUNCH_PROFILE: 'default',
                ASPNETCORE_URLS: 'http://localhost:5000',
                RUNAPI_ENV: 'from-run-api'
            }
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/app.cs'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, executablePath);
        // Only the DOTNET_ROOT* runtime host-resolution variables survive; every profile-derived value is dropped.
        assert.deepStrictEqual(debugConfig.env, {
            DOTNET_ROOT: '/usr/share/dotnet',
            DOTNET_ROOT_X64: '/usr/share/dotnet/x64'
        });
    });

    test('file-based .cs project launched via the dotnet launcher keeps the run-api host args and user args', async () => {
        // When the SDK resolves a file-based app to the `dotnet` launcher, run-api returns ExecutablePath=dotnet
        // and SDK-serialized CommandLineArguments such as `exec "<TargetPath>"` for UseAppHost=false.
        // The extension must deserialize those host tokens before prepending user arguments supplied by DCP.
        const dllPath = '/tmp/obj/Debug/net10.0/app with spaces.dll';
        const workingDirectory = '/tmp/obj/Debug/net10.0';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: 'dotnet',
            CommandLineArguments: `exec "${dllPath}"`,
            WorkingDirectory: workingDirectory,
            EnvironmentVariables: { RUNAPI_ENV: 'from-run-api' }
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/app.cs'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, ['--message', 'hello'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, 'dotnet');
        assert.deepStrictEqual(debugConfig.args, ['exec', dllPath, '--message', 'hello']);
        // cwd comes from the launch profile default (the file's directory), not run-api's WorkingDirectory.
        assert.strictEqual(debugConfig.cwd, '/tmp');
        // run-api's profile-derived EnvironmentVariables are dropped (only DOTNET_ROOT* would be kept), so
        // RUNAPI_ENV must not appear.
        assert.deepStrictEqual(debugConfig.env, {});
        assert.strictEqual(dotNetService.buildDotNetProjectStub.called, true);
    });

    test('file-based .cs AppHost keeps run-api host arguments and forwarded arg tokens separate', async () => {
        // The AppHost forwards run-session arguments as discrete tokens. When run-api reports the file-based
        // host program as `dotnet exec "<TargetPath>"`, the extension must deserialize and prepend those
        // SDK-authored host tokens without joining the forwarded tokens into command-line text.
        const path = require('path');

        const projectPath = path.join(process.cwd(), '.test-temp', `dotnet-apphost-forwarded-args-${process.pid}-${Date.now()}`, 'apphost.cs');
        const projectDirectory = path.dirname(projectPath);
        const dllPath = path.join(projectDirectory, 'obj', 'Debug', 'net10.0', 'output with spaces', 'apphost.dll');
        const forwardedArgs = ['--custom', 'value with spaces', '', 'literal "quote"', String.raw`C:\tools\backslash\path`];
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: 'dotnet',
            CommandLineArguments: `exec "${dllPath}"`,
            WorkingDirectory: path.join(projectDirectory, 'from-run-api'),
            EnvironmentVariables: {}
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, forwardedArgs, [], { debug: true, forceBuild: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, 'dotnet');
        assert.deepStrictEqual(debugConfig.args, ['exec', dllPath, '--custom', 'value with spaces', '', 'literal "quote"', String.raw`C:\tools\backslash\path`]);
        assert.strictEqual(debugConfig.cwd, projectDirectory);
        assert.strictEqual(dotNetService.buildDotNetProjectStub.notCalled, true);
    });

    test('file-based .cs applies the launch profile working directory', async () => {
        // A file-based app can carry a `<name>.run.json` launch profile. When that profile sets an explicit
        // workingDirectory the extension must resolve and apply it, mirroring how launch profiles set the
        // working directory for .csproj projects. run-api's own WorkingDirectory is never consumed.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            const profileWorkingDirectory = path.join(tempRoot, 'from-profile');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        workingDirectory: profileWorkingDirectory
                    }
                }
            }));

            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: 'dotnet',
                CommandLineArguments: '',
                WorkingDirectory: path.join(tempRoot, 'from-run-api'),
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.cwd, profileWorkingDirectory);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs apphost-executable build does not duplicate launch profile arguments from run-api', async () => {
        // For an apphost-executable build, `dotnet run-api` returns the app's own executable as ExecutablePath
        // and — because it always applies the SDK default launch profile — echoes that profile's arguments back
        // in CommandLineArguments. The extension resolves the same profile arguments itself, so it must NOT also
        // prepend run-api's CommandLineArguments or the arguments would appear twice.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-dup-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        commandLineArgs: '--from-profile'
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '--from-profile',
                WorkingDirectory: '',
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            // No run session arguments (undefined) so the launch profile's arguments are the ones used.
            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            assert.strictEqual(debugConfig.args, '--from-profile');
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs with disable_launch_profile ignores run-api profile arguments, working directory, and profile environment but keeps DOTNET_ROOT', async () => {
        // With disable_launch_profile the extension selects no launch profile, but `dotnet run-api` still applies
        // the SDK default profile and returns its arguments, working directory, and environment. The profile
        // values may not leak into the debug configuration: args come only from the run session (an explicit empty
        // array here), cwd defaults to
        // the file's directory, and no profile env is applied. The runtime host-resolution variables (DOTNET_ROOT*)
        // are NOT profile-derived, so they must still be preserved even when the profile is disabled.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-disabled-profile-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        commandLineArgs: '--from-disabled-profile',
                        workingDirectory: path.join(tempRoot, 'from-disabled-profile'),
                        environmentVariables: {
                            DISABLED_PROFILE_ENV: 'should-not-appear'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '--from-disabled-profile',
                WorkingDirectory: path.join(tempRoot, 'from-run-api'),
                EnvironmentVariables: { DISABLED_PROFILE_ENV: 'should-not-appear', DOTNET_ROOT: '/usr/share/dotnet' }
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                disable_launch_profile: true
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            assert.deepStrictEqual(debugConfig.args, []);
            assert.strictEqual(debugConfig.cwd, tempRoot);
            // The profile env (DISABLED_PROFILE_ENV) is dropped; the runtime host variable DOTNET_ROOT is preserved.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT: '/usr/share/dotnet' });
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based dotnet.cs apphost named dotnet is not mistaken for the launcher', async () => {
        // A file-based app whose entry file is `dotnet.cs` builds an apphost whose TargetName — and therefore
        // executable file name — is `dotnet`/`dotnet.exe`, the same name as the launcher but at a full build-output
        // path. run-api returns that full path as ExecutablePath and echoes the SDK default profile's arguments
        // in CommandLineArguments. Because the program is an apphost (a rooted path), not the launcher (a bare
        // command name), the extension must NOT treat CommandLineArguments as host arguments — it resolves the
        // profile arguments itself, so prepending run-api's would duplicate them.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-launchername-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'dotnet.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'dotnet.run.json'), JSON.stringify({
                profiles: {
                    dotnet: {
                        commandName: 'Project',
                        commandLineArgs: '--from-profile'
                    }
                }
            }));

            // The apphost executable derives its name from the .cs file, so it is `dotnet` / `dotnet.exe`.
            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '--from-profile',
                WorkingDirectory: '',
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            // No run session arguments (undefined) so the launch profile's arguments are the ones used.
            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // The apphost's own name is `dotnet`, but it is a full path, so it is not the launcher: the profile
            // arguments appear exactly once and are not prefixed with run-api's CommandLineArguments.
            assert.strictEqual(debugConfig.args, '--from-profile');
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs drops a DOTNET_ROOT defined by the launch profile but keeps the SDK-injected host variable', async () => {
        // `dotnet run-api` applies the SDK default launch profile, whose environmentVariables can define
        // DOTNET_ROOT and then overwrite the SDK-injected value in run-api's output. A profile-derived
        // DOTNET_ROOT must not be treated as an SDK runtime host variable: with the profile disabled (as here)
        // it would otherwise leak and could launch against the wrong runtime. The architecture-specific
        // DOTNET_ROOT_X64 is NOT defined by any profile, so it is a genuine SDK host-resolution variable and
        // must be preserved.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-root-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT: '/profile/dotnet'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '',
                WorkingDirectory: '',
                // run-api echoes the profile's DOTNET_ROOT (overwriting the SDK value) alongside the
                // SDK-injected arch-specific DOTNET_ROOT_X64 that no profile defines.
                EnvironmentVariables: { DOTNET_ROOT: '/profile/dotnet', DOTNET_ROOT_X64: '/usr/share/dotnet/x64' }
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                disable_launch_profile: true
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // DOTNET_ROOT is defined by the (disabled) profile, so run-api's value is dropped; the SDK-injected
            // DOTNET_ROOT_X64 survives.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT_X64: '/usr/share/dotnet/x64' });
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs falls back to dotnet run (no debugger) when the default profile is an Executable profile and launch profile is disabled', async () => {
        // `dotnet run-api` always applies the first *supported* profile and offers no way to request a
        // no-profile command. Here an 'Executable' profile appears BEFORE a 'Project' profile, so run-api would
        // report the Executable profile's external command (e.g. `some-external-tool --version`), NOT the .cs app.
        // With the launch profile disabled the extension must not trust run-api's program; it launches the app
        // itself via `dotnet run --file <app.cs> --no-cache --no-launch-profile` with the debugger detached.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-exec-default-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    // First supported profile: run-api applies this Executable profile and reports its external
                    // command, not the .cs app.
                    runExe: {
                        commandName: 'Executable',
                        executablePath: 'some-external-tool',
                        commandLineArgs: '--version'
                    },
                    // A later 'Project' profile that is not selected here.
                    app: {
                        commandName: 'Project'
                    }
                }
            }));

            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            // The realistic run-api output for an Executable default profile is the external command. If the
            // extension (incorrectly) trusted run-api, it would launch this program. It must not.
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: 'some-external-tool',
                CommandLineArguments: '--version',
                WorkingDirectory: '',
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                disable_launch_profile: true
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            // The extension launches the app itself instead of the Executable profile's external command.
            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.deepStrictEqual(debugConfig.args, ['run', '--file', projectPath, '--no-cache', '--no-launch-profile']);
            assert.strictEqual(debugConfig.noDebug, true);
            assert.strictEqual(debugConfig.cwd, tempRoot);
            assert.deepStrictEqual(debugConfig.env, {});

            dotNetService.buildDotNetProjectStub.resetHistory();
            const appHostDebugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test AppHost Debug Config',
                request: 'launch',
                disableLaunchProfile: true
            };

            await extension.createDebugSessionConfigurationCallback!(
                launchConfig,
                [],
                [],
                { debug: true, forceBuild: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                appHostDebugConfig);

            assert.strictEqual(dotNetService.buildDotNetProjectStub.notCalled, true);
            assert.strictEqual(appHostDebugConfig.program, 'dotnet');
            assert.deepStrictEqual(appHostDebugConfig.args, ['run', '--file', projectPath, '--no-build', '--no-launch-profile', '--property:_AspireSuppressCliRunHook=true']);
            assert.strictEqual(appHostDebugConfig.noDebug, true);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs reads Properties/launchSettings.json (not just <app>.run.json) to detect an Executable default profile', async () => {
        // Regression test for the launch-settings search order. For a file-based app the .NET SDK prefers
        // Properties/launchSettings.json over <app>.run.json when locating launch settings, so `dotnet run-api`
        // applies the default profile from THAT file. The extension previously read only <app>.run.json for
        // file-based apps, so it missed an Executable default profile living in Properties/launchSettings.json
        // and wrongly trusted run-api's external command. It must now read the same file the SDK does, detect
        // the Executable default, and launch the .cs app itself via `dotnet run --file ... --no-launch-profile`.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-exec-props-${process.pid}-${Date.now()}`);
        fs.mkdirSync(path.join(tempRoot, 'Properties'), { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            // The Executable default profile lives ONLY in Properties/launchSettings.json (there is no
            // <app>.run.json). run-api applies it and reports the external command below.
            fs.writeFileSync(path.join(tempRoot, 'Properties', 'launchSettings.json'), JSON.stringify({
                profiles: {
                    runExe: {
                        commandName: 'Executable',
                        executablePath: 'some-external-tool',
                        commandLineArgs: '--version'
                    }
                }
            }));

            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: 'some-external-tool',
                CommandLineArguments: '--version',
                WorkingDirectory: '',
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                disable_launch_profile: true
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            // The extension launches the .cs app itself instead of the Executable profile's external command.
            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.deepStrictEqual(debugConfig.args, ['run', '--file', projectPath, '--no-cache', '--no-launch-profile']);
            assert.strictEqual(debugConfig.noDebug, true);
            assert.strictEqual(debugConfig.cwd, tempRoot);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs falls back to dotnet run applying the selected Project profile when the default profile is Executable', async () => {
        // The default (first) profile is an 'Executable' profile that `dotnet run-api` would apply, but the user
        // explicitly selected a later 'Project' profile. run-api still reports the Executable profile's external
        // command, so the extension launches the app itself via `dotnet run --file ... --no-launch-profile`
        // (no debugger attach) while applying the selected profile's arguments and environment.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-exec-default-select-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    runExe: {
                        commandName: 'Executable',
                        executablePath: 'some-external-tool',
                        commandLineArgs: '--version'
                    },
                    app: {
                        commandName: 'Project',
                        commandLineArgs: '--from-profile',
                        environmentVariables: {
                            APP_ENV: 'from-project-profile'
                        }
                    }
                }
            }));

            const { extension } = createDebuggerExtension('unused-build-output', null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'app'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            // No run session args (undefined): the selected profile's commandLineArgs are appended after `--`.
            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, 'dotnet');
            // The selected 'app' profile's arguments are preserved verbatim after `--`; only the path is quoted.
            assert.strictEqual(debugConfig.args, `run --file "${projectPath}" --no-cache --no-launch-profile -- --from-profile`);
            assert.strictEqual(debugConfig.noDebug, true);
            assert.strictEqual(debugConfig.cwd, tempRoot);
            assert.deepStrictEqual(debugConfig.env, { APP_ENV: 'from-project-profile' });
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('quotes a Windows root without escaping the closing quote', () => {
        assert.strictEqual(quoteCommandLineArgument('C:\\'), '"C:\\\\"');
    });

    test('file-based .cs dotnet run fallback selects the SDK from the file directory and preserves the profile working directory', async () => {
        // Same fallback as above (an Executable default profile forces `dotnet run --file ... --no-launch-profile`),
        // but the selected Project profile sets a custom workingDirectory. The dotnet muxer must run from the
        // .cs file's directory so it selects that directory's SDK, while RunWorkingDirectory separately applies
        // the profile directory to the launched app because --no-launch-profile prevents the SDK from doing so.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-exec-default-wd-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    runExe: {
                        commandName: 'Executable',
                        executablePath: 'some-external-tool',
                        commandLineArgs: '--version'
                    },
                    app: {
                        commandName: 'Project',
                        workingDirectory: 'custom',
                        commandLineArgs: '--from-profile',
                        environmentVariables: {
                            APP_ENV: 'from-project-profile'
                        }
                    }
                }
            }));

            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'app'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.strictEqual(debugConfig.args, `run --file "${projectPath}" --no-cache --no-launch-profile --property:RunWorkingDirectory="${path.join(tempRoot, 'custom')}" -- --from-profile`);
            assert.strictEqual(debugConfig.noDebug, true);
            assert.strictEqual(debugConfig.cwd, tempRoot);
            assert.deepStrictEqual(debugConfig.env, { APP_ENV: 'from-project-profile' });

            const appHostDebugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test AppHost Debug Config',
                request: 'launch',
                launchProfile: 'app'
            };

            await extension.createDebugSessionConfigurationCallback!(
                launchConfig,
                undefined,
                [],
                { debug: true, forceBuild: false, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                appHostDebugConfig);

            assert.strictEqual(dotNetService.buildDotNetProjectStub.calledOnce, true);
            assert.strictEqual(appHostDebugConfig.args, `run --file "${projectPath}" --no-build --no-launch-profile --property:_AspireSuppressCliRunHook=true --property:RunWorkingDirectory="${path.join(tempRoot, 'custom')}" -- --from-profile`);
            assert.strictEqual(appHostDebugConfig.cwd, tempRoot);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs uses the selected profile DOTNET_ROOT rather than the default profile value from run-api', async () => {
        // `dotnet run-api` always applies the SDK *default* (first) profile, so its DOTNET_ROOT reflects that
        // profile. When the extension selects a *different* profile, run-api's default-profile DOTNET_ROOT must
        // not override the selected profile's own DOTNET_ROOT. The selected profile's value wins, and the
        // SDK-injected DOTNET_ROOT_X64 (defined by no profile) is still preserved from run-api.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-root-select-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT: '/default/dotnet'
                        }
                    },
                    other: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT: '/selected/dotnet'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '',
                WorkingDirectory: '',
                // run-api applied the default profile 'app', so it returns that profile's DOTNET_ROOT.
                EnvironmentVariables: { DOTNET_ROOT: '/default/dotnet', DOTNET_ROOT_X64: '/usr/share/dotnet/x64' }
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'other'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // The selected profile's DOTNET_ROOT wins over run-api's default-profile value; DOTNET_ROOT_X64
            // (SDK-injected, defined by no profile) is preserved.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT: '/selected/dotnet', DOTNET_ROOT_X64: '/usr/share/dotnet/x64' });
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('file-based .cs preserves a genuine SDK DOTNET_ROOT even when an unrelated profile defines that name', async () => {
        // `dotnet run-api` only ever applies the SDK default (first 'Project') profile, so only that profile —
        // and the profile the extension selected — can legitimately shadow a DOTNET_ROOT* value. A DOTNET_ROOT*
        // defined by some *other*, unselected profile must NOT cause run-api's genuine SDK-injected value of the
        // same name to be discarded. Here the default/selected profile 'app' defines no DOTNET_ROOT*, while an
        // unrelated profile 'other' defines DOTNET_ROOT_X64; run-api reports a real SDK DOTNET_ROOT_X64, which
        // must be preserved so the file-based app can locate the runtime.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-root-unrelated-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    // The default (first 'Project') profile the extension selects and run-api applies. It does
                    // not define any DOTNET_ROOT*.
                    app: {
                        commandName: 'Project'
                    },
                    // An unrelated profile that is never selected. Its DOTNET_ROOT_X64 must not affect the result.
                    other: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT_X64: '/unrelated/dotnet/x64'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '',
                WorkingDirectory: '',
                // The genuine SDK-injected host variable. Neither the selected nor the run-api default profile
                // defines it.
                EnvironmentVariables: { DOTNET_ROOT_X64: '/usr/share/dotnet/x64' }
            });

            // No launch_profile: the extension uses the default profile 'app', which run-api also applied.
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // The unrelated 'other' profile's DOTNET_ROOT_X64 must not poison the exclusion set, so run-api's
            // genuine SDK-injected DOTNET_ROOT_X64 survives.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT_X64: '/usr/share/dotnet/x64' });
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('does not use dotnet run when ordinary project launch configuration requests NoDebug', async () => {
        const outputPath = '/tmp/bin/Debug/net10.0/Worker.dll';
        const { extension } = createDebuggerExtension(outputPath, null, true, true);

        const projectPath = '/tmp/Worker.csproj';
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            mode: 'NoDebug',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch',
            noDebug: true
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, outputPath);
        assert.strictEqual(debugConfig.noDebug, true);
    });

    test('uses dotnet CLI when project runtimeconfig has no runnable framework', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Spaces');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, ['--message', 'hello world'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.type, 'coreclr');
            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.deepStrictEqual(debugConfig.args, ['run', '--project', projectPath, '--no-launch-profile', '--', '--message', 'hello world']);
            assert.strictEqual(debugConfig.noDebug, true);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('project Executable profile preserves forwarded array args without expanding tokens', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-exec-args-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'ToolProject');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const spacesEnvVarName = 'ASPIRE_TEST_DEBUGGER_ARG_SPACES';
        const pathEnvVarName = 'ASPIRE_TEST_DEBUGGER_ARG_PATH';
        const backslashPath = String.raw`C:\tools\backslash\path`;
        process.env[spacesEnvVarName] = 'value with spaces';
        process.env[pathEnvVarName] = backslashPath;

        try {
            const projectPath = path.join(projectDir, 'ToolProject.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    Tool: {
                        commandName: 'Executable',
                        executablePath: 'dotnet'
                    }
                }
            }));

            const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'ToolProject.dll');
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'Tool'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);
            const forwardedArgs = ['--custom', `$(${spacesEnvVarName})`, '', 'literal "quote"', `$(${pathEnvVarName})`];

            await extension.createDebugSessionConfigurationCallback!(launchConfig, forwardedArgs, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.deepStrictEqual(debugConfig.args, forwardedArgs);
        } finally {
            delete process.env[spacesEnvVarName];
            delete process.env[pathEnvVarName];
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('preserves launch profile argument string for dotnet CLI fallback when run session arguments are absent', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Profile');
        const propertiesDir = path.join(projectDir, 'Properties');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(propertiesDir, { recursive: true });
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    Development: {
                        commandLineArgs: '--arg "value with spaces" --message "say \\"hi\\"" --path "C:\\Temp\\file.txt"'
                    }
                }
            }));

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'Development'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.args, `run --project "${projectPath}" --no-launch-profile -- --arg "value with spaces" --message "say \\"hi\\"" --path "C:\\Temp\\file.txt"`);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('ignores launch profile executable settings when using dotnet CLI fallback', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Executable Settings');
        const propertiesDir = path.join(projectDir, 'Properties');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(propertiesDir, { recursive: true });
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    Development: {
                        workingDirectory: 'custom',
                        executablePath: 'customExecutable'
                    }
                }
            }));

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'Development'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            // The dotnet CLI fallback ignores the profile's executablePath (that setting only applies to
            // 'Executable' command profiles), but it still honors the profile's workingDirectory: it is resolved
            // into cwd up front and must survive the fallback so the app runs from the configured directory.
            assert.strictEqual(debugConfig.cwd, path.join(projectDir, 'custom'));
            assert.strictEqual(debugConfig.executablePath, undefined);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('fails project launch when runtimeconfig cannot be parsed', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Invalid RuntimeConfig');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), '{');

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig),
                /Failed to inspect runtimeconfig/);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('notifies user when dotnet CLI fallback disables debugger attach', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Notification');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));

            const showInformationMessageStub = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(showInformationMessageStub.calledOnce, true);
            assert.match(showInformationMessageStub.firstCall.args[0], /breakpoints/i);
        } finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('applies launch profile settings to debug configuration', async () => {
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'TestProject');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'TestProject.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');

        const launchSettings = {
            profiles: {
                'Development': {
                    commandLineArgs: '--arg "value" --flag',
                    environmentVariables: {
                        BASE: 'base'
                    },
                    workingDirectory: 'custom',
                    executablePath: 'exePath',
                    useSSL: true,
                    launchBrowser: true,
                    applicationUrl: 'https://localhost:5001'
                }
            }
        };

        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net7.0', 'TestProject.dll');
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Development'
        };

        // Provide a run session env that overrides BASE and adds RUN
        const runEnv = [
            { name: 'BASE', value: 'overridden' },
            { name: 'RUN', value: 'run' }
        ];

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, runEnv, { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        // program should be set
        assert.strictEqual(debugConfig.program, outputPath);

        // cwd should resolve to projectDir/custom
        assert.strictEqual(debugConfig.cwd, path.join(projectDir, 'custom'));

        // args should be parsed from commandLineArgs
        assert.deepStrictEqual(debugConfig.args, '--arg "value" --flag');

        // env should include merged values with run session overriding base
        assert.strictEqual(debugConfig.env.BASE, 'overridden');
        assert.strictEqual(debugConfig.env.RUN, 'run');

        // executablePath and checkForDevCert
        assert.strictEqual(debugConfig.executablePath, 'exePath');
        assert.strictEqual(debugConfig.checkForDevCert, true);

        // launchSettings.json sets launchBrowser with applicationUrl https://localhost:5001, but the app
        // host owns this resource's endpoints and can bind it elsewhere, so that address is not where the
        // resource actually listens. `aspire run` never opens it either — Aspire.Hosting parses
        // launchBrowser and never reads it — so the extension must not open a URL the CLI would not.
        assert.strictEqual(debugConfig.serverReadyAction, undefined);

        // cleanup
        removeDirectorySafely(tempDir);
    });

    test('preserves serverReadyAction from project debugger settings', async () => {
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'WebProject');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'WebProject.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');
        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
            profiles: {
                Development: {
                    commandName: 'Project',
                    launchBrowser: true,
                    applicationUrl: 'https://localhost:5001'
                }
            }
        }, null, 2));

        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net7.0', 'WebProject.dll');
        const { extension } = createDebuggerExtension(outputPath, null, true, true);
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Development'
        };
        const serverReadyAction = {
            action: 'openIntegratedBrowser',
            pattern: 'Now listening on:\\s+\\[?(https?://[^\\]\\s]+)'
        };
        const debugSessionConfig: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: projectPath,
            debuggers: {
                project: {
                    serverReadyAction
                }
            }
        };
        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        const debugConfig = await createDebugSessionConfiguration(
            debugSessionConfig,
            launchConfig,
            undefined,
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            extension);

        assert.deepStrictEqual(debugConfig.serverReadyAction, serverReadyAction);

        removeDirectorySafely(tempDir);
    });

    test('does not open the launch profile URL for the orchestrated dashboard resource', async () => {
        // Bug: running an AppHost from VS Code opened http://localhost:15888 — the address in the Aspire
        // dashboard project's own launchSettings.json — instead of the dashboard the app host actually
        // started, which listens on a dynamic port and needs a login token. The app host reassigns this
        // resource's URLs, so the on-disk profile is stale, and the run-session payload carries no
        // endpoint data for the extension to correct it with.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'Aspire.Dashboard');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'Aspire.Dashboard.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');
        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
            profiles: {
                Development: {
                    commandName: 'Project',
                    launchBrowser: true,
                    applicationUrl: 'http://localhost:15888'
                }
            }
        }, null, 2));

        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'Aspire.Dashboard.dll');
        const { extension } = createDebuggerExtension(outputPath, null, true, true);
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Development'
        };
        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Aspire.Dashboard',
            request: 'launch'
        };
        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(
            launchConfig,
            undefined,
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.serverReadyAction, undefined);

        removeDirectorySafely(tempDir);
    });

    test('uses executable path for Executable command launch profiles instead of project output', async () => {
        // Bug #15647: Executable command profiles use the executablePath and
        // commandLineArgs to define how to run the class library project. The extension
        // should use executablePath as the program instead of the project's output DLL.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'MyClassLibFunction');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'MyClassLibFunction.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');

        const launchSettings = {
            profiles: {
                'Aspire_my-function': {
                    commandName: 'Executable',
                    executablePath: 'dotnet',
                    commandLineArgs: 'exec --depsfile ./MyClassLibFunction.deps.json --runtimeconfig ./MyClassLibFunction.runtimeconfig.json RuntimeSupport.dll MyClassLibFunction::MyClassLibFunction.Function::FunctionHandler',
                    workingDirectory: 'bin/Debug/net10.0/',
                    environmentVariables: {
                        FUNCTION_ENV: 'test'
                    }
                }
            }
        };

        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

        // The output path would be a class library DLL - this should NOT be used as program
        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyClassLibFunction.dll');
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Aspire_my-function'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        // program should be the executable path from the profile, NOT the project output DLL
        assert.strictEqual(debugConfig.program, 'dotnet');

        // args should come from the profile's commandLineArgs
        assert.strictEqual(debugConfig.args, 'exec --depsfile ./MyClassLibFunction.deps.json --runtimeconfig ./MyClassLibFunction.runtimeconfig.json RuntimeSupport.dll MyClassLibFunction::MyClassLibFunction.Function::FunctionHandler');

        // cwd should resolve to the profile's working directory
        assert.strictEqual(debugConfig.cwd, path.resolve(propertiesDir, 'bin/Debug/net10.0/'));

        // env should include the profile's environment variables
        assert.strictEqual(debugConfig.env.FUNCTION_ENV, 'test');

        // project should still be built (to compile the class library dependencies)
        assert.strictEqual(dotNetService.buildDotNetProjectStub.calledOnce, true);

        // cleanup
        removeDirectorySafely(tempDir);
    });

    test('fails project launch when the selected Executable launch profile has no executablePath', async () => {
        // An Executable-command profile requires an executablePath. The .NET SDK's ExecutableProvider errors
        // when it is missing, so the extension must surface a configuration error rather than silently falling
        // through and launching the project output.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        try {
            const projectDir = path.join(tempDir, 'MyClassLibFunction');
            const propertiesDir = path.join(projectDir, 'Properties');
            fs.mkdirSync(propertiesDir, { recursive: true });

            const projectPath = path.join(projectDir, 'MyClassLibFunction.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');

            const launchSettings = {
                profiles: {
                    'Aspire_my-function': {
                        commandName: 'Executable',
                        commandLineArgs: 'exec RuntimeSupport.dll'
                    }
                }
            };
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

            const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyClassLibFunction.dll');
            const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'Aspire_my-function'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig),
                /Launch profile 'Aspire_my-function' uses commandName 'Executable' but does not specify an executablePath/);

            // The invalid profile must be rejected before any build/launch work happens.
            assert.strictEqual(dotNetService.buildDotNetProjectStub.called, false);
        } finally {
            removeDirectorySafely(tempDir);
        }
    });

    test('fails project launch when the default Executable launch profile has no executablePath', async () => {
        // With no explicit launch_profile, the extension resolves the SDK default (first supported) profile.
        // When that default is an Executable profile without an executablePath, `dotnet run` would error, so
        // the extension must surface the same configuration error instead of launching the project output.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        try {
            const projectDir = path.join(tempDir, 'MyClassLibFunction');
            const propertiesDir = path.join(projectDir, 'Properties');
            fs.mkdirSync(propertiesDir, { recursive: true });

            const projectPath = path.join(projectDir, 'MyClassLibFunction.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');

            const launchSettings = {
                profiles: {
                    'RunExe': {
                        commandName: 'Executable',
                        commandLineArgs: 'exec RuntimeSupport.dll'
                    }
                }
            };
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

            const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyClassLibFunction.dll');
            const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig),
                /Launch profile 'RunExe' uses commandName 'Executable' but does not specify an executablePath/);

            assert.strictEqual(dotNetService.buildDotNetProjectStub.called, false);
        } finally {
            removeDirectorySafely(tempDir);
        }
    });

    test('fails explicit project launch when the selected profile is missing, malformed, or has an unsupported commandName', async () => {
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        try {
            const projectDir = path.join(tempDir, 'AppHost');
            const propertiesDir = path.join(projectDir, 'Properties');
            fs.mkdirSync(propertiesDir, { recursive: true });

            const projectPath = path.join(projectDir, 'AppHost.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), `{
                "profiles": {
                    "invalid": {
                        "commandName": "project",
                        "commandLineArgs": 42
                    },
                    "malformed": {
                        "commandName": "Project",
                        "launchBrowser": "yes"
                    },
                    "duplicateMalformed": {
                        "commandName": "Project",
                        "launchBrowser": "yes",
                        "launchBrowser": true
                    }
                }
            }`);

            const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'AppHost.dll');
            const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);
            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };
            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);
            const unsupportedLaunchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'invalid'
            };
            const missingLaunchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'missing'
            };
            const malformedLaunchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'malformed'
            };
            const duplicateMalformedLaunchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'duplicateMalformed'
            };

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(
                    unsupportedLaunchConfig,
                    undefined,
                    [],
                    { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                    debugConfig),
                /Launch profile 'invalid' uses a commandName that dotnet run does not support/);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(
                    malformedLaunchConfig,
                    undefined,
                    [],
                    { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                    debugConfig),
                /Launch profile 'malformed' contains property values that dotnet run does not support/);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(
                    duplicateMalformedLaunchConfig,
                    undefined,
                    [],
                    { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                    debugConfig),
                /Launch profile 'duplicateMalformed' contains property values that dotnet run does not support/);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(
                    missingLaunchConfig,
                    undefined,
                    [],
                    { debug: true, runId: '1', debugSessionId: '1', isApphost: true, debugSession: fakeAspireDebugSession },
                    debugConfig),
                /Launch profile 'missing' could not be uniquely resolved/);

            assert.strictEqual(dotNetService.buildDotNetProjectStub.called, false);
        } finally {
            removeDirectorySafely(tempDir);
        }
    });

    test('expands environment variables in Executable profile executablePath and commandLineArgs', async () => {
        // Executable launch profiles may contain $(VAR) references (e.g. $(HOME)) that
        // VS expands natively but the coreclr debugger does not. The extension must expand
        // these before passing them to the debug configuration.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'MyToolProject');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'MyToolProject.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');

        // Set up a test env var so expansion is deterministic
        const envVarName = 'ASPIRE_TEST_TOOL_ROOT';
        const envVarValue = '/opt/tools';
        process.env[envVarName] = envVarValue;

        const launchSettings = {
            profiles: {
                'Aspire_my-tool': {
                    commandName: 'Executable',
                    executablePath: '$(' + envVarName + ')/bin/dotnet',
                    commandLineArgs: 'exec --depsfile ./MyToolProject.deps.json $(' + envVarName + ')/lib/RuntimeSupport.dll MyToolProject::MyToolProject.Function::Handler',
                    workingDirectory: 'bin/Debug/net10.0/'
                }
            }
        };

        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyToolProject.dll');
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Aspire_my-tool'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        // executablePath should have $(VAR) expanded
        assert.strictEqual(debugConfig.program, `${envVarValue}/bin/dotnet`);

        // commandLineArgs should also have $(VAR) expanded
        assert.strictEqual(debugConfig.args, `exec --depsfile ./MyToolProject.deps.json ${envVarValue}/lib/RuntimeSupport.dll MyToolProject::MyToolProject.Function::Handler`);

        // cleanup
        delete process.env[envVarName];
        removeDirectorySafely(tempDir);
    });
});

/**
 * All unit tests share one extension host, so background extension work - an editor event that
 * queues AppHost discovery, for example - can reach a freshly installed stub before the call the
 * test itself makes. Selecting the process by the project it was started for keeps these assertions
 * about the dotnet command under test rather than about whichever process happened to start first.
 */
function msbuildCallFor(stub: sinon.SinonStub, projectPath: string): sinon.SinonSpyCall<any[], any> {
    return dotnetCallFor(stub, projectPath, 'dotnet msbuild');
}

function buildCallFor(stub: sinon.SinonStub, projectPath: string): sinon.SinonSpyCall<any[], any> {
    return dotnetCallFor(stub, projectPath, 'dotnet build');
}

function dotnetCallFor(stub: sinon.SinonStub, projectPath: string, description: string): sinon.SinonSpyCall<any[], any> {
    const call = stub.getCalls().find(candidate => {
        const args = candidate.args[1];
        return Array.isArray(args) && args.includes(projectPath);
    });

    assert.ok(call, `${description} was not started for '${projectPath}'. Started: ${JSON.stringify(stub.getCalls().map(candidate => candidate.args[1]))}`);
    return call;
}
