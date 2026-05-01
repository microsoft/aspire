import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireCodeLensProvider } from '../editor/AspireCodeLensProvider';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { AppHostDataRepository, AppHostDisplayInfo, ResourceJson } from '../views/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
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

function makeResource(name: string): ResourceJson {
    return {
        name,
        displayName: name,
        type: 'container',
        state: 'Running',
        stateStyle: '',
        commands: {},
        endpoints: [],
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
    const treeProvider = new AspireAppHostTreeProvider(repository, terminalProvider);

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

    test('emits builder lenses when document matches a running global AppHost', () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        const doc = createMockDocument(APP_HOST_DOC, docPath);
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 2);
        assert.deepStrictEqual(builderLenses[0].command?.arguments, [hostPath]);
        assert.deepStrictEqual(builderLenses[1].command?.arguments, [hostPath]);
        harness.dispose();
    });

    test('does not emit builder lenses when no AppHost is running', () => {
        const harness = createHarness({});

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('does not emit builder lenses when running AppHost is in an unrelated directory', () => {
        const harness = createHarness({ appHosts: [makeAppHost(p('elsewhere', 'Other.csproj'))] });

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('emits builder lenses for AppHost file with no Add* calls when host is running', () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        const doc = createMockDocument(APP_HOST_NO_RESOURCES, docPath);
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 2);
        harness.dispose();
    });

    test('emits builder lenses for workspace AppHost when document matches workspace path and resources are live', () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({
            workspaceAppHostPath: hostPath,
            workspaceResources: [makeResource('cache')],
        });

        const doc = createMockDocument(APP_HOST_DOC, docPath);
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 2);
        assert.deepStrictEqual(builderLenses[0].command?.arguments, [hostPath]);
        harness.dispose();
    });

    test('does not emit builder lenses when workspaceAppHostPath is set but no workspace resources are live', () => {
        const harness = createHarness({
            workspaceAppHostPath: p('repo', 'AppHost', 'AppHost.csproj'),
            workspaceResources: [],
        });

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('does not emit builder lenses when workspace AppHost is in a different directory', () => {
        const harness = createHarness({
            workspaceAppHostPath: p('elsewhere', 'Other.csproj'),
            workspaceResources: [makeResource('cache')],
        });

        const doc = createMockDocument(APP_HOST_DOC, p('repo', 'AppHost', 'AppHost.cs'));
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const builderLenses = lenses.filter(l =>
            l.command?.command === 'aspire-vscode.codeLensOpenDashboard' ||
            l.command?.command === 'aspire-vscode.codeLensViewAppHostLogs'
        );

        assert.strictEqual(builderLenses.length, 0);
        harness.dispose();
    });

    test('returns empty array for non-AppHost documents', () => {
        const harness = createHarness({ appHosts: [makeAppHost(p('repo', 'AppHost', 'AppHost.csproj'))] });

        const doc = createMockDocument('using System;\nclass Program { }', p('repo', 'AppHost', 'Program.cs'));
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];

        assert.strictEqual(lenses.length, 0);
        harness.dispose();
    });

    test('builder lens points at the builder line, not the resource line', () => {
        const docPath = p('repo', 'AppHost', 'AppHost.cs');
        const hostPath = p('repo', 'AppHost', 'AppHost.csproj');
        const harness = createHarness({ appHosts: [makeAppHost(hostPath)] });

        const doc = createMockDocument(APP_HOST_DOC, docPath);
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
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
