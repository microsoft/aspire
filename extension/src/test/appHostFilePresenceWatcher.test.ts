import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AppHostFilePresenceWatcher } from '../editor/AppHostFilePresenceWatcher';
import { AppHostDataRepository } from '../views/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
// Import parsers so they self-register before the watcher consults them.
import '../editor/parsers/csharpAppHostParser';
import '../editor/parsers/jsTsAppHostParser';

interface CapturedListeners {
    visibilityListeners: Array<(editors: readonly vscode.TextEditor[]) => void>;
    documentListeners: Array<(event: vscode.TextDocumentChangeEvent) => void>;
}

function makeEditor(filePath: string, content: string): vscode.TextEditor {
    const lines = content.split('\n');
    const document = {
        uri: vscode.Uri.file(filePath),
        fileName: filePath,
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
        getText: () => content,
        getWordRangeAtPosition: () => undefined,
        validateRange: (range: vscode.Range) => range,
        validatePosition: (position: vscode.Position) => position,
        notebook: undefined as any,
        isUntitled: false,
    } as unknown as vscode.TextDocument;
    return { document } as unknown as vscode.TextEditor;
}

const appHostCsContent = 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddRedis("cache");\nbuilder.Build().Run();';
const nonAppHostCsContent = 'using System;\nclass Program { static void Main() { } }';

suite('AppHostFilePresenceWatcher', () => {
    let captured: CapturedListeners;
    let visibleEditorsStub: sinon.SinonStub;
    let onDidChangeVisibleStub: sinon.SinonStub;
    let onDidChangeTextStub: sinon.SinonStub;
    let repository: AppHostDataRepository;
    let setOpenSpy: sinon.SinonSpy;
    let clock: sinon.SinonFakeTimers;
    let visibleEditors: vscode.TextEditor[];

    setup(() => {
        captured = { visibilityListeners: [], documentListeners: [] };
        visibleEditors = [];

        visibleEditorsStub = sinon.stub(vscode.window, 'visibleTextEditors').get(() => visibleEditors);
        onDidChangeVisibleStub = sinon.stub(vscode.window, 'onDidChangeVisibleTextEditors').callsFake(((listener: (editors: readonly vscode.TextEditor[]) => void) => {
            captured.visibilityListeners.push(listener);
            return { dispose: () => { } };
        }) as any);
        onDidChangeTextStub = sinon.stub(vscode.workspace, 'onDidChangeTextDocument').callsFake(((listener: (event: vscode.TextDocumentChangeEvent) => void) => {
            captured.documentListeners.push(listener);
            return { dispose: () => { } };
        }) as any);

        const terminalProvider = new AspireTerminalProvider([]);
        repository = new AppHostDataRepository(terminalProvider);
        setOpenSpy = sinon.spy(repository, 'setAppHostFileOpen');
        clock = sinon.useFakeTimers();
    });

    teardown(() => {
        clock.restore();
        setOpenSpy.restore();
        repository.dispose();
        onDidChangeTextStub.restore();
        onDidChangeVisibleStub.restore();
        visibleEditorsStub.restore();
    });

    test('constructor evaluates visible editors and reports false when none are AppHost files', () => {
        visibleEditors = [makeEditor('/test/Program.cs', nonAppHostCsContent)];

        const watcher = new AppHostFilePresenceWatcher(repository);

        // No call expected because the cached "_lastValue" starts at false.
        assert.strictEqual(setOpenSpy.called, false);
        watcher.dispose();
    });

    test('constructor reports true when an AppHost file is already visible', () => {
        visibleEditors = [makeEditor('/test/AppHost.cs', appHostCsContent)];

        const watcher = new AppHostFilePresenceWatcher(repository);

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.strictEqual(setOpenSpy.firstCall.args[0], true);
        watcher.dispose();
    });

    test('becoming-AppHost transition via visibility change reports true', () => {
        const watcher = new AppHostFilePresenceWatcher(repository);
        assert.strictEqual(setOpenSpy.called, false);

        visibleEditors = [makeEditor('/test/AppHost.cs', appHostCsContent)];
        captured.visibilityListeners.forEach(l => l(visibleEditors));

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.strictEqual(setOpenSpy.firstCall.args[0], true);
        watcher.dispose();
    });

    test('all-non-AppHost transition reports false', () => {
        visibleEditors = [makeEditor('/test/AppHost.cs', appHostCsContent)];
        const watcher = new AppHostFilePresenceWatcher(repository);
        assert.strictEqual(setOpenSpy.lastCall.args[0], true);

        visibleEditors = [makeEditor('/test/Program.cs', nonAppHostCsContent)];
        captured.visibilityListeners.forEach(l => l(visibleEditors));

        assert.strictEqual(setOpenSpy.callCount, 2);
        assert.strictEqual(setOpenSpy.secondCall.args[0], false);
        watcher.dispose();
    });

    test('redundant visibility events with same value do not re-notify', () => {
        visibleEditors = [makeEditor('/test/AppHost.cs', appHostCsContent)];
        const watcher = new AppHostFilePresenceWatcher(repository);
        const initialCalls = setOpenSpy.callCount;

        captured.visibilityListeners.forEach(l => l(visibleEditors));
        captured.visibilityListeners.forEach(l => l(visibleEditors));

        assert.strictEqual(setOpenSpy.callCount, initialCalls);
        watcher.dispose();
    });

    test('document edit on a visible non-AppHost file that becomes AppHost reports true after debounce', () => {
        const editor = makeEditor('/test/Program.cs', nonAppHostCsContent);
        visibleEditors = [editor];
        const watcher = new AppHostFilePresenceWatcher(repository);
        assert.strictEqual(setOpenSpy.called, false);

        // Mutate the document content to look like an AppHost.
        const upgraded = makeEditor('/test/Program.cs', appHostCsContent);
        visibleEditors = [upgraded];
        captured.documentListeners.forEach(l => l({ document: upgraded.document, contentChanges: [], reason: undefined } as any));

        // Listener is debounced; nothing fires yet.
        assert.strictEqual(setOpenSpy.called, false);

        clock.tick(300);

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.strictEqual(setOpenSpy.firstCall.args[0], true);
        watcher.dispose();
    });

    test('document edit on a non-visible document is ignored', () => {
        const visible = makeEditor('/test/Program.cs', nonAppHostCsContent);
        visibleEditors = [visible];
        const watcher = new AppHostFilePresenceWatcher(repository);

        const offscreen = makeEditor('/elsewhere/AppHost.cs', appHostCsContent);
        captured.documentListeners.forEach(l => l({ document: offscreen.document, contentChanges: [], reason: undefined } as any));
        clock.tick(500);

        assert.strictEqual(setOpenSpy.called, false);
        watcher.dispose();
    });

    test('rapid edits coalesce into a single update', () => {
        const editor = makeEditor('/test/AppHost.cs', appHostCsContent);
        visibleEditors = [editor];
        const watcher = new AppHostFilePresenceWatcher(repository);
        const initial = setOpenSpy.callCount;

        // Switch to non-AppHost while firing several rapid edits.
        const downgraded = makeEditor('/test/AppHost.cs', nonAppHostCsContent);
        visibleEditors = [downgraded];
        for (let i = 0; i < 5; i++) {
            captured.documentListeners.forEach(l => l({ document: downgraded.document, contentChanges: [], reason: undefined } as any));
            clock.tick(50);
        }
        clock.tick(300);

        assert.strictEqual(setOpenSpy.callCount, initial + 1);
        assert.strictEqual(setOpenSpy.lastCall.args[0], false);
        watcher.dispose();
    });

    test('dispose removes listeners and pending timer', () => {
        const editor = makeEditor('/test/Program.cs', nonAppHostCsContent);
        visibleEditors = [editor];
        const watcher = new AppHostFilePresenceWatcher(repository);

        const upgraded = makeEditor('/test/Program.cs', appHostCsContent);
        visibleEditors = [upgraded];
        captured.documentListeners.forEach(l => l({ document: upgraded.document, contentChanges: [], reason: undefined } as any));

        watcher.dispose();
        clock.tick(500);

        assert.strictEqual(setOpenSpy.called, false);
    });
});
