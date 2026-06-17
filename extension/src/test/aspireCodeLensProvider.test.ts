/// <reference types="mocha" />

import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireCodeLensProvider } from '../editor/AspireCodeLensProvider';
import { AspireGutterDecorationProvider } from '../editor/AspireGutterDecorationProvider';
import * as AppHostResourceParser from '../editor/parsers/AppHostResourceParser';
import { ParsedResource } from '../editor/parsers/AppHostResourceParser';
import { codeLensCommand, codeLensResourceValueMissing } from '../loc/strings';
import { ResourceState, ResourceType } from '../editor/resourceConstants';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { AppHostDataRepository, AppHostDisplayInfo, ResourceJson } from '../views/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
// Import parsers so they self-register before the provider consults them.
import '../editor/parsers/csharpAppHostParser';
import '../editor/parsers/jsTsAppHostParser';

// Build platform-native paths so dirname comparison works on Windows too
// (vscode.Uri.file('/foo/bar').fsPath becomes '\\foo\\bar' on Windows, so we
// need the host paths to use the same separator).
function p(...segments: string[]): string {
    return path.join(path.sep, ...segments);
}

function createMockDocument(content: string, filePath: string): vscode.TextDocument {
    const lines = content.split('\n');
    return {
        uri: vscode.Uri.file(filePath),
        fileName: filePath,
        isUntitled: false,
        languageId: filePath.endsWith('.cs') ? 'csharp' : filePath.endsWith('.ts') ? 'typescript' : 'javascript',
        version: 1,
        isDirty: false,
        isClosed: false,
        eol: vscode.EndOfLine.LF,
        lineCount: lines.length,
        encoding: 'utf-8',
        save: () => Promise.resolve(false),
        lineAt: (lineOrPos: number | vscode.Position) => {
            const lineNum = typeof lineOrPos === 'number' ? lineOrPos : lineOrPos.line;
            const text = lines[lineNum] || '';
            return {
                lineNumber: lineNum,
                text,
                range: new vscode.Range(lineNum, 0, lineNum, text.length),
                rangeIncludingLineBreak: new vscode.Range(lineNum, 0, lineNum + 1, 0),
                firstNonWhitespaceCharacterIndex: text.search(/\S/),
                isEmptyOrWhitespace: text.trim().length === 0,
            } as vscode.TextLine;
        },
        offsetAt: (position: vscode.Position) => {
            let offset = 0;
            for (let i = 0; i < position.line && i < lines.length; i++) {
                offset += lines[i].length + 1;
            }
            return offset + position.character;
        },
        positionAt: (offset: number) => {
            let remaining = offset;
            for (let i = 0; i < lines.length; i++) {
                if (remaining <= lines[i].length) {
                    return new vscode.Position(i, remaining);
                }
                remaining -= lines[i].length + 1;
            }
            return new vscode.Position(lines.length - 1, lines[lines.length - 1].length);
        },
        getText: (range?: vscode.Range) => {
            if (!range) {
                return content;
            }
            const startOffset = lines.slice(0, range.start.line).reduce((sum, l) => sum + l.length + 1, 0) + range.start.character;
            const endOffset = lines.slice(0, range.end.line).reduce((sum, l) => sum + l.length + 1, 0) + range.end.character;
            return content.substring(startOffset, endOffset);
        },
        getWordRangeAtPosition: () => undefined,
        validateRange: (range: vscode.Range) => range,
        validatePosition: (position: vscode.Position) => position,
        notebook: undefined as any,
    } as vscode.TextDocument;
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
}): TestHarness {
    const subs: vscode.Disposable[] = [];
    const terminalProvider = new AspireTerminalProvider(subs);
    const repository = new AppHostDataRepository(terminalProvider);
    const treeProvider = new AspireAppHostTreeProvider(repository, terminalProvider, new AppHostLaunchService());

    const appHostsStub = sinon.stub(repository, 'appHosts').get(() => opts.appHosts ?? []);
    const workspaceResourcesStub = sinon.stub(repository, 'workspaceResources').get(() => opts.workspaceResources ?? []);
    const workspaceAppHostPathStub = sinon.stub(repository, 'workspaceAppHostPath').get(() => opts.workspaceAppHostPath);

    const provider = new AspireCodeLensProvider(treeProvider, repository);

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
});
