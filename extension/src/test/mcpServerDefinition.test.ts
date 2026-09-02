import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliPath from '../utils/cliPath';
import { AspireMcpServerDefinitionProvider, createAspireMcpServerDefinition } from '../mcp/AspireMcpServerDefinitionProvider';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

suite('AspireMcpServerDefinitionProvider definition tests', () => {
    test('wraps Windows command shims for VS Code MCP launchers', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const cliPath = 'C:\\Program Files\\a&b,c;d%NAME%\\aspire.cmd';
            const definition = createAspireMcpServerDefinition(cliPath);

            assert.strictEqual(definition.label, 'Aspire');
            assert.strictEqual(definition.command, process.env.ComSpec);
            assert.deepStrictEqual(definition.args, [
                '/d',
                '/v:off',
                '/c',
                'C:\\Program^ Files\\a^&b^,c^;d%NAME%\\aspire.cmd',
                'agent',
                'mcp',
            ]);
        }
        finally {
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('passes native executables through to the VS Code MCP launcher', () => {
        const cliPath = 'C:\\Program Files\\Aspire\\aspire.exe';
        const definition = createAspireMcpServerDefinition(cliPath, undefined, undefined, {
            isAbsolute: () => true,
            fileExists: candidate => candidate === cliPath,
            realpath: () => undefined,
        });

        assert.strictEqual(definition.command, cliPath);
        assert.deepStrictEqual(definition.args, ['agent', 'mcp']);
        assert.deepStrictEqual(definition.env, { AspireCliPath: cliPath });
    });

    // `aspire agent mcp` can build an AppHost, and that build inherits this environment. Forwarding
    // an unbundled framework-dependent CLI path makes ResolveAspireCliBundle stamp bundle assets
    // from a CLI that has no bundle layout, so the MCP server must apply the same forwardability
    // guard every other AspireCliPath producer applies.
    test('does not forward an unbundled framework-dependent CLI path to the MCP server', () => {
        // Build the paths with `path` rather than literals: the production guard derives the
        // adjacent assembly with path.dirname/path.join, which emits backslashes on Windows, so a
        // hardcoded POSIX literal would never match there and the test would silently pass.
        const cliPath = path.join(path.sep, 'repo', 'artifacts', 'bin', 'Aspire.Cli', 'Debug', 'aspire');
        const cliAssemblyPath = path.join(path.dirname(cliPath), 'aspire.dll');
        const definition = createAspireMcpServerDefinition(cliPath, undefined, undefined, {
            isAbsolute: () => true,
            // An inner-loop `dotnet build` output: the apphost sits next to aspire.dll with no
            // install sidecar and no adjacent bundle layout.
            fileExists: candidate => candidate === cliPath || candidate === cliAssemblyPath,
            realpath: () => undefined,
        });

        assert.strictEqual(definition.command, cliPath);
        assert.deepStrictEqual(definition.args, ['agent', 'mcp']);
        // VS Code normalizes an omitted env to an empty record, so asserting the whole value both
        // proves AspireCliPath is absent and pins that nothing else is forwarded in its place.
        assert.deepStrictEqual(definition.env, {});
    });
});

suite('AspireMcpServerDefinitionProvider refresh tests', () => {
    let configChangeHandler: ((event: vscode.ConfigurationChangeEvent) => void) | undefined;
    let configurationStub: sinon.SinonStub;
    let getConfigurationStub: sinon.SinonStub;
    let workspaceFoldersStub: sinon.SinonStub;
    let trustGrantHandler: (() => void) | undefined;
    let trustGrantStub: sinon.SinonStub;

    setup(() => {
        configurationStub = sinon.stub(vscode.workspace, 'onDidChangeConfiguration').callsFake(handler => {
            configChangeHandler = handler as (event: vscode.ConfigurationChangeEvent) => void;
            return { dispose: () => { } };
        });
        workspaceFoldersStub = sinon.stub(vscode.workspace, 'onDidChangeWorkspaceFolders').returns({ dispose: () => { } });
        trustGrantStub = sinon.stub(vscode.workspace, 'onDidGrantWorkspaceTrust').callsFake(handler => {
            trustGrantHandler = handler;
            return { dispose: () => { } };
        });
        const workspaceConfiguration: vscode.WorkspaceConfiguration = {
            get: sinon.stub().returns(true),
            has: sinon.stub().returns(true),
            inspect: sinon.stub().returns(undefined),
            update: sinon.stub().resolves(),
        };
        getConfigurationStub = sinon.stub(vscode.workspace, 'getConfiguration').returns(workspaceConfiguration);
    });

    teardown(() => {
        configurationStub.restore();
        getConfigurationStub.restore();
        trustGrantStub.restore();
        workspaceFoldersStub.restore();
    });

    test('refreshes when the configured CLI executable path changes', () => {
        const provider = new AspireMcpServerDefinitionProvider();
        const refresh = sinon.stub(provider, 'refresh').resolves();

        configChangeHandler!({
            affectsConfiguration: section => section === 'aspire.aspireCliExecutablePath',
        });

        assert.ok(refresh.calledOnce);
        provider.dispose();
    });

    test('refreshes when workspace trust is granted', () => {
        const provider = new AspireMcpServerDefinitionProvider();
        const refresh = sinon.stub(provider, 'refresh').resolves();

        trustGrantHandler!();

        assert.ok(refresh.calledOnce);
        provider.dispose();
    });

    test('does not provide MCP definitions in an untrusted workspace', async () => {
        const folder = { index: 0, name: 'app', uri: vscode.Uri.file('/repo/app') };
        const workspaceFoldersValueStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([folder]);
        const trustDescriptor = Object.getOwnPropertyDescriptor(vscode.workspace, 'isTrusted');
        Object.defineProperty(vscode.workspace, 'isTrusted', { value: false, configurable: true });
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const resolve = sinon.stub().resolves({ available: true, cliPath: '/repo/app/aspire', source: 'configured' });
        const resolver = { resolve, onDidChangeForwarding: forwardingEmitter.event } as unknown as cliPath.CliPathResolver;
        const provider = new AspireMcpServerDefinitionProvider(resolver);

        try {
            await provider.refresh();

            assert.deepStrictEqual(provider.provideMcpServerDefinitions(new vscode.CancellationTokenSource().token), []);
            assert.ok(resolve.notCalled);
        }
        finally {
            provider.dispose();
            forwardingEmitter.dispose();
            Object.defineProperty(vscode.workspace, 'isTrusted', trustDescriptor!);
            workspaceFoldersValueStub.restore();
        }
    });

    test('refreshes when CLI resolution rejects a configured path', async () => {
        cliPath.resetRejectedConfiguredCliPathForForwarding();
        const provider = new AspireMcpServerDefinitionProvider();
        const refresh = sinon.stub(provider, 'refresh').resolves();

        try {
            await cliPath.resolveCliPath({
                getConfiguredPath: () => '/invalid/aspire',
                getWorkspaceFolders: () => [],
                getDefaultPaths: () => [],
                isConfiguredPathAutoConfigured: () => false,
                findOnPath: async () => 'aspire',
                findAtDefaultPath: async () => undefined,
                tryExecute: async () => false,
                getExecutableCandidates: (candidate: string) => [candidate],
                setConfiguredPath: async () => { },
                updateResolvedPathForForwarding: () => { },
            });

            assert.ok(refresh.called, 'MCP definitions should refresh when another consumer rejects the configured CLI');
        }
        finally {
            provider.dispose();
            cliPath.resetRejectedConfiguredCliPathForForwarding();
        }
    });

    test('provides one folder-scoped MCP definition per workspace folder', async () => {
        const folderA = { index: 0, name: 'a', uri: vscode.Uri.file('/repo/a') };
        const folderB = { index: 1, name: 'b', uri: vscode.Uri.file('/repo/b') };
        const workspaceFoldersValueStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([folderA, folderB]);
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const resolver = {
            resolve: sinon.stub().callsFake(async (target: CliPathResolutionTarget) => ({
                available: true,
                cliPath: target.kind === 'workspaceFolder' && target.workspaceFolder.name === 'a'
                    ? '/repo/a/aspire'
                    : '/repo/b/aspire',
                source: 'configured',
            })),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as cliPath.CliPathResolver;
        const provider = new AspireMcpServerDefinitionProvider(resolver);
        const cancellationSource = new vscode.CancellationTokenSource();

        try {
            await provider.refresh();
            const definitions = await Promise.resolve(provider.provideMcpServerDefinitions(cancellationSource.token)) ?? [];

            assert.deepStrictEqual(definitions.map(definition => ({
                label: definition.label,
                command: definition.command,
                cwd: definition.cwd?.fsPath,
            })), [
                { label: 'Aspire (a)', command: '/repo/a/aspire', cwd: folderA.uri.fsPath },
                { label: 'Aspire (b)', command: '/repo/b/aspire', cwd: folderB.uri.fsPath },
            ]);
        }
        finally {
            cancellationSource.dispose();
            forwardingEmitter.dispose();
            provider.dispose();
            workspaceFoldersValueStub.restore();
        }
    });

    test('keeps the Aspire label for a single workspace folder', async () => {
        const folder = { index: 0, name: 'app', uri: vscode.Uri.file('/repo/app') };
        const workspaceFoldersValueStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([folder]);
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const resolver = {
            resolve: sinon.stub().resolves({
                available: true,
                cliPath: '/repo/app/aspire',
                source: 'configured',
            }),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as cliPath.CliPathResolver;
        const provider = new AspireMcpServerDefinitionProvider(resolver);
        const cancellationSource = new vscode.CancellationTokenSource();

        try {
            await provider.refresh();
            const definitions = await Promise.resolve(provider.provideMcpServerDefinitions(cancellationSource.token)) ?? [];

            assert.strictEqual(definitions.length, 1);
            assert.strictEqual(definitions[0].label, 'Aspire');
            assert.strictEqual(definitions[0].cwd?.fsPath, folder.uri.fsPath);
        }
        finally {
            cancellationSource.dispose();
            forwardingEmitter.dispose();
            provider.dispose();
            workspaceFoldersValueStub.restore();
        }
    });

    test('disambiguates MCP labels when workspace folders share a name', async () => {
        const folderA = { index: 0, name: 'api', uri: vscode.Uri.file('/repo/a/api') };
        const folderB = { index: 1, name: 'api', uri: vscode.Uri.file('/repo/b/api') };
        const workspaceFoldersValueStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([folderA, folderB]);
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const resolver = {
            resolve: sinon.stub().callsFake(async (target: CliPathResolutionTarget) => ({
                available: true,
                cliPath: target.kind === 'workspaceFolder' && target.workspaceFolder.index === 0
                    ? '/repo/a/aspire'
                    : '/repo/b/aspire',
                source: 'configured',
            })),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as cliPath.CliPathResolver;
        const provider = new AspireMcpServerDefinitionProvider(resolver);
        const cancellationSource = new vscode.CancellationTokenSource();

        try {
            await provider.refresh();
            const definitions = await Promise.resolve(provider.provideMcpServerDefinitions(cancellationSource.token)) ?? [];

            assert.deepStrictEqual(definitions.map(definition => definition.label), [
                'Aspire (api 1)',
                'Aspire (api 2)',
            ]);
        }
        finally {
            cancellationSource.dispose();
            forwardingEmitter.dispose();
            provider.dispose();
            workspaceFoldersValueStub.restore();
        }
    });

    test('keeps duplicate-folder labels stable when one CLI is unavailable', async () => {
        const folderA = { index: 0, name: 'api', uri: vscode.Uri.file('/repo/a/api') };
        const folderB = { index: 1, name: 'api', uri: vscode.Uri.file('/repo/b/api') };
        const workspaceFoldersValueStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([folderA, folderB]);
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const resolver = {
            resolve: sinon.stub().callsFake(async (target: CliPathResolutionTarget) => ({
                available: target.kind === 'workspaceFolder' && target.workspaceFolder.index === 1,
                cliPath: '/repo/b/aspire',
                source: 'configured',
            })),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as cliPath.CliPathResolver;
        const provider = new AspireMcpServerDefinitionProvider(resolver);

        try {
            await provider.refresh();
            const definitions = await Promise.resolve(
                provider.provideMcpServerDefinitions(new vscode.CancellationTokenSource().token)) ?? [];

            assert.deepStrictEqual(definitions.map(definition => definition.label), ['Aspire (api 2)']);
        }
        finally {
            forwardingEmitter.dispose();
            provider.dispose();
            workspaceFoldersValueStub.restore();
        }
    });

    test('does not collide a generated MCP label with a real folder name', async () => {
        const folderA = { index: 0, name: 'api', uri: vscode.Uri.file('/repo/a/api') };
        const folderB = { index: 1, name: 'api', uri: vscode.Uri.file('/repo/b/api') };
        const folderC = { index: 2, name: 'api 1', uri: vscode.Uri.file('/repo/c/api') };
        const workspaceFoldersValueStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([folderA, folderB, folderC]);
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const resolver = {
            resolve: sinon.stub().callsFake(async (target: CliPathResolutionTarget) => ({
                available: true,
                cliPath: `/repo/${target.kind === 'workspaceFolder' ? target.workspaceFolder.index : 'window'}/aspire`,
                source: 'configured',
            })),
            onDidChangeForwarding: forwardingEmitter.event,
        } as unknown as cliPath.CliPathResolver;
        const provider = new AspireMcpServerDefinitionProvider(resolver);
        const cancellationSource = new vscode.CancellationTokenSource();

        try {
            await provider.refresh();
            const definitions = await Promise.resolve(provider.provideMcpServerDefinitions(cancellationSource.token)) ?? [];

            assert.deepStrictEqual(definitions.map(definition => definition.label), [
                'Aspire (api 2)',
                'Aspire (api 3)',
                'Aspire (api 1)',
            ]);
        }
        finally {
            cancellationSource.dispose();
            forwardingEmitter.dispose();
            provider.dispose();
            workspaceFoldersValueStub.restore();
        }
    });

    test('ignores an older refresh that completes after a newer result', async () => {
        const workspaceFoldersValueStub = sinon.stub(vscode.workspace, 'workspaceFolders').value([{
            index: 0,
            name: 'test',
            uri: vscode.Uri.file('/workspace'),
        }]);
        let completeOlderRefresh: ((result: cliPath.CliPathResolutionResult) => void) | undefined;
        const olderResult = new Promise<cliPath.CliPathResolutionResult>(resolve => completeOlderRefresh = resolve);
        const forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
        const resolve = sinon.stub();
        resolve.onFirstCall().returns(olderResult);
        resolve.onSecondCall().resolves({
            available: false,
            cliPath: 'aspire',
            source: 'not-found',
        });
        const resolver = { resolve, onDidChangeForwarding: forwardingEmitter.event } as unknown as cliPath.CliPathResolver;
        const provider = new AspireMcpServerDefinitionProvider(resolver);
        const cancellationSource = new vscode.CancellationTokenSource();

        try {
            const olderRefresh = provider.refresh();
            await provider.refresh();

            completeOlderRefresh!({
                available: true,
                cliPath: '/old/aspire',
                source: 'configured',
            });
            await olderRefresh;

            assert.deepStrictEqual(
                provider.provideMcpServerDefinitions(cancellationSource.token),
                [],
                'an older refresh must not restore a stale CLI path');
        }
        finally {
            cancellationSource.dispose();
            forwardingEmitter.dispose();
            provider.dispose();
            workspaceFoldersValueStub.restore();
        }
    });
});
