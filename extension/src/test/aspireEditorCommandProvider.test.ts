/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AppHostDiscoveryService } from '../utils/appHostDiscovery';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
import * as cliPathModule from '../utils/cliPath';
import { noAppHostInWorkspace } from '../loc/strings';

import { removeDirectorySafely } from './testHelpers';
function createEditor(filePath: string): vscode.TextEditor {
    return {
        document: {
            uri: vscode.Uri.file(filePath),
            fileName: filePath,
            languageId: filePath.endsWith('.ts') ? 'typescript' : filePath.endsWith('.rs') ? 'rust' : 'csharp'
        } as vscode.TextDocument
    } as vscode.TextEditor;
}

function createLaunchService(): AppHostLaunchService {
    return new AppHostLaunchService({
        getCapabilityStatus: async () => 'supported',
    });
}

suite('AspireEditorCommandProvider', () => {
    let tempDir: string;
    let activeEditor: vscode.TextEditor | undefined;
    let activeEditorStub: sinon.SinonStub;
    let workspaceFoldersStub: sinon.SinonStub;
    let getWorkspaceFolderStub: sinon.SinonStub;
    let onDidChangeWorkspaceFoldersStub: sinon.SinonStub;
    let onDidChangeActiveTextEditorStub: sinon.SinonStub;
    let executeCommandStub: sinon.SinonStub;
    let startDebuggingStub: sinon.SinonStub;
    let showErrorMessageStub: sinon.SinonStub;
    let resolveCliPathStub: sinon.SinonStub;

    setup(() => {
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-editor-command-provider-'));
        activeEditor = undefined;

        activeEditorStub = sinon.stub(vscode.window, 'activeTextEditor').get(() => activeEditor);
        workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value(undefined);
        getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) => {
            // VS Code lowercases the drive letter in fsPath, so the raw mkdtemp path does not
            // prefix-match its own URI on Windows. Normalise both sides through Uri.file.
            if (uri.fsPath.startsWith(vscode.Uri.file(tempDir).fsPath)) {
                return { uri: vscode.Uri.file(tempDir), name: 'test', index: 0 };
            }

            return undefined;
        });
        onDidChangeWorkspaceFoldersStub = sinon.stub(vscode.workspace, 'onDidChangeWorkspaceFolders').returns({ dispose: () => { } } as vscode.Disposable);
        onDidChangeActiveTextEditorStub = sinon.stub(vscode.window, 'onDidChangeActiveTextEditor').returns({ dispose: () => { } } as vscode.Disposable);
        executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        // AppHostLaunchService.launch gates on CLI availability before starting the debug
        // session, so stub resolution to "available" here. Otherwise the gate resolves to
        // not-found on hosts without the Aspire CLI and the launch path throws a
        // CancellationError before vscode.debug.startDebugging is ever called.
        resolveCliPathStub = sinon.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: 'aspire', available: true, source: 'path' });
    });

    teardown(() => {
        resolveCliPathStub.restore();
        showErrorMessageStub.restore();
        startDebuggingStub.restore();
        executeCommandStub.restore();
        onDidChangeActiveTextEditorStub.restore();
        onDidChangeWorkspaceFoldersStub.restore();
        getWorkspaceFolderStub.restore();
        workspaceFoldersStub.restore();
        activeEditorStub.restore();
        removeDirectorySafely(tempDir);
    });

    test('returns containing project file when active editor is SDK-style AppHost Program.cs', async () => {
        const appHostDirectory = path.join(tempDir, 'AppHost');
        fs.mkdirSync(appHostDirectory);

        const programPath = path.join(appHostDirectory, 'Program.cs');
        const projectPath = path.join(appHostDirectory, 'AppHost.csproj');
        fs.writeFileSync(programPath, 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();');
        fs.writeFileSync(projectPath, '<Project Sdk="Microsoft.NET.Sdk" />');
        activeEditor = createEditor(programPath);

        const provider = new AspireEditorCommandProvider(createAppHostDiscoveryService(projectPath), createLaunchService());
        try {
            assert.strictEqual(await provider.getAppHostPath(), projectPath);
        }
        finally {
            provider.dispose();
        }
    });

    test('returns source file when active editor is single-file apphost.cs', async () => {
        const appHostPath = path.join(tempDir, 'apphost.cs');
        fs.writeFileSync(appHostPath, '#:sdk Aspire.AppHost.Sdk\nvar builder = DistributedApplication.CreateBuilder(args);');
        activeEditor = createEditor(appHostPath);

        const provider = new AspireEditorCommandProvider(createAppHostDiscoveryService(appHostPath), createLaunchService());
        try {
            assert.strictEqual(await provider.getAppHostPath(), appHostPath);
        }
        finally {
            provider.dispose();
        }
    });

    test('returns source file when active editor is TypeScript apphost.ts', async () => {
        const appHostPath = path.join(tempDir, 'apphost.ts');
        fs.writeFileSync(appHostPath, 'import { createBuilder } from "./.aspire/modules/aspire";');
        activeEditor = createEditor(appHostPath);

        const provider = new AspireEditorCommandProvider(createAppHostDiscoveryService(appHostPath, 'typescript/nodejs'), createLaunchService());
        try {
            assert.strictEqual(await provider.getAppHostPath(), appHostPath);
        }
        finally {
            provider.dispose();
        }
    });

    test('returns source file when active editor is Rust apphost.rs', async () => {
        const appHostPath = path.join(tempDir, 'apphost.rs');
        fs.writeFileSync(appHostPath, 'fn main() {}');
        activeEditor = createEditor(appHostPath);

        const provider = new AspireEditorCommandProvider(createAppHostDiscoveryService(appHostPath, 'rust'), createLaunchService());
        try {
            assert.strictEqual(await provider.getAppHostPath(), appHostPath);
        }
        finally {
            provider.dispose();
        }
    });

    test('clears AppHost contexts when discovery fails while processing document', async () => {
        const programPath = path.join(tempDir, 'Program.cs');
        fs.writeFileSync(programPath, 'var builder = DistributedApplication.CreateBuilder(args);');
        activeEditor = createEditor(programPath);

        const provider = new AspireEditorCommandProvider(createFailingAppHostDiscoveryService(), createLaunchService());
        try {
            await provider.processDocument(activeEditor.document);

            assert.ok(executeCommandStub.calledWith('setContext', 'aspire.fileIsAppHost', false));
            assert.ok(executeCommandStub.calledWith('setContext', 'aspire.workspaceHasAppHost', false));
        }
        finally {
            provider.dispose();
        }
    });

    test('returns null when discovery fails while resolving AppHost path', async () => {
        const programPath = path.join(tempDir, 'Program.cs');
        fs.writeFileSync(programPath, 'var builder = DistributedApplication.CreateBuilder(args);');
        activeEditor = createEditor(programPath);

        const provider = new AspireEditorCommandProvider(createFailingAppHostDiscoveryService(), createLaunchService());
        try {
            assert.strictEqual(await provider.getAppHostPath(), null);
        }
        finally {
            provider.dispose();
        }
    });

    test('run command uses resolved AppHost path from discovery', async () => {
        const appHostDirectory = path.join(tempDir, 'ResolvedAppHost');
        fs.mkdirSync(appHostDirectory);

        const appHostPath = path.join(appHostDirectory, 'ResolvedAppHost.csproj');
        const programPath = path.join(appHostDirectory, 'Program.cs');
        fs.writeFileSync(appHostPath, '<Project Sdk="Microsoft.NET.Sdk" />');
        fs.writeFileSync(programPath, 'var builder = DistributedApplication.CreateBuilder(args);');
        activeEditor = createEditor(programPath);

        const provider = new AspireEditorCommandProvider(createAppHostDiscoveryService(appHostPath), createLaunchService());
        try {
            await provider.tryExecuteRunAppHost(true);

            assert.ok(startDebuggingStub.calledOnce);
            const launchConfiguration = startDebuggingStub.firstCall.args[1] as vscode.DebugConfiguration;
            assert.strictEqual(launchConfiguration.program, appHostPath);
            assert.strictEqual(launchConfiguration.command, 'run');
            assert.strictEqual(launchConfiguration.noDebug, true);
            assert.strictEqual(showErrorMessageStub.called, false);
        }
        finally {
            provider.dispose();
        }
    });

    test('explicit AppHost URI wins over the active editor AppHost', async () => {
        const appHostADirectory = path.join(tempDir, 'AppHostA');
        const appHostBDirectory = path.join(tempDir, 'AppHostB');
        fs.mkdirSync(appHostADirectory);
        fs.mkdirSync(appHostBDirectory);

        const appHostAPath = path.join(appHostADirectory, 'AppHostA.csproj');
        const appHostBPath = path.join(appHostBDirectory, 'AppHostB.csproj');
        const programAPath = path.join(appHostADirectory, 'Program.cs');
        fs.writeFileSync(appHostAPath, '<Project Sdk="Microsoft.NET.Sdk" />');
        fs.writeFileSync(appHostBPath, '<Project Sdk="Microsoft.NET.Sdk" />');
        fs.writeFileSync(programAPath, 'var builder = DistributedApplication.CreateBuilder(args);');
        activeEditor = createEditor(programAPath);

        const discoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            tryFindCandidateForEditorFile: async (filePath: string) => ({
                path: filePath === vscode.Uri.file(appHostBPath).fsPath ? appHostBPath : appHostAPath,
                language: 'csharp',
                status: 'buildable',
            }),
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireEditorCommandProvider(discoveryService, createLaunchService());

        try {
            await provider.tryExecuteRunAppHost(true, vscode.Uri.file(appHostBPath));

            const launchConfiguration = startDebuggingStub.firstCall.args[1] as vscode.DebugConfiguration;
            assert.strictEqual(launchConfiguration.program, appHostBPath);
        }
        finally {
            provider.dispose();
        }
    });

    test('active editor URI falls back to the workspace AppHost', async () => {
        const activeDocumentPath = path.join(tempDir, 'Service.java');
        const workspaceAppHostPath = path.join(tempDir, 'AppHost.java');
        fs.writeFileSync(activeDocumentPath, 'public class Service {}');
        fs.writeFileSync(workspaceAppHostPath, 'public class AppHost {}');
        activeEditor = createEditor(activeDocumentPath);

        const discoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            tryFindCandidateForEditorFile: async () => undefined,
            discover: async () => [{
                path: workspaceAppHostPath,
                language: 'java',
                status: 'buildable',
            }],
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireEditorCommandProvider(discoveryService, createLaunchService());

        try {
            await provider.tryExecuteRunAppHost(true);

            assert.ok(startDebuggingStub.calledOnce);
            const launchConfiguration = startDebuggingStub.firstCall.args[1] as vscode.DebugConfiguration;
            assert.strictEqual(launchConfiguration.program, workspaceAppHostPath);
        }
        finally {
            provider.dispose();
        }
    });

    test('explicit active editor AppHost URI does not fall back to another workspace AppHost', async () => {
        const activeAppHostPath = path.join(tempDir, 'AppHost.java');
        const workspaceAppHostPath = path.join(tempDir, 'OtherAppHost.java');
        fs.writeFileSync(activeAppHostPath, 'public class AppHost {}');
        fs.writeFileSync(workspaceAppHostPath, 'public class OtherAppHost {}');
        activeEditor = createEditor(activeAppHostPath);

        const discoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            tryFindCandidateForEditorFile: async () => undefined,
            discover: async () => [{
                path: workspaceAppHostPath,
                language: 'java',
                status: 'buildable',
            }],
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireEditorCommandProvider(discoveryService, createLaunchService());

        try {
            await provider.tryExecuteRunAppHost(true, activeEditor.document.uri, false);

            assert.strictEqual(startDebuggingStub.called, false);
            assert.ok(showErrorMessageStub.calledOnceWith(noAppHostInWorkspace));
        }
        finally {
            provider.dispose();
        }
    });

    test('explicit non-AppHost URI does not fall back to another workspace AppHost', async () => {
        const workspaceAppHostPath = path.join(tempDir, 'AppHost.java');
        const activeDocumentPath = path.join(tempDir, 'Service.java');
        fs.writeFileSync(workspaceAppHostPath, 'public class AppHost {}');
        fs.writeFileSync(activeDocumentPath, 'public class Service {}');
        activeEditor = createEditor(activeDocumentPath);
        const explicitUri = activeEditor.document.uri;

        const discoveryService = {
            onDidChangeCandidates: () => ({ dispose: () => { } }),
            tryFindCandidateForEditorFile: async () => undefined,
            discover: async () => [{
                path: workspaceAppHostPath,
                language: 'java',
                status: 'buildable',
            }],
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireEditorCommandProvider(discoveryService, createLaunchService());

        try {
            await provider.tryExecuteRunAppHost(true, explicitUri, false);

            assert.strictEqual(startDebuggingStub.called, false);
            assert.ok(showErrorMessageStub.calledOnce);
        }
        finally {
            provider.dispose();
        }
    });

});

function createAppHostDiscoveryService(resolvedPath: string, language = 'csharp'): AppHostDiscoveryService {
    return {
        onDidChangeCandidates: () => ({ dispose: () => { } }),
        tryFindCandidateForEditorFile: async () => ({
            path: resolvedPath,
            language: language,
            status: 'buildable',
        }),
        discover: async () => [{
            path: resolvedPath,
            language: language,
            status: 'buildable',
        }],
    } as unknown as AppHostDiscoveryService;
}

function createFailingAppHostDiscoveryService(): AppHostDiscoveryService {
    return {
        onDidChangeCandidates: () => ({ dispose: () => { } }),
        tryFindCandidateForEditorFile: async () => {
            throw new Error('discovery failed');
        },
        discover: async () => {
            throw new Error('discovery failed');
        },
    } as unknown as AppHostDiscoveryService;
}
