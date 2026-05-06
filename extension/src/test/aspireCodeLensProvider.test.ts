/// <reference types="mocha" />

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

    test('does not emit resource lenses from a different running AppHost', () => {
        const runningHostPath = p('repo', 'RunningAppHost', 'AppHost.csproj');
        const stoppedHostPath = p('repo', 'StoppedAppHost', 'AppHost.cs');
        const runningAppHost = {
            ...makeAppHost(runningHostPath),
            resources: [makeResource('cache')],
        };
        const harness = createHarness({ appHosts: [runningAppHost] });

        const doc = createMockDocument(APP_HOST_DOC, stoppedHostPath);
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const resourceLenses = lenses.filter(l =>
            l.command?.command !== 'aspire-vscode.codeLensOpenDashboard'
            && l.command?.command !== 'aspire-vscode.codeLensViewAppHostLogs'
            && l.command?.command !== 'aspire-vscode.codeLensDebugPipelineStep'
        );

        assert.strictEqual(resourceLenses.length, 0);
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

    test('single-resource fluent chain anchors lens at the statement-start line, not the .add* call line', () => {
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
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
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

    test('multi-resource fluent chain anchors each resource lens at its own .add* call line', () => {
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
        const lenses = harness.provider.provideCodeLenses(doc, cancellationToken) as vscode.CodeLens[];
        const resourceLenses = getResourceLenses(lenses);

        const lines = new Set(resourceLenses.map(l => l.range.start.line));
        assert.ok(
            lines.has(2) && lines.has(3),
            `expected resource lenses on both line 2 (pg) and line 3 (db) so they don't stack; got lines [${[...lines].join(', ')}]`
        );
        harness.dispose();
    });
});
