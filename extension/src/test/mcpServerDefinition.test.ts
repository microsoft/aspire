import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as cliPath from '../utils/cliPath';
import {
    AspireMcpServerDefinitionProvider,
    canonicalizeMcpAppHostPath,
    createAspireMcpServerDefinition,
} from '../mcp/AspireMcpServerDefinitionProvider';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';
import { agentMcpCapability, CapabilityStatus } from '../types/configInfo';
import { CandidateAppHostDisplayInfo } from '../utils/appHostCandidateTypes';
import { ConfigInfoOptions } from '../utils/configInfoProvider';

suite('AspireMcpServerDefinitionProvider definition tests', () => {
    test('wraps Windows command shims for VS Code MCP launchers', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const cliPath = 'C:\\Program Files\\a&b,c;d%NAME%\\aspire.cmd';
            const definition = createAspireMcpServerDefinition(cliPath, 'C:\\repo\\a&b\\AppHost.csproj');

            assert.strictEqual(definition.label, 'Aspire');
            assert.strictEqual(definition.command, process.env.ComSpec);
            assert.deepStrictEqual(definition.args, [
                '/d',
                '/v:off',
                '/c',
                'C:\\Program^ Files\\a^&b^,c^;d%NAME%\\aspire.cmd',
                'agent',
                'mcp',
                '--apphost',
                'C:\\repo\\a^&b\\AppHost.csproj',
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

    // A `dotnet tool install --global` shim sits at `%USERPROFILE%\.dotnet\tools\aspire.cmd` and
    // AppHost projects routinely live under a spaced path such as `C:\Users\a b\source\My App`.
    // VS Code double-quotes any token containing a space before handing it to `cmd.exe`, which is
    // the only construction a batch shim's `%1` split preserves, so the wrapper must stand aside.
    test('launches a Windows command shim directly when VS Code can quote every token', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const cliPath = 'C:\\Users\\a b\\.dotnet\\tools\\aspire.cmd';
            const appHostPath = 'C:\\Users\\a b\\source\\My App\\AppHost.csproj';
            const definition = createAspireMcpServerDefinition(cliPath, appHostPath, {
                deps: {
                    isAbsolute: () => true,
                    fileExists: candidate => candidate === cliPath,
                    realpath: () => undefined,
                },
            });

            assert.strictEqual(definition.command, cliPath);
            assert.deepStrictEqual(definition.args, ['agent', 'mcp', '--apphost', appHostPath]);
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

    test('pins the AppHost project path into the MCP launch arguments', () => {
        const cliPath = 'C:\\Program Files\\Aspire\\aspire.exe';
        const appHostPath = 'C:\\repo\\AppHost\\AppHost.csproj';
        const definition = createAspireMcpServerDefinition(cliPath, appHostPath, {
            deps: {
                isAbsolute: () => true,
                fileExists: candidate => candidate === cliPath,
                realpath: () => undefined,
            },
        });

        assert.strictEqual(definition.command, cliPath);
        assert.deepStrictEqual(definition.args, ['agent', 'mcp', '--apphost', appHostPath]);
        assert.deepStrictEqual(definition.env, { AspireCliPath: cliPath });
    });

    test('bounds AppHost canonicalization', async () => {
        const realpath = sinon.stub(fs.promises, 'realpath').returns(new Promise(() => { }));

        try {
            await assert.rejects(
                canonicalizeMcpAppHostPath('/repo/AppHost.csproj', 1),
                /AppHost canonicalization did not complete within 1ms/);
        }
        finally {
            realpath.restore();
        }
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
        const appHostPath = path.join(path.sep, 'repo', 'AppHost', 'AppHost.csproj');
        const definition = createAspireMcpServerDefinition(cliPath, appHostPath, {
            deps: {
                isAbsolute: () => true,
                // An inner-loop `dotnet build` output: the apphost sits next to aspire.dll with no
                // install sidecar and no adjacent bundle layout.
                fileExists: candidate => candidate === cliPath || candidate === cliAssemblyPath,
                realpath: () => undefined,
            },
        });

        assert.strictEqual(definition.command, cliPath);
        assert.deepStrictEqual(definition.args, ['agent', 'mcp', '--apphost', appHostPath]);
        // VS Code normalizes an omitted env to an empty record, so asserting the whole value both
        // proves AspireCliPath is absent and pins that nothing else is forwarded in its place.
        assert.deepStrictEqual(definition.env, {});
    });
});

interface ConfigurationInspection {
    globalValue?: boolean;
    workspaceValue?: boolean;
    workspaceFolderValue?: boolean;
}

interface HarnessOptions {
    folders: vscode.WorkspaceFolder[];
    trusted?: boolean;
    inspectFor?: (scope: vscode.ConfigurationScope | undefined) => ConfigurationInspection | undefined;
    cliPathFor?: (folder: vscode.WorkspaceFolder) => string | undefined;
    capabilityFor?: (folder: vscode.WorkspaceFolder) => CapabilityStatus;
    candidatesFor?: (folder: vscode.WorkspaceFolder) => Promise<CandidateAppHostDisplayInfo[]>;
    canonicalPathFor?: (appHostPath: string) => string;
    /** Makes CLI resolution reject for a folder, standing in for a spawn or filesystem failure. */
    resolveErrorFor?: (folder: vscode.WorkspaceFolder) => Error | undefined;
    /** Makes the capability probe reject for a folder, standing in for a CLI probe failure. */
    capabilityErrorFor?: (folder: vscode.WorkspaceFolder) => Error | undefined;
}

class ProviderHarness {
    readonly provider: AspireMcpServerDefinitionProvider;
    readonly candidateChangeEmitter = new vscode.EventEmitter<vscode.WorkspaceFolder>();
    readonly forwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
    readonly resolveStub: sinon.SinonStub;
    readonly capabilityStub: sinon.SinonStub;
    readonly discoverStub: sinon.SinonStub;
    configChangeHandler: ((event: vscode.ConfigurationChangeEvent) => void) | undefined;
    workspaceFolderChangeHandler: (() => void) | undefined;
    trustGrantHandler: (() => void) | undefined;

    private readonly _restores: (() => void)[] = [];
    private readonly _trustDescriptor: PropertyDescriptor | undefined;

    constructor(options: HarnessOptions) {
        const folders = options.folders;
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').value(folders);
        this._restores.push(() => workspaceFoldersStub.restore());
        this._trustDescriptor = Object.getOwnPropertyDescriptor(vscode.workspace, 'isTrusted');
        Object.defineProperty(vscode.workspace, 'isTrusted', { value: options.trusted ?? true, configurable: true });
        this._restores.push(stubRestore(sinon.stub(vscode.workspace, 'onDidChangeConfiguration').callsFake(handler => {
            this.configChangeHandler = handler as (event: vscode.ConfigurationChangeEvent) => void;
            return { dispose: () => { } };
        })));
        this._restores.push(stubRestore(sinon.stub(vscode.workspace, 'onDidChangeWorkspaceFolders').callsFake(handler => {
            this.workspaceFolderChangeHandler = handler as () => void;
            return { dispose: () => { } };
        })));
        this._restores.push(stubRestore(sinon.stub(vscode.workspace, 'onDidGrantWorkspaceTrust').callsFake(handler => {
            this.trustGrantHandler = handler as () => void;
            return { dispose: () => { } };
        })));
        this._restores.push(stubRestore(sinon.stub(vscode.workspace, 'getConfiguration').callsFake((_section, scope) => ({
            get: sinon.stub().returns(undefined),
            has: sinon.stub().returns(true),
            inspect: sinon.stub().callsFake(() => options.inspectFor?.(scope as vscode.ConfigurationScope | undefined)),
            update: sinon.stub().resolves(),
        } as unknown as vscode.WorkspaceConfiguration))));

        this.resolveStub = sinon.stub().callsFake(async (target: CliPathResolutionTarget) => {
            const folder = target.kind === 'workspaceFolder' ? target.workspaceFolder : folders[0];
            const resolveError = options.resolveErrorFor?.(folder);
            if (resolveError) {
                throw resolveError;
            }

            const resolved = options.cliPathFor === undefined
                ? path.join(folder.uri.fsPath, 'aspire')
                : options.cliPathFor(folder);
            return resolved === undefined
                ? { available: false, cliPath: 'aspire', source: 'not-found' }
                : { available: true, cliPath: resolved, source: 'configured' };
        });
        this.capabilityStub = sinon.stub().callsFake(async (_capability: string, probeOptions?: ConfigInfoOptions) => {
            const target = probeOptions?.target;
            const folder = target?.kind === 'workspaceFolder' ? target.workspaceFolder : folders[0];
            const capabilityError = options.capabilityErrorFor?.(folder);
            if (capabilityError) {
                throw capabilityError;
            }

            return options.capabilityFor?.(folder) ?? 'supported';
        });
        this.discoverStub = sinon.stub().callsFake(async (folder: vscode.WorkspaceFolder) =>
            await (options.candidatesFor?.(folder) ?? Promise.resolve([])));
        this._restores.push(stubRestore(sinon.stub(fs.promises, 'realpath').callsFake(async appHostPath =>
            options.canonicalPathFor?.(appHostPath.toString()) ?? appHostPath.toString())));

        this.provider = new AspireMcpServerDefinitionProvider({
            appHostDiscovery: {
                discover: this.discoverStub,
                onDidChangeCandidates: this.candidateChangeEmitter.event,
            },
            capabilityProbe: { getCapabilityStatus: this.capabilityStub },
        }, { resolve: this.resolveStub, onDidChangeForwarding: this.forwardingEmitter.event } as unknown as cliPath.CliPathResolver);
    }

    definitions(): vscode.McpStdioServerDefinition[] {
        return (this.provider.provideMcpServerDefinitions(new vscode.CancellationTokenSource().token) ?? []) as vscode.McpStdioServerDefinition[];
    }

    /**
     * Makes the workspace-trust read throw so a refresh fails outside any per-folder gate. This is
     * the only way to reach the terminal handler without reaching into the provider's internals.
     */
    breakWorkspaceTrust(): void {
        Object.defineProperty(vscode.workspace, 'isTrusted', {
            get: () => {
                throw new Error('workspace trust unavailable');
            },
            configurable: true,
        });
    }

    dispose(): void {
        this.provider.dispose();
        this.candidateChangeEmitter.dispose();
        this.forwardingEmitter.dispose();
        if (this._trustDescriptor) {
            Object.defineProperty(vscode.workspace, 'isTrusted', this._trustDescriptor);
        }
        for (const restore of this._restores.reverse()) {
            restore();
        }
    }
}

function stubRestore(stub: { restore: () => void }): () => void {
    return () => stub.restore();
}

function workspaceFolder(name: string, folderPath: string, index: number): vscode.WorkspaceFolder {
    return { index, name, uri: vscode.Uri.file(folderPath) };
}

function appHostCandidate(folder: vscode.WorkspaceFolder, ...segments: string[]): CandidateAppHostDisplayInfo {
    return { path: path.join(folder.uri.fsPath, ...segments), language: 'csharp', status: 'buildable' };
}

suite('AspireMcpServerDefinitionProvider pinned registration tests', () => {
    teardown(() => {
        sinon.restore();
    });

    test('registers one pinned definition per discovered AppHost', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const first = appHostCandidate(folder, 'src', 'AppHost', 'AppHost.csproj');
        const second = appHostCandidate(folder, 'other', 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [second, first],
        });

        try {
            await harness.provider.refresh();
            const definitions = harness.definitions();

            assert.deepStrictEqual(definitions.map(definition => ({
                label: definition.label,
                command: definition.command,
                args: definition.args,
                cwd: definition.cwd?.fsPath,
            })), [
                {
                    label: 'Aspire (app: other/AppHost.csproj)',
                    command: path.join(folder.uri.fsPath, 'aspire'),
                    args: ['agent', 'mcp', '--apphost', path.resolve(second.path)],
                    cwd: folder.uri.fsPath,
                },
                {
                    label: 'Aspire (app: src/AppHost/AppHost.csproj)',
                    command: path.join(folder.uri.fsPath, 'aspire'),
                    args: ['agent', 'mcp', '--apphost', path.resolve(first.path)],
                    cwd: folder.uri.fsPath,
                },
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('excludes AppHost candidates that are not buildable', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const buildable = appHostCandidate(folder, 'AppHost', 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [
                buildable,
                { path: path.join(folder.uri.fsPath, 'broken', 'AppHost.csproj'), language: 'csharp', status: 'unsupported' },
            ],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(buildable.path)],
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('registers per-folder definitions with folder-qualified labels in a multi-root workspace', async () => {
        const folderA = workspaceFolder('a', '/repo/a', 0);
        const folderB = workspaceFolder('b', '/repo/b', 1);
        const candidateA = appHostCandidate(folderA, 'AppHost.csproj');
        const candidateB = appHostCandidate(folderB, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folderA, folderB],
            cliPathFor: folder => path.join(folder.uri.fsPath, 'aspire'),
            candidatesFor: async folder => folder.index === 0 ? [candidateA] : [candidateB],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => ({
                label: definition.label,
                command: definition.command,
                args: definition.args,
                cwd: definition.cwd?.fsPath,
            })), [
                {
                    label: 'Aspire (a: AppHost.csproj)',
                    command: path.join(folderA.uri.fsPath, 'aspire'),
                    args: ['agent', 'mcp', '--apphost', path.resolve(candidateA.path)],
                    cwd: folderA.uri.fsPath,
                },
                {
                    label: 'Aspire (b: AppHost.csproj)',
                    command: path.join(folderB.uri.fsPath, 'aspire'),
                    args: ['agent', 'mcp', '--apphost', path.resolve(candidateB.path)],
                    cwd: folderB.uri.fsPath,
                },
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    // VS Code identifies an MCP server by its label, so a label that depends on how many folders
    // are open would rename - and therefore restart - a server when an unrelated folder is added.
    test('keeps the pinned label and definition stable when another workspace folder is added', async () => {
        const folderA = workspaceFolder('a', '/repo/a', 0);
        const folderB = workspaceFolder('b', '/repo/b', 1);
        const candidateA = appHostCandidate(folderA, 'AppHost.csproj');
        const candidateB = appHostCandidate(folderB, 'AppHost.csproj');
        const folders = [folderA];
        const harness = new ProviderHarness({
            folders,
            cliPathFor: folder => path.join(folder.uri.fsPath, 'aspire'),
            candidatesFor: async folder => folder.index === 0 ? [candidateA] : [candidateB],
        });

        try {
            await harness.provider.refresh();
            const [initialDefinition] = harness.definitions();
            assert.strictEqual(initialDefinition.label, 'Aspire (a: AppHost.csproj)');

            folders.push(folderB);
            await harness.provider.refresh();

            const definitions = harness.definitions();
            assert.strictEqual(definitions[0], initialDefinition, 'adding a folder must not recreate an existing pinned definition');
            assert.deepStrictEqual(definitions.map(definition => definition.label), [
                'Aspire (a: AppHost.csproj)',
                'Aspire (b: AppHost.csproj)',
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    // Nested multi-root folders each discover the AppHost that lives under both of them. Two
    // definitions would mean two MCP servers building and serving the same project.
    test('registers one definition for an AppHost discovered by nested workspace folders', async () => {
        const outerFolder = workspaceFolder('repo', '/repo', 0);
        const innerFolder = workspaceFolder('app', '/repo/app', 1);
        const appHostPath = path.join(innerFolder.uri.fsPath, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [outerFolder, innerFolder],
            cliPathFor: () => '/repo/aspire',
            candidatesFor: async () => [{ path: appHostPath, language: 'csharp', status: 'buildable' }],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => ({
                label: definition.label,
                args: definition.args,
                cwd: definition.cwd?.fsPath,
            })), [
                {
                    label: 'Aspire (repo: app/AppHost.csproj)',
                    args: ['agent', 'mcp', '--apphost', path.resolve(appHostPath)],
                    cwd: outerFolder.uri.fsPath,
                },
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('keeps a child path beginning with two dots workspace-relative in its label', async () => {
        const folder = workspaceFolder('repo', '/repo', 0);
        const appHostPath = path.join(folder.uri.fsPath, '..app', 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [{ path: appHostPath, language: 'csharp', status: 'buildable' }],
        });

        try {
            await harness.provider.refresh();

            assert.strictEqual(
                harness.definitions()[0].label,
                'Aspire (repo: ..app/AppHost.csproj)');
        }
        finally {
            harness.dispose();
        }
    });

    // Windows paths are case-insensitive, so two candidates differing only in case name one file.
    test('deduplicates case-variant AppHost paths on Windows', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [
                { path: path.join(folder.uri.fsPath, 'AppHost', 'AppHost.csproj'), language: 'csharp', status: 'buildable' },
                { path: path.join(folder.uri.fsPath, 'apphost', 'apphost.csproj'), language: 'csharp', status: 'buildable' },
            ],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(folder.uri.fsPath, 'AppHost', 'AppHost.csproj')],
            ]);
        }
        finally {
            harness.dispose();
            platformStub.restore();
        }
    });

    // A pinned MCP server outlives the refresh that created it and is launched from whatever
    // working directory VS Code chooses, so a relative candidate must be anchored before it is
    // baked into the arguments.
    test('resolves relative AppHost candidate paths against the owning folder', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [{ path: path.join('AppHost', 'AppHost.csproj'), language: 'csharp', status: 'buildable' }],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => ({
                label: definition.label,
                args: definition.args,
            })), [
                {
                    label: 'Aspire (app: AppHost/AppHost.csproj)',
                    args: ['agent', 'mcp', '--apphost', path.resolve(folder.uri.fsPath, 'AppHost', 'AppHost.csproj')],
                },
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('keeps an existing label stable when a same-named folder introduces a collision', async () => {
        const firstFolder = workspaceFolder('repo', '/checkout/one/repo', 0);
        const secondFolder = workspaceFolder('repo', '/checkout/two/repo', 1);
        const folders = [firstFolder];
        const firstCandidate = appHostCandidate(firstFolder, 'AppHost.csproj');
        const secondCandidate = appHostCandidate(secondFolder, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders,
            cliPathFor: folder => path.join(folder.uri.fsPath, 'aspire'),
            candidatesFor: async folder => folder.index === 0 ? [firstCandidate] : [secondCandidate],
        });

        try {
            await harness.provider.refresh();
            const initialDefinition = harness.definitions()[0];
            assert.strictEqual(initialDefinition.label, 'Aspire (repo: AppHost.csproj)');

            folders.push(secondFolder);
            await harness.provider.refresh();
            const collidingDefinitions = harness.definitions();

            assert.strictEqual(collidingDefinitions.length, 2);
            assert.strictEqual(collidingDefinitions[0], initialDefinition);
            assert.strictEqual(collidingDefinitions[0].label, 'Aspire (repo: AppHost.csproj)');
            assert.match(collidingDefinitions[1].label, /^Aspire \(repo: AppHost\.csproj\) \[[a-z0-9]+\]$/);
            assert.deepStrictEqual(collidingDefinitions.map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(firstCandidate.path)],
                ['agent', 'mcp', '--apphost', path.resolve(secondCandidate.path)],
            ]);

            folders.pop();
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), [initialDefinition]);
        }
        finally {
            harness.dispose();
        }
    });

    test('deduplicates selector aliases by canonical AppHost identity', async () => {
        const firstFolder = workspaceFolder('repo', '/checkout/repo', 0);
        const secondFolder = workspaceFolder('linked', '/checkout/linked', 1);
        const canonicalPath = path.resolve(firstFolder.uri.fsPath, 'AppHost.csproj');
        const firstCandidate = appHostCandidate(firstFolder, 'AppHost.csproj');
        const linkedCandidate = appHostCandidate(secondFolder, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [firstFolder, secondFolder],
            candidatesFor: async folder => folder.index === 0 ? [firstCandidate] : [linkedCandidate],
            canonicalPathFor: () => canonicalPath,
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => ({
                label: definition.label,
                args: definition.args,
            })), [{
                label: 'Aspire (repo: AppHost.csproj)',
                args: ['agent', 'mcp', '--apphost', canonicalPath],
            }]);
        }
        finally {
            harness.dispose();
        }
    });

    test('rejects AppHost candidates whose canonical path escapes the workspace root', async () => {
        const folder = workspaceFolder('repo', '/checkout/repo', 0);
        const linkedCandidate = appHostCandidate(folder, 'linked/AppHost.csproj');
        const safeCandidate = appHostCandidate(folder, 'safe/AppHost.csproj');
        const canonicalWorkspaceRoot = path.resolve('/canonical/repo');
        const canonicalSafePath = path.join(canonicalWorkspaceRoot, 'safe/AppHost.csproj');
        const canonicalExternalPath = path.resolve('/canonical/external/AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [linkedCandidate, safeCandidate],
            canonicalPathFor: appHostPath => {
                if (appHostPath === path.resolve(folder.uri.fsPath)) {
                    return canonicalWorkspaceRoot;
                }

                return appHostPath === path.resolve(linkedCandidate.path)
                    ? canonicalExternalPath
                    : canonicalSafePath;
            },
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => ({
                label: definition.label,
                args: definition.args,
            })), [{
                label: 'Aspire (repo: safe/AppHost.csproj)',
                args: ['agent', 'mcp', '--apphost', canonicalSafePath],
            }]);
        }
        finally {
            harness.dispose();
        }
    });

    test('accepts a physical AppHost candidate under an aliased workspace root', async () => {
        const folder = workspaceFolder('repo', '/checkout/repo-alias', 0);
        const canonicalWorkspaceRoot = path.resolve('/canonical/repo');
        const canonicalAppHostPath = path.join(canonicalWorkspaceRoot, 'AppHost.csproj');
        const candidate: CandidateAppHostDisplayInfo = {
            path: canonicalAppHostPath,
            language: 'csharp',
            status: 'buildable',
        };
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [candidate],
            canonicalPathFor: appHostPath =>
                appHostPath === path.resolve(folder.uri.fsPath)
                    ? canonicalWorkspaceRoot
                    : canonicalAppHostPath,
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => ({
                label: definition.label,
                args: definition.args,
            })), [{
                label: 'Aspire (repo: AppHost.csproj)',
                args: ['agent', 'mcp', '--apphost', canonicalAppHostPath],
            }]);
        }
        finally {
            harness.dispose();
        }
    });

    test('does not publish a refresh that was in flight when the provider was disposed', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        let startDiscovery: (() => void) | undefined;
        let completeDiscovery: ((candidates: CandidateAppHostDisplayInfo[]) => void) | undefined;
        // Wait on discovery actually starting rather than on elapsed time: the refresh reaches it
        // only after the CLI and capability gates resolve.
        const discoveryStarted = new Promise<void>(resolve => startDiscovery = resolve);
        const discoveryResult = new Promise<CandidateAppHostDisplayInfo[]>(resolve => completeDiscovery = resolve);
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: () => {
                startDiscovery!();
                return discoveryResult;
            },
        });
        let changeCount = 0;
        const subscription = harness.provider.onDidChangeMcpServerDefinitions(() => changeCount++);

        try {
            const pendingRefresh = harness.provider.refresh();
            await discoveryStarted;
            harness.provider.dispose();
            completeDiscovery!([candidate]);
            await pendingRefresh;

            assert.deepStrictEqual(harness.definitions(), []);
            assert.strictEqual(changeCount, 0, 'a disposed provider must not publish or announce definitions');
        }
        finally {
            subscription.dispose();
            harness.dispose();
        }
    });

    test('does not register definitions when the CLI omits the agent MCP capability', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            capabilityFor: () => 'unsupported',
            candidatesFor: async () => [appHostCandidate(folder, 'AppHost.csproj')],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
            assert.ok(harness.discoverStub.notCalled, 'discovery must not run for a CLI without the capability');
            assert.strictEqual(harness.capabilityStub.firstCall.args[0], agentMcpCapability);
        }
        finally {
            harness.dispose();
        }
    });

    test('does not register definitions when the capability probe cannot complete', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            capabilityFor: () => 'unavailable',
            candidatesFor: async () => [appHostCandidate(folder, 'AppHost.csproj')],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
        }
        finally {
            harness.dispose();
        }
    });

    test('does not register definitions when AppHost discovery fails', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => {
                throw new Error('discovery failed');
            },
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
        }
        finally {
            harness.dispose();
        }
    });

    // A rejecting gate must not propagate out of `Promise.all` into an unhandled rejection that
    // leaves the previously published servers running against a CLI that can no longer be probed.
    test('drops stale definitions when CLI resolution rejects', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        let resolveError: Error | undefined;
        const harness = new ProviderHarness({
            folders: [folder],
            resolveErrorFor: () => resolveError,
            candidatesFor: async () => [candidate],
        });

        try {
            await harness.provider.refresh();
            assert.strictEqual(harness.definitions().length, 1);

            resolveError = new Error('cli resolution failed');
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), [], 'a failed gate must fail closed rather than keep stale servers');
        }
        finally {
            harness.dispose();
        }
    });

    test('drops stale definitions when the capability probe rejects', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        let capabilityError: Error | undefined;
        const harness = new ProviderHarness({
            folders: [folder],
            capabilityErrorFor: () => capabilityError,
            candidatesFor: async () => [candidate],
        });

        try {
            await harness.provider.refresh();
            assert.strictEqual(harness.definitions().length, 1);

            capabilityError = new Error('config info failed');
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
        }
        finally {
            harness.dispose();
        }
    });

    test('keeps registering healthy folders when another folder fails', async () => {
        const folderA = workspaceFolder('a', '/repo/a', 0);
        const folderB = workspaceFolder('b', '/repo/b', 1);
        const candidateA = appHostCandidate(folderA, 'AppHost.csproj');
        const candidateB = appHostCandidate(folderB, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folderA, folderB],
            cliPathFor: folder => path.join(folder.uri.fsPath, 'aspire'),
            capabilityErrorFor: folder => folder.index === 0 ? new Error('config info failed') : undefined,
            candidatesFor: async folder => folder.index === 0 ? [candidateA] : [candidateB],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(candidateB.path)],
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('publishes an empty set when a refresh fails outside per-folder work', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => [candidate],
        });

        try {
            await harness.provider.refresh();
            assert.strictEqual(harness.definitions().length, 1);

            harness.breakWorkspaceTrust();
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
        }
        finally {
            harness.dispose();
        }
    });

    // A pinned path that cannot be turned into a Windows shim launch must not take the whole
    // refresh - and therefore every other AppHost's registration - down with it.
    test('skips only the AppHost whose Windows shim launch cannot be constructed', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';
        const folder = workspaceFolder('app', '/repo/app', 0);
        // cmd.exe expands `%NAME%` before a batch shim sees its arguments and quoting cannot make
        // it literal, so this pin has no safe launch while the sibling pin does.
        const rejected = appHostCandidate(folder, '%NAME%', 'AppHost.csproj');
        const launchable = appHostCandidate(folder, 'ok', 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            cliPathFor: () => 'C:\\tools\\aspire.cmd',
            candidatesFor: async () => [rejected, launchable],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(launchable.path)],
            ]);
        }
        finally {
            harness.dispose();
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('does not register definitions when the CLI is unavailable', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            cliPathFor: () => undefined,
            candidatesFor: async () => [appHostCandidate(folder, 'AppHost.csproj')],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
            assert.ok(harness.capabilityStub.notCalled);
        }
        finally {
            harness.dispose();
        }
    });

    test('does not register definitions in an untrusted workspace', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            trusted: false,
            candidatesFor: async () => [appHostCandidate(folder, 'AppHost.csproj')],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
            assert.ok(harness.resolveStub.notCalled);
        }
        finally {
            harness.dispose();
        }
    });

    test('registers definitions when the registration setting has no explicit value', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            inspectFor: () => ({}),
            candidatesFor: async () => [candidate],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(candidate.path)],
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('registers definitions when the registration setting is explicitly enabled', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folder],
            inspectFor: () => ({ globalValue: true }),
            candidatesFor: async () => [candidate],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(candidate.path)],
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('does not register definitions when the registration setting is explicitly disabled', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            inspectFor: () => ({ globalValue: false }),
            candidatesFor: async () => [appHostCandidate(folder, 'AppHost.csproj')],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
            assert.ok(harness.resolveStub.notCalled);
        }
        finally {
            harness.dispose();
        }
    });

    test('prefers a folder opt-out over an enabling workspace value', async () => {
        const folderA = workspaceFolder('a', '/repo/a', 0);
        const folderB = workspaceFolder('b', '/repo/b', 1);
        const candidateB = appHostCandidate(folderB, 'AppHost.csproj');
        const harness = new ProviderHarness({
            folders: [folderA, folderB],
            inspectFor: scope => scope?.toString() === folderA.uri.toString()
                ? { workspaceFolderValue: false, workspaceValue: true, globalValue: true }
                : { workspaceValue: true, globalValue: true },
            candidatesFor: async folder => folder.index === 0
                ? [appHostCandidate(folderA, 'AppHost.csproj')]
                : [candidateB],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions().map(definition => ({
                label: definition.label,
                args: definition.args,
            })), [
                { label: 'Aspire (b: AppHost.csproj)', args: ['agent', 'mcp', '--apphost', path.resolve(candidateB.path)] },
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('prefers a workspace opt-out over an enabling global value', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({
            folders: [folder],
            inspectFor: () => ({ workspaceValue: false, globalValue: true }),
            candidatesFor: async () => [appHostCandidate(folder, 'AppHost.csproj')],
        });

        try {
            await harness.provider.refresh();

            assert.deepStrictEqual(harness.definitions(), []);
        }
        finally {
            harness.dispose();
        }
    });

    test('keeps pinned definitions stable when discovery adds and removes AppHosts', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const kept = appHostCandidate(folder, 'kept', 'AppHost.csproj');
        const removed = appHostCandidate(folder, 'removed', 'AppHost.csproj');
        const added = appHostCandidate(folder, 'zadded', 'AppHost.csproj');
        let candidates = [kept, removed];
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => candidates,
        });

        try {
            await harness.provider.refresh();
            const initialDefinitions = harness.definitions();
            assert.strictEqual(initialDefinitions.length, 2);
            const keptDefinition = initialDefinitions[0];

            candidates = [kept, added];
            await harness.provider.refresh();
            const updatedDefinitions = harness.definitions();

            assert.strictEqual(updatedDefinitions[0], keptDefinition, 'a surviving AppHost must keep its pinned definition');
            assert.deepStrictEqual(updatedDefinitions.map(definition => definition.args), [
                ['agent', 'mcp', '--apphost', path.resolve(kept.path)],
                ['agent', 'mcp', '--apphost', path.resolve(added.path)],
            ]);
        }
        finally {
            harness.dispose();
        }
    });

    test('fires a change event only when the pinned definition set changes', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        let candidates = [candidate];
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: async () => candidates,
        });
        let changeCount = 0;
        const subscription = harness.provider.onDidChangeMcpServerDefinitions(() => changeCount++);

        try {
            await harness.provider.refresh();
            assert.strictEqual(changeCount, 1);

            await harness.provider.refresh();
            assert.strictEqual(changeCount, 1, 'an unchanged refresh must not restart MCP servers');

            candidates = [];
            await harness.provider.refresh();
            assert.strictEqual(changeCount, 2);
        }
        finally {
            subscription.dispose();
            harness.dispose();
        }
    });

    test('refreshes when AppHost discovery candidates change', () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const harness = new ProviderHarness({ folders: [folder] });
        const refresh = sinon.stub(harness.provider, 'refresh').resolves();

        try {
            harness.candidateChangeEmitter.fire(folder);

            assert.ok(refresh.calledOnce);
        }
        finally {
            harness.dispose();
        }
    });

    test('refreshes when the registration setting changes', () => {
        const harness = new ProviderHarness({ folders: [workspaceFolder('app', '/repo/app', 0)] });
        const refresh = sinon.stub(harness.provider, 'refresh').resolves();

        try {
            harness.configChangeHandler!({
                affectsConfiguration: section => section === 'aspire.registerMcpServerInWorkspace',
            });

            assert.ok(refresh.calledOnce);
        }
        finally {
            harness.dispose();
        }
    });

    test('refreshes when the configured CLI executable path changes', () => {
        const harness = new ProviderHarness({ folders: [workspaceFolder('app', '/repo/app', 0)] });
        const refresh = sinon.stub(harness.provider, 'refresh').resolves();

        try {
            harness.configChangeHandler!({
                affectsConfiguration: section => section === 'aspire.aspireCliExecutablePath',
            });

            assert.ok(refresh.calledOnce);
        }
        finally {
            harness.dispose();
        }
    });

    test('refreshes when workspace folders change', () => {
        const harness = new ProviderHarness({ folders: [workspaceFolder('app', '/repo/app', 0)] });
        const refresh = sinon.stub(harness.provider, 'refresh').resolves();

        try {
            harness.workspaceFolderChangeHandler!();

            assert.ok(refresh.calledOnce);
        }
        finally {
            harness.dispose();
        }
    });

    test('refreshes when workspace trust is granted', () => {
        const harness = new ProviderHarness({ folders: [workspaceFolder('app', '/repo/app', 0)] });
        const refresh = sinon.stub(harness.provider, 'refresh').resolves();

        try {
            harness.trustGrantHandler!();

            assert.ok(refresh.calledOnce);
        }
        finally {
            harness.dispose();
        }
    });

    test('refreshes when CLI path forwarding changes', () => {
        const harness = new ProviderHarness({ folders: [workspaceFolder('app', '/repo/app', 0)] });
        const refresh = sinon.stub(harness.provider, 'refresh').resolves();

        try {
            harness.forwardingEmitter.fire({ kind: 'window' });

            assert.ok(refresh.calledOnce);
        }
        finally {
            harness.dispose();
        }
    });

    test('coalesces refresh bursts into one follow-up with the latest result', async () => {
        const folder = workspaceFolder('app', '/repo/app', 0);
        const candidate = appHostCandidate(folder, 'AppHost.csproj');
        let completeOlderDiscovery: ((candidates: CandidateAppHostDisplayInfo[]) => void) | undefined;
        let markOlderDiscoveryStarted: (() => void) | undefined;
        const olderDiscoveryStarted = new Promise<void>(resolve => markOlderDiscoveryStarted = resolve);
        let discoveryCall = 0;
        const harness = new ProviderHarness({
            folders: [folder],
            candidatesFor: () => {
                discoveryCall++;
                if (discoveryCall === 1) {
                    markOlderDiscoveryStarted!();
                    return new Promise<CandidateAppHostDisplayInfo[]>(resolve => completeOlderDiscovery = resolve);
                }

                return Promise.resolve([]);
            },
        });

        try {
            const olderRefresh = harness.provider.refresh();
            await olderDiscoveryStarted;
            const newerRefreshes = Array.from({ length: 10 }, () => harness.provider.refresh());

            completeOlderDiscovery!([candidate]);
            await Promise.all([olderRefresh, ...newerRefreshes]);

            assert.strictEqual(discoveryCall, 2, 'a burst must produce only one follow-up refresh');
            assert.deepStrictEqual(harness.definitions(), [], 'the follow-up result must win');
        }
        finally {
            harness.dispose();
        }
    });
});

suite('AspireMcpServerDefinitionProvider CLI rejection tests', () => {
    test('refreshes when CLI resolution rejects a configured path', async () => {
        cliPath.resetRejectedConfiguredCliPathForForwarding();
        const candidateChangeEmitter = new vscode.EventEmitter<vscode.WorkspaceFolder>();
        const provider = new AspireMcpServerDefinitionProvider({
            appHostDiscovery: { discover: async () => [], onDidChangeCandidates: candidateChangeEmitter.event },
            capabilityProbe: { getCapabilityStatus: async () => 'supported' },
        });
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
            candidateChangeEmitter.dispose();
            cliPath.resetRejectedConfiguredCliPathForForwarding();
        }
    });
});
