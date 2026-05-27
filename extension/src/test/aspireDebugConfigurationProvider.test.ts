/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugConfigurationProvider } from '../debugger/AspireDebugConfigurationProvider';
import { AppHostDiscoveryService } from '../utils/appHostDiscovery';

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

    test('provides dynamic launch config when active file resolves to AppHost candidate', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const projectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(projectPath));
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, projectPath);
    });

    test('provides default dynamic launch config when active file is not an AppHost candidate', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'Web', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(programPath, null));
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
    });

    test('provides default dynamic launch config when discovery fails', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createFailingAppHostDiscoveryService());
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
    });

    test('provides default dynamic launch config when there is no active editor', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(folder.uri.fsPath, null));
        sandbox.stub(vscode.window, 'activeTextEditor').value(undefined);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
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
    return {
        resolveDebugTarget: async () => resolvedPath,
        tryFindCandidateForEditorFile: async () => candidatePath ? {
            path: candidatePath,
            language: language,
            status: 'buildable',
        } : undefined,
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
