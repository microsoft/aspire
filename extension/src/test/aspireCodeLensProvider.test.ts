/// <reference types="mocha" />

import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createMockDocument } from './testHelpers';
import { AspireCodeLensProvider } from '../editor/AspireCodeLensProvider';
import { AspireGutterDecorationProvider } from '../editor/AspireGutterDecorationProvider';
import * as AppHostResourceParser from '../editor/parsers/AppHostResourceParser';
import { ParsedResource } from '../editor/parsers/AppHostResourceParser';
import { codeLensCommand, codeLensJavaAppHostAlreadyRunning, codeLensJavaAppHostAlreadyRunningTooltip, codeLensJavaAppHostUseAspire, codeLensJavaAppHostUseAspireTooltip, codeLensResourceValueMissing, codeLensRustAppHostUseAspire, codeLensSpringBootDashboardBypassesAspire, codeLensSpringBootDashboardBypassesAspireTooltip } from '../loc/strings';
import { ResourceState, ResourceType } from '../editor/resourceConstants';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { AppHostDataRepository, AppHostDisplayInfo, ResourceJson } from '../data/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
import type { ResourceDebugger } from '../debugger/resourceDebugContracts';
// Import parsers so they self-register before the provider consults them.
import '../editor/parsers/csharpAppHostParser';
import '../editor/parsers/jsTsAppHostParser';
import '../editor/parsers/rustAppHostParser';

// Build platform-native paths so dirname comparison works on Windows too
// (vscode.Uri.file('/foo/bar').fsPath becomes '\\foo\\bar' on Windows, so we
// need the host paths to use the same separator).
function p(...segments: string[]): string {
    return path.join(path.sep, ...segments);
}


function makeAppHost(appHostPath: string): AppHostDisplayInfo {
    return {
        appHostPid: 1234,
        appHostPath,
        cliPid: undefined,
        dashboardUrl: undefined,
        resources: [],
        appHostName: 'Test',
    } as unknown as AppHostDisplayInfo;
}

function makeResource(name: string, overrides: Partial<ResourceJson> = {}): ResourceJson {
    return {
        name,
        displayName: name,
        type: 'container',
        state: 'Running',
        stateStyle: '',
        commands: {},
        endpoints: [],
        ...overrides,
    } as unknown as ResourceJson;
}

interface TestHarness {
    provider: AspireCodeLensProvider;
    appHostsStub: sinon.SinonStub;
    workspaceResourcesStub: sinon.SinonStub;
    workspaceAppHostPathStub: sinon.SinonStub;
    repository: AppHostDataRepository;
    treeProvider: AspireAppHostTreeProvider;
    dispose(): void;
}

function createHarness(opts: {
    appHosts?: AppHostDisplayInfo[];
    workspaceResources?: ResourceJson[];
    workspaceAppHostPath?: string;
    installedExtensions?: string[];
}): TestHarness {
    const subs: vscode.Disposable[] = [];
    const terminalProvider = new AspireTerminalProvider(subs);
    const repository = new AppHostDataRepository(terminalProvider);
    const resourceDebugger: ResourceDebugger = {
        debug: async () => ({ outcome: 'unsupportedResource' }),
        canAttachToResource: () => false,
    };
    const treeProvider = new AspireAppHostTreeProvider(repository, terminalProvider, new AppHostLaunchService({
        getCapabilityStatus: async () => 'supported',
    }), resourceDebugger);

    const appHostsStub = sinon.stub(repository, 'appHosts').get(() => opts.appHosts ?? []);
    const workspaceResourcesStub = sinon.stub(repository, 'workspaceResources').get(() => opts.workspaceResources ?? []);
    const workspaceAppHostPathStub = sinon.stub(repository, 'workspaceAppHostPath').get(() => opts.workspaceAppHostPath);

    const provider = new AspireCodeLensProvider(
        treeProvider,
        repository,
        extensionId => (opts.installedExtensions ?? []).includes(extensionId));

    return {
        provider,
        appHostsStub,
        workspaceResourcesStub,
        workspaceAppHostPathStub,
        repository,
        treeProvider,
        dispose() {
            workspaceAppHostPathStub.restore();
            workspaceResourcesStub.restore();
            appHostsStub.restore();
            treeProvider.dispose();
            repository.dispose();
            subs.forEach(s => s.dispose());
        },
    };
}

const APP_HOST_DOC = 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddRedis("cache");\nbuilder.Build().Run();';
const APP_HOST_NO_RESOURCES = 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();';

const cancellationToken = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => { } }) } as vscode.CancellationToken;

function createMockEditor(document: vscode.TextDocument): { editor: vscode.TextEditor; decorationCalls: vscode.DecorationOptions[][]; decorationState: Map<vscode.TextEditorDecorationType, vscode.DecorationOptions[]> } {
    const decorationCalls: vscode.DecorationOptions[][] = [];
    const decorationState = new Map<vscode.TextEditorDecorationType, vscode.DecorationOptions[]>();
    const editor = {
        document,
        setDecorations: (decorationType: vscode.TextEditorDecorationType, options: readonly vscode.DecorationOptions[]) => {
            const copiedOptions = [...options];
            decorationCalls.push(copiedOptions);
            decorationState.set(decorationType, copiedOptions);
        },
    } as unknown as vscode.TextEditor;

    return { editor, decorationCalls, decorationState };
}

function getDecoratedLines(decorationCalls: readonly vscode.DecorationOptions[][]): number[] {
    return decorationCalls
        .flatMap(options => options.map(option => option.range.start.line))
        .sort((left, right) => left - right);
}

function getCurrentDecoratedLines(decorationState: ReadonlyMap<vscode.TextEditorDecorationType, readonly vscode.DecorationOptions[]>): number[] {
    return getDecoratedLines([...decorationState.values()].map(options => [...options]));
}

async function applyGutterDecorations(provider: AspireGutterDecorationProvider, editor: vscode.TextEditor): Promise<void> {
    await (provider as unknown as { _applyDecorations(editor: vscode.TextEditor): Promise<void> })._applyDecorations(editor);
}

function makeParsedResource(name: string, line: number): ParsedResource {
    return {
        name,
        methodName: 'AddContainer',
        range: new vscode.Range(line, 0, line, 0),
        kind: 'resource',
        statementStartLine: line,
    };
}

suite('AspireCodeLensProvider builder lens', () => {
    let getConfigStub: sinon.SinonStub;

    setup(() => {
        getConfigStub = sinon.stub(vscode.workspace, 'getConfiguration').returns({
            get: () => true,
            has: () => true,
            inspect: () => undefined,
            update: () => Promise.resolve(),
        } as any);
    });

    teardown(() => {
        getConfigStub.restore();
    });

    test('emits builder lenses when document matches a running global AppHost', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        const doc = createMockDocument(APP_HOST_DOC, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 2);
        assert.deepStrictEqual(builderLenses[0].command?.arguments, [hostPath]);
        assert.deepStrictEqual(builderLenses[1].command?.arguments, [hostPath]);
        harness.dispose();
    });

    test('emits builder lenses when .mts document matches a running global AppHost', async () => {
        const appHostPath = p('repo', 'AppHost', 'apphost.mts');
        const harness = createHarness({ appHosts: [makeAppHost(appHostPath)] });

        const doc = createMockDocument([
            'import { createBuilder } from "@aspire/sdk";',
            'const builder = await createBuilder();',
            'await builder.addRedis("cache");',
        ].join('\n'), appHostPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 2);
        assert.deepStrictEqual(builderLenses[0].command?.arguments, [appHostPath]);
        assert.deepStrictEqual(builderLenses[1].command?.arguments, [appHostPath]);
        harness.dispose();
    });

    test('emits builder lenses when Windows AppHost path casing differs from document path', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'apphost', 'apphost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        try {
            const doc = createMockDocument(APP_HOST_DOC, docPath);
            const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
            const builderLenses = lenses.filter(l =>
                l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
                l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
            );

            assert.strictEqual(builderLenses.length, 2);
            assert.deepStrictEqual(builderLenses[0].command?.arguments, [hostPath]);
        } finally {
            harness.dispose();
            platformStub.restore();
        }
    });

    test('does not emit builder lenses when no AppHost is running', async () => {
        const harness = createHarness({});

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('does not emit builder lenses when running AppHost is in an unrelated directory', async () => {
        const harness = createHarness({ appHosts: [makeAppHost(p('elsewhere', 'Other.csproj'))] });

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('does not emit resource lenses from a different running AppHost', async () => {
        const runningHostPath = p('repo', 'RunningAppHost', 'AppHost.csproj');
        const stoppedHostPath = p('repo', 'StoppedAppHost', 'AppHost.cs');
        const runningAppHost = {
            ...makeAppHost(runningHostPath),
            resources: [makeResource('cache')],
        };
        const harness = createHarness({ appHosts: [runningAppHost] });

        const doc = createMockDocument(APP_HOST_DOC, stoppedHostPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const resourceLenses = lenses.filter(l =>
            l.command?.command !== 'aspire-vscode.codeLensOpenDashboard'
            && l.command?.command !== 'aspire-vscode.codeLensViewAppHostLogs'
            && l.command?.command !== 'aspire-vscode.codeLensDebugPipelineStep'
        );

        assert.strictEqual(resourceLenses.length, 0);
        harness.dispose();
    });

    test('resource reveal lens includes the matching AppHost path', async () => {
        const firstHostPath = p('repo', 'FirstAppHost', 'AppHost.csproj');
        const secondHostPath = p('repo', 'SecondAppHost', 'AppHost.csproj');
        const secondDocPath = p('repo', 'SecondAppHost', 'AppHost.cs');
        const harness = createHarness({
            appHosts: [
                { ...makeAppHost(firstHostPath), resources: [makeResource('cache', { name: 'cache-a' })] },
                { ...makeAppHost(secondHostPath), resources: [makeResource('cache', { name: 'cache-b' })] },
            ],
        });

        const doc = createMockDocument(APP_HOST_DOC, secondDocPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const revealLens = lenses.find(lens => lens.command?.command === 'aspire-vscode.codeLensRevealResource');

        assert.deepStrictEqual(revealLens?.command?.arguments, ['cache', secondHostPath]);
        harness.dispose();
    });

    test('emits builder lenses for AppHost file with no Add* calls when host is running', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        const doc = createMockDocument(APP_HOST_NO_RESOURCES, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 2);
        harness.dispose();
    });

    test('emits builder lenses for workspace AppHost when document matches workspace path and resources are live', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('cache')],
        });

        const doc = createMockDocument(APP_HOST_DOC, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 2);
        assert.deepStrictEqual(builderLenses[0].command?.arguments, [hostPath]);
        harness.dispose();
    });

    test('does not emit builder lenses when workspaceAppHostPath is set but no workspace resources are live', async () => {
        const harness = createHarness({
            workspaceAppHostPath: p('repo', 'AppHost', 'AppHost.csproj'),
            workspaceResources: [],
        });

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('does not emit builder lenses when workspace AppHost is in a different directory', async () => {
        const harness = createHarness({
            workspaceAppHostPath: p('elsewhere', 'Other.csproj'),
            workspaceResources: [makeResource('cache')],
        });

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('warns on the main CodeLens row when a Rust AppHost is already running', async () => {
        const appHostPath = p('repo', 'AppHost', 'apphost.rs');
        const content = [
            'fn main() {',
            '    let builder = create_builder(None)?;',
            '}',
        ].join('\n');
        const harness = createHarness({ appHosts: [makeAppHost(appHostPath)] });

        const lenses = await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[];
        const warningLens = lenses.find(lens => lens.command?.command === 'aspire-vscode.codeLensRevealAppHost');

        assert.ok(warningLens);
        assert.strictEqual(warningLens.command?.title, '⚠️ Do not click the rust-analyzer Run or Debug actions; this AppHost is already running in Aspire');
        assert.strictEqual(warningLens.command?.tooltip, 'Use Aspire controls instead. rust-analyzer starts another Cargo process outside the running Aspire session.');
        assert.deepStrictEqual(warningLens.command?.arguments, [appHostPath]);
        assert.strictEqual(warningLens.range.start.line, 0);
        harness.dispose();
    });

    test('renders the stopped Rust AppHost warning as non-clickable text', async () => {
        const appHostPath = p('repo', 'AppHost', 'apphost.rs');
        const content = 'fn main() {\n    let builder = create_builder(None)?;\n}';
        const harness = createHarness({});

        const lenses = await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[];
        const warningLens = lenses.find(lens => lens.command?.title === codeLensRustAppHostUseAspire);

        assert.ok(warningLens);
        assert.strictEqual(warningLens.command?.title, '⚠️ Do not click the rust-analyzer Run or Debug actions; they bypass Aspire');
        assert.strictEqual(warningLens.command?.tooltip, 'Use Aspire Run or Debug instead. rust-analyzer starts Cargo directly, so VS Code does not create or attach to an Aspire AppHost session.');
        // An empty command id keeps the warning from being a link that reveals an AppHost the tree
        // cannot contain while it is stopped.
        assert.strictEqual(warningLens.command?.command, '');
        assert.strictEqual(warningLens.command?.arguments, undefined);
        assert.ok(!lenses.some(lens => lens.command?.command === 'aspire-vscode.codeLensRevealAppHost'));
        assert.strictEqual(warningLens.range.start.line, 0);
        harness.dispose();
    });

    test('does not add the Rust warning to a running C# AppHost', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        const lenses = await harness.provider.provideCodeLenses(createMockDocument(APP_HOST_DOC, docPath), cancellationToken) as vscode.CodeLens[];

        assert.ok(!lenses.some(lens => lens.command?.command === 'aspire-vscode.codeLensRevealAppHost'));
        harness.dispose();
    });

    test('returns empty array for non-AppHost documents', async () => {
        const harness = createHarness({ appHosts: [makeAppHost(p('repo', 'AppHost', 'AppHost.csproj'))] });

        const doc = createMockDocument('using System;\nclass Program { }', p('repo', 'AppHost', 'Program.cs'));
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];

        assert.strictEqual(lenses.length, 0);
        harness.dispose();
    });

    test('returns undefined when cancellation is requested during CodeLens computation', async () => {
        const harness = createHarness({ appHosts: [makeAppHost(p('repo', 'AppHost', 'AppHost.csproj'))] });

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const cancelledToken = { isCancellationRequested: true, onCancellationRequested: () => ({ dispose: () => { } }) } as vscode.CancellationToken;
        const lenses = await harness.provider.provideCodeLenses(doc, cancelledToken);

        assert.strictEqual(lenses, undefined);
        harness.dispose();
    });

    test('builder lens points at the builder line, not the resource line', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        const doc = createMockDocument(APP_HOST_DOC, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        // Builder line is line 0 in our fixture document.
        for (const lens of builderLenses) {
            assert.strictEqual(lens.range.start.line, 0);
        }
        harness.dispose();
    });
});

suite('AspireGutterDecorationProvider resource decoration filtering', () => {
    let getConfigStub: sinon.SinonStub;
    let visibleEditorsStub: sinon.SinonStub;

    setup(() => {
        getConfigStub = sinon.stub(vscode.workspace, 'getConfiguration').returns({
            get: () => true,
            has: () => true,
            inspect: () => undefined,
            update: () => Promise.resolve(),
        } as any);
        visibleEditorsStub = sinon.stub(vscode.window, 'visibleTextEditors').get(() => []);
    });

    teardown(() => {
        visibleEditorsStub.restore();
        getConfigStub.restore();
    });

    test('does not decorate commented or string resource calls in C# and JS/TS AppHosts', async () => {
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [
                makeResource('active-csharp'),
                makeResource('commented-csharp'),
                makeResource('string-csharp'),
                makeResource('active-ts'),
                makeResource('commented-ts'),
                makeResource('string-ts'),
            ],
        });
        const provider = new AspireGutterDecorationProvider(harness.treeProvider);

        const csharpDoc = createMockDocument([
            'var builder = DistributedApplication.CreateBuilder(args);',
            'builder.AddContainer("active-csharp", "nginx");',
            '// builder.AddContainer("commented-csharp", "nginx");',
            'var sample = "builder.AddContainer(\\"string-csharp\\", \\"nginx\\")";',
        ].join('\n'), p('repo', 'AppHost', 'AppHost.cs'));
        const csharpEditor = createMockEditor(csharpDoc);

        const tsDoc = createMockDocument([
            'import { createBuilder } from "@aspire/sdk";',
            'const builder = await createBuilder();',
            'await builder.addContainer("active-ts", "nginx");',
            '// await builder.addContainer("commented-ts", "nginx");',
            'const sample = "await builder.addContainer(\\"string-ts\\", \\"nginx\\");";',
        ].join('\n'), p('repo', 'AppHost', 'apphost.ts'));
        const tsEditor = createMockEditor(tsDoc);

        try {
            await applyGutterDecorations(provider, csharpEditor.editor);
            await applyGutterDecorations(provider, tsEditor.editor);

            assert.deepStrictEqual(getDecoratedLines(csharpEditor.decorationCalls), [1]);
            assert.deepStrictEqual(getDecoratedLines(tsEditor.decorationCalls), [2]);
        } finally {
            provider.dispose();
            harness.dispose();
        }
    });

    test('ignores stale gutter decoration results that complete after a newer update', async () => {
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [
                makeResource('stale'),
                makeResource('fresh'),
            ],
        });
        const provider = new AspireGutterDecorationProvider(harness.treeProvider);
        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const editor = createMockEditor(doc);
        let resolveStaleParse: ((resources: ParsedResource[]) => void) | undefined;
        const staleParser = {
            getSupportedExtensions: () => ['.cs'],
            isAppHostFile: async () => true,
            parseResources: () => new Promise<ParsedResource[]>(resolve => {
                resolveStaleParse = resolve;
            }),
        } satisfies AppHostResourceParser.AppHostResourceParser;
        const freshParser = {
            getSupportedExtensions: () => ['.cs'],
            isAppHostFile: async () => true,
            parseResources: async () => [makeParsedResource('fresh', 0)],
        } satisfies AppHostResourceParser.AppHostResourceParser;
        const getParserStub = sinon.stub(AppHostResourceParser, 'getParserForDocument');
        getParserStub.onFirstCall().resolves(staleParser);
        getParserStub.onSecondCall().resolves(freshParser);

        try {
            const staleApply = applyGutterDecorations(provider, editor.editor);
            await Promise.resolve();

            await applyGutterDecorations(provider, editor.editor);
            resolveStaleParse!([makeParsedResource('stale', 1)]);
            await staleApply;

            assert.deepStrictEqual(getCurrentDecoratedLines(editor.decorationState), [0]);
        } finally {
            getParserStub.restore();
            provider.dispose();
            harness.dispose();
        }
    });

    test('allows concurrent gutter decoration updates for different editors to complete independently', async () => {
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [
                makeResource('first-editor'),
                makeResource('second-editor'),
            ],
        });
        const provider = new AspireGutterDecorationProvider(harness.treeProvider);
        const firstEditor = createMockEditor(createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'First.cs')));
        const secondEditor = createMockEditor(createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'Second.cs')));
        let resolveFirstParse: ((resources: ParsedResource[]) => void) | undefined;
        const firstParser = {
            getSupportedExtensions: () => ['.cs'],
            isAppHostFile: async () => true,
            parseResources: () => new Promise<ParsedResource[]>(resolve => {
                resolveFirstParse = resolve;
            }),
        } satisfies AppHostResourceParser.AppHostResourceParser;
        const secondParser = {
            getSupportedExtensions: () => ['.cs'],
            isAppHostFile: async () => true,
            parseResources: async () => [makeParsedResource('second-editor', 1)],
        } satisfies AppHostResourceParser.AppHostResourceParser;
        const getParserStub = sinon.stub(AppHostResourceParser, 'getParserForDocument');
        getParserStub.onFirstCall().resolves(firstParser);
        getParserStub.onSecondCall().resolves(secondParser);

        try {
            const firstApply = applyGutterDecorations(provider, firstEditor.editor);
            await Promise.resolve();

            await applyGutterDecorations(provider, secondEditor.editor);
            resolveFirstParse!([makeParsedResource('first-editor', 0)]);
            await firstApply;

            assert.deepStrictEqual(getCurrentDecoratedLines(firstEditor.decorationState), [0]);
            assert.deepStrictEqual(getCurrentDecoratedLines(secondEditor.decorationState), [1]);
        } finally {
            getParserStub.restore();
            provider.dispose();
            harness.dispose();
        }
    });
});

suite('AspireCodeLensProvider resource lens anchoring', () => {
    let getConfigStub: sinon.SinonStub;

    setup(() => {
        getConfigStub = sinon.stub(vscode.workspace, 'getConfiguration').returns({
            get: () => true,
            has: () => true,
            inspect: () => undefined,
            update: () => Promise.resolve(),
        } as any);
    });

    teardown(() => {
        getConfigStub.restore();
    });

    function getResourceLenses(lenses: vscode.CodeLens[]): vscode.CodeLens[] {
        return lenses.filter(l =>
            l.command?.command !== 'aspire-vscode.codeLensOpenDashboard'
            && l.command?.command !== 'aspire-vscode.codeLensViewAppHostLogs'
            && l.command?.command !== 'aspire-vscode.codeLensDebugPipelineStep'
        );
    }

    function getStateLenses(lenses: vscode.CodeLens[]): vscode.CodeLens[] {
        return lenses.filter(l => l.command?.command === 'aspire-vscode.codeLensRevealResource');
    }

    test('emits resource state and action lenses for a running Rust AppHost', async () => {
        const appHostPath = p('repo', 'AppHost', 'apphost.rs');
        const content = [
            'fn main() {',
            '    let builder = create_builder(None)?;',
            '    let cache = builder.add_redis("cache")?;',
            '}',
        ].join('\n');
        const harness = createHarness({
            workspaceAppHostPath: appHostPath,
            workspaceResources: [makeResource('cache', {
                commands: {
                    restart: {
                        displayName: 'Restart',
                        description: null,
                        state: 'Enabled',
                        visibility: 'Api, Ui',
                    },
                },
            })],
        });

        const lenses = await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[];

        assert.ok(lenses.some(lens => lens.command?.command === 'aspire-vscode.codeLensRevealResource'));
        assert.ok(lenses.some(lens => lens.command?.command === 'aspire-vscode.codeLensResourceAction'));
        harness.dispose();
    });

    test('renders resource and builder lenses for a Java AppHost, which previously had none', async () => {
        const appHostPath = p('repo', 'AppHost', 'AppHost.java');
        const content = [
            'import aspire.*;',
            'void main() throws Exception {',
            '    var builder = DistributedApplication.CreateBuilder();',
            '    var catalog = builder.addSpringBootApp("catalog", "./catalog");',
            '    builder.build().run();',
            '}',
        ].join('\n');
        const harness = createHarness({
            appHosts: [makeAppHost(appHostPath)],
            workspaceAppHostPath: appHostPath,
            workspaceResources: [makeResource('catalog', { state: 'Running' })],
        });

        const lenses = await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[];

        assert.ok(lenses.some(lens => lens.range.start.line === 3), 'expected at least one lens anchored on the addSpringBootApp line');
        assert.ok(lenses.some(lens => lens.command?.command === 'aspire-vscode.codeLensOpenDashboard'), 'expected the builder statement to carry Open Dashboard');
        harness.dispose();
    });

    test('emits pipeline debug lenses for a stopped Rust AppHost', async () => {
        const appHostPath = p('repo', 'AppHost', 'apphost.rs');
        const content = [
            'fn main() {',
            '    let builder = create_builder(None)?;',
            '    builder.add_step("publish")?;',
            '}',
        ].join('\n');
        const harness = createHarness({ workspaceAppHostPath: appHostPath });

        const lenses = await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[];
        const pipelineLens = lenses.find(lens => lens.command?.command === 'aspire-vscode.codeLensDebugPipelineStep');

        assert.ok(pipelineLens);
        assert.deepStrictEqual(pipelineLens.command?.arguments, ['publish']);
        assert.strictEqual(pipelineLens.range.start.line, 2);
        harness.dispose();
    });

    test('does not emit resource state lenses for line-commented C# resource calls', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const content = [
            'var builder = DistributedApplication.CreateBuilder(args);',
            'builder.AddContainer("active", "nginx");',
            '// builder.AddContainer("active", "nginx");',
            '    //builder.AddContainer("line-commented", "nginx");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [
                makeResource('active'),
                makeResource('line-commented'),
            ],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const stateLenses = getStateLenses(lenses);

        assert.strictEqual(stateLenses.length, 1);
        assert.strictEqual(stateLenses[0].range.start.line, 1);
        harness.dispose();
    });

    test('does not emit resource state lenses for block-commented C# resource calls', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const content = [
            'var builder = DistributedApplication.CreateBuilder(args);',
            '/*',
            'builder.AddContainer("block-commented", "nginx");',
            'nested-looking block opener /* does not make this active',
            'builder.AddContainer("also-block-commented", "nginx");',
            '*/',
            'builder.AddContainer("active", "nginx");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [
                makeResource('active'),
                makeResource('block-commented'),
                makeResource('also-block-commented'),
            ],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const stateLenses = getStateLenses(lenses);

        assert.strictEqual(stateLenses.length, 1);
        assert.strictEqual(stateLenses[0].range.start.line, 6);
        harness.dispose();
    });

    test('does not emit resource state lenses for C# resource calls in trailing comments', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const content = [
            'var builder = DistributedApplication.CreateBuilder(args);',
            'builder.AddContainer("active", "nginx"); // builder.AddContainer("trailing-commented", "nginx");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [
                makeResource('active'),
                makeResource('trailing-commented'),
            ],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const stateLenses = getStateLenses(lenses);

        assert.strictEqual(stateLenses.length, 1);
        assert.strictEqual(stateLenses[0].range.start.line, 1);
        harness.dispose();
    });

    test('does not emit resource state lenses for C# resource calls inside strings', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const content = [
            'var builder = DistributedApplication.CreateBuilder(args);',
            'var escaped = "builder.AddContainer(\\"escaped\\", \\"nginx\\")";',
            'var verbatim = @"builder.AddContainer(""verbatim"", ""nginx"")";',
            'var interpolatedVerbatim = $@"builder.AddContainer(""interpolated-verbatim"", ""nginx"")";',
            'var raw = """',
            'builder.AddContainer("raw", "nginx");',
            '""";',
            'var interpolatedRaw = $"""',
            'builder.AddContainer("interpolated-raw", "nginx");',
            '""";',
            'builder.AddContainer("active", "nginx");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [
                makeResource('active'),
                makeResource('escaped'),
                makeResource('verbatim'),
                makeResource('interpolated-verbatim'),
                makeResource('raw'),
                makeResource('interpolated-raw'),
            ],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const stateLenses = getStateLenses(lenses);

        assert.strictEqual(stateLenses.length, 1);
        assert.strictEqual(stateLenses[0].range.start.line, 10);
        harness.dispose();
    });

    test('still emits resource state lenses for active C# resource calls with whitespace', async () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const content = [
            'var builder = DistributedApplication.CreateBuilder(args);',
            'var active = builder',
            '    .AddContainer(',
            '        "active",',
            '        "nginx");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('active')],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const stateLenses = getStateLenses(lenses);

        assert.strictEqual(stateLenses.length, 1);
        assert.strictEqual(stateLenses[0].range.start.line, 1);
        harness.dispose();
    });

    test('single-resource fluent chain anchors lens at the statement-start line, not the .add* call line', async () => {
        const docPath = p('repo', 'AppHost', 'apphost.ts');
        const hostPath = p('repo', 'AppHost', 'apphost.ts');
        // Multi-line chain: declaration starts at line 2 ("const nodePlayer = await builder")
        // and the .addNodeApp(...) call is on line 3. Line 0 carries the createBuilder()
        // entry point so the parser recognizes this as an AppHost file.
        const content = [
            'const builder = await createBuilder();',                               // line 0
            '',                                                                     // line 1
            '// Node Knight (Player 2)',                                            // line 2
            'const nodePlayer = await builder',                                     // line 3
            '    .addNodeApp("node-player", "./node-player", "src/server.ts")',     // line 4
            '    .withRunScript("dev");',                                           // line 5
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('node-player')],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const resourceLenses = getResourceLenses(lenses);

        assert.ok(resourceLenses.length > 0, 'expected at least one resource lens for node-player');
        for (const lens of resourceLenses) {
            assert.strictEqual(
                lens.range.start.line,
                3,
                `resource lens should anchor at statement-start line 3 (above 'const nodePlayer'), got ${lens.range.start.line}`
            );
        }
        harness.dispose();
    });

    test('multi-resource fluent chain anchors each resource lens at its own .add* call line', async () => {
        const docPath = p('repo', 'AppHost', 'apphost.ts');
        const hostPath = p('repo', 'AppHost', 'apphost.ts');
        // Single fluent chain declaring two resources. Statement starts at line 2,
        // pg call is on line 2, db call on line 3. We expect each resource's lens
        // to anchor at its own .addX line so they don't stack.
        const content = [
            'const builder = await createBuilder();',         // line 0
            '',                                                // line 1
            'const db = builder.addPostgres("pg")',            // line 2 (statement-start AND pg call)
            '    .addDatabase("db");',                          // line 3 (db call)
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('pg'), makeResource('db')],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const resourceLenses = getResourceLenses(lenses);

        const lines = new Set(resourceLenses.map(l => l.range.start.line));
        assert.ok(
            lines.has(2) && lines.has(3),
            `expected resource lenses on both line 2 (pg) and line 3 (db) so they don't stack; got lines [${[...lines].join(', ')}]`
        );
        harness.dispose();
    });

    test('custom command lens uses displayName as label and description as tooltip', async () => {
        const docPath = p('repo', 'AppHost', 'apphost.ts');
        const hostPath = p('repo', 'AppHost', 'apphost.ts');
        const content = [
            'const builder = await createBuilder();',
            'builder.addRedis("cache");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('cache', {
                commands: {
                    'reset-db': {
                        displayName: 'Reset Database',
                        description: 'Stop the resource, rebuild the project from source, and restart it.',
                    },
                },
            })],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const customLens = lenses.find(l =>
            l.command?.command === 'aspire-vscode.codeLensResourceAction'
            && l.command?.arguments?.[1] === 'reset-db');

        assert.ok(customLens);
        assert.strictEqual(customLens!.command?.title, codeLensCommand('Reset Database'));
        assert.strictEqual(customLens!.command?.tooltip, 'Stop the resource, rebuild the project from source, and restart it.');
        harness.dispose();
    });

    test('resource action lenses only execute enabled commands', async () => {
        const docPath = p('repo', 'AppHost', 'apphost.ts');
        const hostPath = p('repo', 'AppHost', 'apphost.ts');
        const content = [
            'const builder = await createBuilder();',
            'builder.addRedis("cache");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('cache', {
                commands: {
                    restart: {
                        displayName: 'Restart',
                        description: null,
                        visibility: 'Api',
                    },
                    stop: {
                        displayName: 'Stop',
                        description: null,
                        state: 'Disabled',
                    },
                    start: {
                        displayName: 'Start',
                        description: null,
                        state: 'Hidden',
                    },
                    'reset-db': {
                        displayName: 'Reset Database',
                        description: null,
                        state: 'Enabled',
                        visibility: 'Api',
                    },
                    'ui-custom': {
                        displayName: 'UI Custom',
                        description: null,
                        state: 'Enabled',
                        visibility: 'Api, Ui',
                    },
                    'disabled-custom': {
                        displayName: 'Disabled Custom',
                        description: null,
                        state: 'Disabled',
                    },
                    'hidden-custom': {
                        displayName: 'Hidden Custom',
                        description: null,
                        state: 'Hidden',
                    },
                    'legacy-custom': {
                        displayName: 'Legacy Custom',
                        description: null,
                    },
                },
            })],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const actionNames = lenses
            .filter(l => l.command?.command === 'aspire-vscode.codeLensResourceAction')
            .map(l => l.command!.arguments![1])
            .sort();

        assert.deepStrictEqual(actionNames, ['legacy-custom', 'ui-custom']);
        const restartLens = lenses.find(l => l.command?.arguments?.[1] === 'restart');
        assert.strictEqual(restartLens, undefined);
        harness.dispose();
    });

    test('custom command lens falls back to command name when display text is whitespace', async () => {
        const docPath = p('repo', 'AppHost', 'apphost.ts');
        const hostPath = p('repo', 'AppHost', 'apphost.ts');
        const content = [
            'const builder = await createBuilder();',
            'builder.addRedis("cache");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('cache', {
                commands: {
                    'reset-db': {
                        displayName: '   ',
                        description: '   ',
                    },
                },
            })],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const customLens = lenses.find(l =>
            l.command?.command === 'aspire-vscode.codeLensResourceAction'
            && l.command?.arguments?.[1] === 'reset-db');

        assert.ok(customLens);
        assert.strictEqual(customLens!.command?.title, codeLensCommand('reset-db'));
        assert.strictEqual(customLens!.command?.tooltip, 'reset-db');
        harness.dispose();
    });

    test('custom command lens falls back to command name when displayName is omitted', async () => {
        const docPath = p('repo', 'AppHost', 'apphost.ts');
        const hostPath = p('repo', 'AppHost', 'apphost.ts');
        const content = [
            'const builder = await createBuilder();',
            'builder.addRedis("cache");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('cache', {
                commands: {
                    'reset-db': {
                        description: null,
                    },
                },
            })],
        });

        const doc = createMockDocument(content, docPath);
        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const customLens = lenses.find(l =>
            l.command?.command === 'aspire-vscode.codeLensResourceAction'
            && l.command?.arguments?.[1] === 'reset-db');

        assert.ok(customLens);
        assert.strictEqual(customLens!.command?.title, codeLensCommand('reset-db'));
        assert.strictEqual(customLens!.command?.tooltip, 'reset-db');
        harness.dispose();
    });

    function makeParameterHarness(overrides: Partial<ResourceJson>) {
        const hostPath = p('repo', 'AppHost', 'apphost.ts');
        const content = [
            'const builder = await createBuilder();',
            'builder.addParameter("param");',
        ].join('\n');

        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('param', {
                resourceType: ResourceType.Parameter,
                state: ResourceState.Running,
                ...overrides,
            } as Partial<ResourceJson>)],
        });

        return { harness, doc: createMockDocument(content, p('repo', 'AppHost', 'apphost.ts')) };
    }

    const revealLenses = (lenses: vscode.CodeLens[]) =>
        lenses.filter(l => l.command?.command === 'aspire-vscode.codeLensRevealResource');

    test('parameter value lens shows a non-secret value', async () => {
        const { harness, doc } = makeParameterHarness({
            properties: { Value: 'plain-value' } as any,
            commands: {
                'set-parameter': { displayName: 'Set parameter', description: null, argumentInputs: [{ name: 'Value', inputType: 'Text' }] },
            } as any,
        });

        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const valueLens = revealLenses(lenses).find(l => l.command?.title === 'plain-value');

        assert.ok(valueLens, 'expected a value lens showing the parameter value');
        harness.dispose();
    });

    test('parameter value lens masks secret values', async () => {
        const { harness, doc } = makeParameterHarness({
            properties: { Value: 'super-secret-value' } as any,
            commands: {
                'set-parameter': { displayName: 'Set parameter', description: null, argumentInputs: [{ name: 'Value', inputType: 'SecretText' }] },
            } as any,
        });

        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const titles = revealLenses(lenses).map(l => l.command?.title);

        assert.ok(titles.includes('●●●●●●●●'), 'expected a masked value lens');
        assert.ok(!titles.includes('super-secret-value'), 'secret value must not be displayed');
        harness.dispose();
    });

    test('parameter value lens truncates long values to 80 characters', async () => {
        const longValue = 'a'.repeat(100);
        const { harness, doc } = makeParameterHarness({
            properties: { Value: longValue } as any,
            commands: {
                'set-parameter': { displayName: 'Set parameter', description: null, argumentInputs: [{ name: 'Value', inputType: 'Text' }] },
            } as any,
        });

        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const valueLens = revealLenses(lenses).find(l => typeof l.command?.title === 'string' && l.command.title.endsWith('…'));

        assert.ok(valueLens, 'expected a truncated value lens');
        assert.strictEqual(valueLens!.command!.title!.length, 80);
        harness.dispose();
    });

    test('parameter with missing value shows the warning state lens and no value lens', async () => {
        const { harness, doc } = makeParameterHarness({
            state: ResourceState.ValueMissing,
            properties: {} as any,
            commands: {} as any,
        });

        const lenses = await harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const reveals = revealLenses(lenses);

        assert.strictEqual(reveals.length, 1, 'expected only the state lens (no value lens) for a missing value');
        assert.strictEqual(reveals[0].command?.title, codeLensResourceValueMissing);
        harness.dispose();
    });
    suite('Java AppHost entry point warning', () => {
        const javaAppHostPath = p('repo', 'AppHost', 'AppHost.java');

        function appHostSource(main: string): string {
            return [
                'import aspire.*;',
                main,
                '    var builder = DistributedApplication.CreateBuilder();',
                '    builder.addSpringBootApp("catalog", "./catalog");',
                '    builder.build().run();',
                '}',
            ].join('\n');
        }

        function entryPointLenses(lenses: vscode.CodeLens[]): vscode.CodeLens[] {
            return lenses.filter(lens =>
                lens.command?.title === codeLensJavaAppHostUseAspire ||
                lens.command?.title === codeLensJavaAppHostAlreadyRunning);
        }

        async function lensesFor(content: string, opts: Parameters<typeof createHarness>[0] = {}): Promise<vscode.CodeLens[]> {
            const harness = createHarness(opts);
            const lenses = entryPointLenses(await harness.provider.provideCodeLenses(createMockDocument(content, javaAppHostPath), cancellationToken) as vscode.CodeLens[]);
            harness.dispose();
            return lenses;
        }

        test('warns on the implicitly declared instance main the Java AppHost actually ships', async () => {
            // JEP 512: a source-launched AppHost.java has no class, no modifiers and no parameters.
            const lenses = await lensesFor(appHostSource('void main() throws Exception {'));

            assert.strictEqual(lenses.length, 1);
            assert.strictEqual(lenses[0].range.start.line, 1, 'the warning belongs on the declaration the Run/Debug lens sits above');
            assert.strictEqual(lenses[0].command?.title, codeLensJavaAppHostUseAspire);
            assert.strictEqual(lenses[0].command?.tooltip, codeLensJavaAppHostUseAspireTooltip);
            // Rendered as plain text: a stopped AppHost has nothing in the tree to reveal.
            assert.strictEqual(lenses[0].command?.command, '');
        });

        test('warns on a conventional static main, which is the Maven and Gradle project shape', async () => {
            const lenses = await lensesFor(appHostSource('public static void main(String[] args) throws Exception {'));

            assert.strictEqual(lenses.length, 1);
            assert.strictEqual(lenses[0].range.start.line, 1);
        });

        test('does not warn on a Java file that is not an AppHost', async () => {
            const lenses = await lensesFor('public class Application {\n    public static void main(String[] args) {\n    }\n}');

            assert.deepStrictEqual(lenses, [], 'detection is content-based, so an ordinary Java file with a main is left alone');
        });

        test('points at the running AppHost once it is started under Aspire', async () => {
            const lenses = await lensesFor(appHostSource('void main() {'), {
                appHosts: [makeAppHost(javaAppHostPath)],
            });

            assert.strictEqual(lenses.length, 1);
            assert.strictEqual(lenses[0].command?.title, codeLensJavaAppHostAlreadyRunning);
            assert.strictEqual(lenses[0].command?.tooltip, codeLensJavaAppHostAlreadyRunningTooltip);
            assert.strictEqual(lenses[0].command?.command, 'aspire-vscode.codeLensRevealAppHost');
            assert.deepStrictEqual(lenses[0].command?.arguments, [javaAppHostPath]);
        });
    });

    suite('Spring Boot Dashboard warning', () => {
        const springBootDashboard = 'vscjava.vscode-spring-boot-dashboard';

        function springBootLenses(lenses: vscode.CodeLens[]): vscode.CodeLens[] {
            return lenses.filter(lens => lens.command?.title === codeLensSpringBootDashboardBypassesAspire);
        }

        test('warns on the line that launches a Java resource through the Spring Boot Maven plugin', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.cs');
            const content = [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'builder.AddJavaApp("api", "../api")',
                '       .WithMavenGoal("spring-boot:run");',
                '',
                'builder.Build().Run();',
            ].join('\n');
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.strictEqual(lenses.length, 1);
            assert.strictEqual(lenses[0].range.start.line, 3, 'the warning belongs on the goal, not the Add call');
            assert.strictEqual(lenses[0].command?.tooltip, codeLensSpringBootDashboardBypassesAspireTooltip);
            // Rendered as plain text: there is no Spring Boot Dashboard command worth invoking here.
            assert.strictEqual(lenses[0].command?.command, '');
            harness.dispose();
        });

        test('warns on the Gradle bootRun task', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.cs');
            const content = [
                'var builder = DistributedApplication.CreateBuilder(args);',
                'builder.AddJavaApp("api", "../api").WithGradleTask("bootRun");',
            ].join('\n');
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.strictEqual(lenses.length, 1);
            assert.strictEqual(lenses[0].range.start.line, 1);
            harness.dispose();
        });

        test('warns on AddSpringBootApp, which configures the same launch internally', async () => {
            // The README leads with this form, so matching only the explicit goal would miss the case
            // users are most likely to hit.
            const cases: ReadonlyArray<readonly [string, string]> = [
                [p('repo', 'AppHost', 'apphost.cs'), 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddSpringBootApp("catalog", "../catalog");'],
                [p('repo', 'AppHost', 'apphost.ts'), "const builder = createBuilder();\nbuilder.addSpringBootApp('catalog', '../catalog');"],
                [p('repo', 'AppHost', 'apphost.rs'), 'fn main() {\n    let builder = create_builder(None)?;\n    let catalog = builder.add_spring_boot_app("catalog", "../catalog")?;\n}'],
                [p('repo', 'AppHost', 'apphost.py'), 'builder = create_builder()\nbuilder.add_spring_boot_app("catalog", "../catalog")'],
                [p('repo', 'AppHost', 'AppHost.java'), 'public class AppHost {\n    public static void main(String[] args) {\n        var builder = DistributedApplication.CreateBuilder();\n        builder.addSpringBootApp("catalog", "../catalog");\n    }\n}'],
                [p('repo', 'AppHost', 'apphost.go'), 'func main() {\n\tbuilder.AddSpringBootApp("catalog", "../catalog")\n}'],
            ];

            for (const [appHostPath, content] of cases) {
                const harness = createHarness({ installedExtensions: [springBootDashboard] });
                const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);
                assert.strictEqual(lenses.length, 1, `expected a warning for ${appHostPath}`);
                harness.dispose();
            }
        });

        test('stays silent for AddQuarkusApp, which the Spring Boot Dashboard does not offer to run', async () => {
            const harness = createHarness({ installedExtensions: [springBootDashboard] });
            const content = 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddQuarkusApp("inventory", "../inventory");';
            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, p('repo', 'AppHost', 'apphost.cs')), cancellationToken) as vscode.CodeLens[]);
            assert.strictEqual(lenses.length, 0);
            harness.dispose();
        });

        test('warns regardless of the AppHost language', async () => {
            const cases: ReadonlyArray<readonly [string, string]> = [
                [p('repo', 'AppHost', 'apphost.ts'), "const builder = createBuilder();\nbuilder.addJavaApp('api', '../api').withGradleTask('bootRun');"],
                [p('repo', 'AppHost', 'apphost.js'), 'const builder = createBuilder();\nbuilder.addJavaApp("api", "../api").withMavenGoal("spring-boot:run");'],
                [p('repo', 'AppHost', 'apphost.rs'), 'fn main() {\n    let builder = create_builder(None)?;\n    let api = builder.add_java_app("api", "../api")?.with_maven_goal("spring-boot:run")?;\n}'],
            ];

            for (const [appHostPath, content] of cases) {
                const harness = createHarness({ installedExtensions: [springBootDashboard] });
                const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);
                assert.strictEqual(lenses.length, 1, `expected a warning for ${appHostPath}`);
                harness.dispose();
            }
        });

        test('warns in AppHost languages that have no resource parser', async () => {
            // These languages produce no state or action lenses because nothing parses their resource
            // model, but they can still launch a Java resource through Spring Boot's build plugins.
            const cases: ReadonlyArray<readonly [string, string]> = [
                [p('repo', 'AppHost', 'apphost.py'), 'builder = create_builder()\nbuilder.add_java_app("api", "../api").with_gradle_task("bootRun")'],
                [p('repo', 'AppHost', 'apphost.go'), 'func main() {\n\tbuilder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run")\n}'],
            ];

            for (const [appHostPath, content] of cases) {
                const harness = createHarness({ installedExtensions: [springBootDashboard] });
                const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);
                assert.strictEqual(lenses.length, 1, `expected a warning for ${appHostPath}`);
                harness.dispose();
            }
        });

        test('stays silent for a source file that is not the AppHost', async () => {
            // The parserless languages have nothing narrowing the document to an AppHost, so without a
            // file-name gate every Python and Go file in the workspace would be scanned - and any
            // that happened to contain the call would get an Aspire lens.
            const cases: ReadonlyArray<readonly [string, string]> = [
                [p('repo', 'src', 'Application.java'), 'class Application {\n    void configure() {\n        builder.addJavaApp("api", "../api").withMavenGoal("spring-boot:run");\n    }\n}'],
                [p('repo', 'src', 'helpers.py'), 'builder.add_java_app("api", "../api").with_gradle_task("bootRun")'],
                [p('repo', 'src', 'main.go'), 'func run() {\n\tbuilder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run")\n}'],
            ];

            for (const [filePath, content] of cases) {
                const harness = createHarness({ installedExtensions: [springBootDashboard] });
                const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, filePath), cancellationToken) as vscode.CodeLens[]);
                assert.strictEqual(lenses.length, 0, `expected no warning for ${filePath}`);
                harness.dispose();
            }
        });

        test('warns in a nested AppHost that keeps the conventional file name', async () => {
            // A Maven or Gradle Java AppHost sits at the build tool's source root rather than next to
            // the project file, so content-based parser detection has to work no matter how deep the
            // AppHost.java source file is nested.
            const appHostPath = p('repo', 'AppHost', 'src', 'main', 'java', 'AppHost.java');
            const content = 'public class AppHost {\n    public static void main(String[] args) {\n        var builder = DistributedApplication.CreateBuilder();\n        builder.addJavaApp("api", "../api").withMavenGoal("spring-boot:run");\n    }\n}';
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.strictEqual(lenses.length, 1);
            harness.dispose();
        });

        test('stays silent for a commented-out launch in Java and languages that have no parser', async () => {
            const cases: ReadonlyArray<readonly [string, string]> = [
                [p('repo', 'AppHost', 'AppHost.java'), 'public class AppHost {\n    public static void main(String[] args) {\n        var builder = DistributedApplication.CreateBuilder();\n        // builder.addJavaApp("api", "../api").withMavenGoal("spring-boot:run");\n        /* builder.addJavaApp("b", "../b").withGradleTask("bootRun"); */\n    }\n}'],
                [p('repo', 'AppHost', 'apphost.py'), '# builder.add_java_app("api", "../api").with_maven_goal("spring-boot:run")'],
                [p('repo', 'AppHost', 'apphost.go'), 'func main() {\n\t// builder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run")\n}'],
            ];

            for (const [appHostPath, content] of cases) {
                const harness = createHarness({ installedExtensions: [springBootDashboard] });
                const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);
                assert.strictEqual(lenses.length, 0, `expected no warning for ${appHostPath}`);
                harness.dispose();
            }
        });

        test('still warns on a live Java launch that follows a commented-out one', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.java');
            const content = [
                'public class AppHost {',
                '    public static void main(String[] args) {',
                '        var builder = DistributedApplication.CreateBuilder();',
                '        // builder.addJavaApp("old", "../old").withMavenGoal("spring-boot:run");',
                '        builder.addJavaApp("api", "../api").withMavenGoal("spring-boot:run");',
                '    }',
                '}',
            ].join('\n');
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.strictEqual(lenses.length, 1);
            assert.strictEqual(lenses[0].range.start.line, 4);
            harness.dispose();
        });

        test('stays silent for a launch quoted inside a Java text block', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.java');
            const content = [
                'public class AppHost {',
                '    public static void main(String[] args) {',
                '        var builder = DistributedApplication.CreateBuilder();',
                '    }',
                '    static final String DOCS = """',
                '        builder.addJavaApp("api", "../api").withMavenGoal("spring-boot:run");',
                '        """;',
                '}',
            ].join('\n');
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.strictEqual(lenses.length, 0);
            harness.dispose();
        });

        test('warns once per line and once per resource', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.cs');
            const content = [
                'var builder = DistributedApplication.CreateBuilder(args);',
                'builder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run");',
                'builder.AddJavaApp("worker", "../worker").WithGradleTask("bootRun");',
            ].join('\n');
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.deepStrictEqual(lenses.map(lens => lens.range.start.line), [1, 2]);
            harness.dispose();
        });

        test('stays silent when the Spring Boot Dashboard is not installed', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.cs');
            const content = [
                'var builder = DistributedApplication.CreateBuilder(args);',
                'builder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run");',
            ].join('\n');
            const harness = createHarness({});

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.strictEqual(lenses.length, 0);
            harness.dispose();
        });

        test('stays silent for build-tool launches that are not Spring Boot', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.cs');
            const content = [
                'var builder = DistributedApplication.CreateBuilder(args);',
                'builder.AddJavaApp("api", "../api").WithMavenGoal("exec:java");',
                'builder.AddJavaApp("worker", "../worker").WithGradleTask("run");',
            ].join('\n');
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.strictEqual(lenses.length, 0);
            harness.dispose();
        });

        test('stays silent for a commented-out launch', async () => {
            // Users leave disabled variants in place while iterating; warning about a resource that
            // does not exist is misleading.
            const cases: ReadonlyArray<readonly [string, readonly string[]]> = [
                [p('repo', 'AppHost', 'AppHost.cs'), [
                    'var builder = DistributedApplication.CreateBuilder(args);',
                    '// builder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run");',
                    '/* builder.AddJavaApp("worker", "../worker").WithGradleTask("bootRun"); */',
                ]],
                [p('repo', 'AppHost', 'apphost.ts'), [
                    'const builder = createBuilder();',
                    "// builder.addJavaApp('api', '../api').withMavenGoal('spring-boot:run');",
                    "/* builder.addJavaApp('worker', '../worker').withGradleTask('bootRun'); */",
                ]],
                [p('repo', 'AppHost', 'apphost.rs'), [
                    'fn main() {',
                    '    let builder = create_builder(None)?;',
                    '    // builder.add_java_app("api", "../api")?.with_maven_goal("spring-boot:run")?;',
                    '    /* builder.add_java_app("worker", "../worker")?.with_gradle_task("bootRun")?; */',
                    '}',
                ]],
            ];

            for (const [appHostPath, lines] of cases) {
                const harness = createHarness({ installedExtensions: [springBootDashboard] });
                const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(lines.join('\n'), appHostPath), cancellationToken) as vscode.CodeLens[]);
                assert.strictEqual(lenses.length, 0, `expected no warning for ${appHostPath}, got ${lenses.length}`);
                harness.dispose();
            }
        });

        test('stays silent for a launch quoted inside a string literal', async () => {
            const cases: ReadonlyArray<readonly [string, string]> = [
                [p('repo', 'AppHost', 'AppHost.cs'),
                    'var builder = DistributedApplication.CreateBuilder(args);\nvar docs = "call .WithMavenGoal(\\"spring-boot:run\\") to use the plugin";'],
                [p('repo', 'AppHost', 'apphost.ts'),
                    'const builder = createBuilder();\nconst docs = `call .withMavenGoal("spring-boot:run") to use the plugin`;'],
                [p('repo', 'AppHost', 'apphost.rs'),
                    'fn main() {\n    let builder = create_builder(None)?;\n    let docs = "call .with_maven_goal(\\"spring-boot:run\\") to use the plugin";\n}'],
            ];

            for (const [appHostPath, content] of cases) {
                const harness = createHarness({ installedExtensions: [springBootDashboard] });
                const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);
                assert.strictEqual(lenses.length, 0, `expected no warning for ${appHostPath}, got ${lenses.length}`);
                harness.dispose();
            }
        });

        test('still warns on a real launch that shares a document with a commented-out one', async () => {
            const appHostPath = p('repo', 'AppHost', 'AppHost.cs');
            const content = [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '// builder.AddJavaApp("old", "../old").WithMavenGoal("spring-boot:run");',
                'builder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run");',
            ].join('\n');
            const harness = createHarness({ installedExtensions: [springBootDashboard] });

            const lenses = springBootLenses(await harness.provider.provideCodeLenses(createMockDocument(content, appHostPath), cancellationToken) as vscode.CodeLens[]);

            assert.deepStrictEqual(lenses.map(lens => lens.range.start.line), [2]);
            harness.dispose();
        });
    });
});
