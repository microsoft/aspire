/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugConfigurationProvider } from '../debugger/AspireDebugConfigurationProvider';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';
import * as cliPathModule from '../utils/cliPath';
import { AppHostDiscoveryService } from '../utils/appHostDiscovery';
import { appHostSelectionOriginConfigKey } from '../debugger/AspireDebugConfigurationMetadata';

suite('AspireDebugConfigurationProvider', () => {
    let tempDir: string;
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-debug-configuration-provider-'));
    });

    teardown(() => {
        sandbox.restore();
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    test('resolves launch config SDK-style AppHost Program.cs to containing project file', async () => {
        const appHostDirectory = path.join(tempDir, 'AppHost');
        fs.mkdirSync(appHostDirectory);

        const programPath = path.join(appHostDirectory, 'Program.cs');
        const projectPath = path.join(appHostDirectory, 'AppHost.csproj');
        fs.writeFileSync(programPath, 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();');
        fs.writeFileSync(projectPath, '<Project Sdk="Microsoft.NET.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(projectPath));
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.program, projectPath);
        assert.strictEqual(config?.__aspireAppHostSelectionOrigin, 'explicit-launch-configuration');
    });

    test('leaves launch config single-file apphost.cs unchanged', async () => {
        const appHostPath = path.join(tempDir, 'apphost.cs');
        fs.writeFileSync(appHostPath, '#:sdk Aspire.AppHost.Sdk\nvar builder = DistributedApplication.CreateBuilder(args);');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath));
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath
        });

        assert.strictEqual(config?.program, appHostPath);
    });

    test('leaves launch config TypeScript apphost.ts unchanged', async () => {
        const appHostPath = path.join(tempDir, 'apphost.ts');
        fs.writeFileSync(appHostPath, 'import { createBuilder } from "./.aspire/modules/aspire";');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath, appHostPath, 'typescript/nodejs'));
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath
        });

        assert.strictEqual(config?.program, appHostPath);
    });

    test('leaves launch config non-AppHost C# source file unchanged', async () => {
        const appDirectory = path.join(tempDir, 'App');
        fs.mkdirSync(appDirectory);

        const programPath = path.join(appDirectory, 'Program.cs');
        fs.writeFileSync(programPath, 'Console.WriteLine("Hello");');
        fs.writeFileSync(path.join(appDirectory, 'App.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(programPath));
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.program, programPath);
    });

    test('treats workspace folder launch target as default discovery and records AppHost telemetry target', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const appHostPath = path.join(tempDir, 'NestedAppHost', 'apphost.ts');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath));

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folder.uri.fsPath
        });

        assert.strictEqual(config?.program, folder.uri.fsPath);
        assert.strictEqual(config?.__aspireAppHostTelemetryTargetPath, appHostPath);
        assert.strictEqual(config?.__aspireAppHostSelectionOrigin, 'default-discovery');
    });

    test('treats launch target in an AppHost subdirectory as an explicit launch configuration', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const appHostDirectory = path.join(tempDir, 'FirstAppHost');
        fs.mkdirSync(appHostDirectory);
        const appHostPath = path.join(appHostDirectory, 'FirstAppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Microsoft.NET.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath));

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostDirectory
        });

        assert.strictEqual(config?.__aspireAppHostSelectionOrigin, 'explicit-launch-configuration');
    });

    test('omits the selection origin from initial launch configurations written to launch.json', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const projectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
        const provider = new AspireDebugConfigurationProvider(
            createAppHostDiscoveryService(projectPath),
            vscode.DebugConfigurationProviderTriggerKind.Initial);
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, projectPath);
        assert.ok(!(appHostSelectionOriginConfigKey in configs[0]));
    });

    test('classifies an initial launch configuration as explicit once it is resolved from launch.json', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const projectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
        const provider = new AspireDebugConfigurationProvider(
            createAppHostDiscoveryService(projectPath),
            vscode.DebugConfigurationProviderTriggerKind.Initial);
        setActiveEditor(programPath, folder);

        const [provided] = await provider.provideDebugConfigurations(folder);
        const config = await provider.resolveDebugConfiguration(folder, { ...provided, skipCliAvailabilityCheck: true });

        assert.strictEqual(config?.__aspireAppHostSelectionOrigin, 'explicit-launch-configuration');
    });

    test('provides dynamic launch config when active file resolves to AppHost candidate', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const projectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(projectPath));
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, projectPath);
        assert.strictEqual(configs[0].__aspireAppHostSelectionOrigin, 'default-discovery');
    });

    test('provides default dynamic launch config when active file is not an AppHost candidate', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'Web', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(programPath, null));
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
        assert.strictEqual(configs[0].__aspireAppHostSelectionOrigin, 'default-discovery');
    });

    test('provides default dynamic launch config when discovery fails', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createFailingAppHostDiscoveryService());
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
        assert.strictEqual(configs[0].__aspireAppHostSelectionOrigin, 'default-discovery');
    });

    test('provides default dynamic launch config when there is no active editor', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(folder.uri.fsPath, null));
        sandbox.stub(vscode.window, 'activeTextEditor').value(undefined);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
        assert.strictEqual(configs[0].__aspireAppHostSelectionOrigin, 'default-discovery');
    });

    test('leaves launch config program unchanged when debug target resolution fails', async () => {
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createFailingAppHostDiscoveryService());

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.program, programPath);
    });

    test('resolveDebugConfiguration keeps skip flag through repeated resolver calls after launch service already checked CLI', async () => {
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService('/repo/AppHost.csproj'));
        const resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
        const showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);

        const initialConfig = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: '/repo/AppHost.csproj',
            skipCliAvailabilityCheck: true,
        } as AspireExtendedDebugConfiguration;

        const firstConfig = await provider.resolveDebugConfiguration(undefined, initialConfig) as AspireExtendedDebugConfiguration | undefined;
        const config = firstConfig
            ? await provider.resolveDebugConfiguration(undefined, firstConfig) as AspireExtendedDebugConfiguration | undefined
            : undefined;

        assert.ok(config);
        assert.strictEqual(config.program, '/repo/AppHost.csproj');
        assert.strictEqual(config.skipCliAvailabilityCheck, true);
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'explicit-launch-configuration');
        assert.strictEqual(resolveCliPathStub.called, false);
        assert.strictEqual(showErrorMessageStub.called, false);
    });

    test('preserves default discovery provenance when resolving its populated program', async () => {
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService('/repo/AppHost.csproj'));
        const config = await provider.resolveDebugConfiguration(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: '/repo/AppHost.csproj',
            skipCliAvailabilityCheck: true,
            __aspireAppHostSelectionOrigin: 'default-discovery',
        } as AspireExtendedDebugConfiguration) as AspireExtendedDebugConfiguration | undefined;

        assert.ok(config);
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'default-discovery');
    });

    test('resolveDebugConfigurationWithSubstitutedVariables removes internal skip flag before launch', async () => {
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService('/repo/AppHost.csproj'));

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: '/repo/AppHost.csproj',
            skipCliAvailabilityCheck: true,
        } as AspireExtendedDebugConfiguration) as AspireExtendedDebugConfiguration | undefined;

        assert.ok(config);
        assert.strictEqual(config.skipCliAvailabilityCheck, undefined);
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'explicit-launch-configuration');
    });

    function setActiveEditor(filePath: string, folder: vscode.WorkspaceFolder): void {
        sandbox.stub(vscode.window, 'activeTextEditor').value({
            document: {
                uri: vscode.Uri.file(filePath),
            },
        });
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(folder);
    }
});

function createWorkspaceFolder(folderPath: string): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(folderPath),
        name: 'workspace',
        index: 0,
    };
}

function createAppHostDiscoveryService(resolvedPath: string, candidatePath: string | null = resolvedPath, language = 'csharp'): AppHostDiscoveryService {
    const createCandidate = () => candidatePath ? {
        path: candidatePath,
        language: language,
        status: 'buildable',
    } : undefined;

    return {
        resolveDebugTarget: async (filePath: string, folder?: vscode.WorkspaceFolder) => folder && path.resolve(filePath) === path.resolve(folder.uri.fsPath) ? filePath : resolvedPath,
        tryFindWorkspaceDefaultCandidate: async (filePath: string, folder?: vscode.WorkspaceFolder) => folder && path.resolve(filePath) === path.resolve(folder.uri.fsPath) ? createCandidate() : undefined,
        tryFindCandidateForEditorFile: async () => createCandidate(),
    } as unknown as AppHostDiscoveryService;
}

function createFailingAppHostDiscoveryService(): AppHostDiscoveryService {
    return {
        resolveDebugTarget: async () => {
            throw new Error('discovery failed');
        },
        tryFindCandidateForEditorFile: async () => {
            throw new Error('discovery failed');
        },
    } as unknown as AppHostDiscoveryService;
}
