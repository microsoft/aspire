import * as assert from 'assert';
import { EventEmitter } from 'events';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AppHostFilePresenceWatcher } from '../editor/AppHostFilePresenceWatcher';
import { AppHostDataRepository } from '../views/AppHostDataRepository';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliModule from '../debugger/languages/cli';
// Import parsers so they self-register before the watcher consults them.
import '../editor/parsers/csharpAppHostParser';
import '../editor/parsers/jsTsAppHostParser';

interface CapturedListeners {
    tabsListeners: Array<() => void>;
    visibleEditorsListeners: Array<() => void>;
    activeEditorListeners: Array<() => void>;
    documentListeners: Array<(event: vscode.TextDocumentChangeEvent) => void>;
}

// A minimal stand-in for the CLI child process so `setAppHostFilesOpen` (which the watcher calls for
// real via the spy) can drive ps polling without spawning an actual `aspire` process.
class TestChildProcess extends EventEmitter {
    public killed = false;
    public stdout = new EventEmitter();
    public stderr = new EventEmitter();
    kill(): boolean {
        this.killed = true;
        return true;
    }
}

function makeDocument(filePath: string, content: string): vscode.TextDocument {
    const lines = content.split('\n');
    return {
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
}

const appHostCsContent = 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddRedis("cache");\nbuilder.Build().Run();';
const appHostTsContent = 'import { createBuilder } from "@aspire/sdk";\nconst builder = await createBuilder();\nawait builder.addRedis("cache");';
const nonAppHostCsContent = 'using System;\nclass Program { static void Main() { } }';

function fsPath(path: string): string {
    return vscode.Uri.file(path).fsPath;
}

suite('AppHostFilePresenceWatcher', () => {
    let captured: CapturedListeners;
    let tabGroupsStub: sinon.SinonStub;
    let onDidChangeActiveStub: sinon.SinonStub;
    let onDidChangeVisibleStub: sinon.SinonStub;
    let visibleEditorsStub: sinon.SinonStub;
    let onDidChangeTextStub: sinon.SinonStub;
    let textDocumentsStub: sinon.SinonStub;
    let spawnStub: sinon.SinonStub;
    let repository: AppHostDataRepository;
    let setOpenSpy: sinon.SinonSpy;
    let clock: sinon.SinonFakeTimers;
    let tabs: vscode.Tab[];
    let textDocuments: vscode.TextDocument[];
    let visibleDocuments: vscode.TextDocument[];

    // `visible` defaults to all open documents (the common case: opening a file makes it visible).
    // Pass an explicit list — often `[]` — to model backgrounded tabs whose editors are not on screen.
    function setOpenDocuments(documents: vscode.TextDocument[], visible: vscode.TextDocument[] = documents): void {
        textDocuments = documents;
        visibleDocuments = visible;
        tabs = documents.map(document => ({ input: { uri: document.uri } }) as unknown as vscode.Tab);
    }

    setup(() => {
        captured = { tabsListeners: [], visibleEditorsListeners: [], activeEditorListeners: [], documentListeners: [] };
        tabs = [];
        textDocuments = [];
        visibleDocuments = [];

        tabGroupsStub = sinon.stub(vscode.window, 'tabGroups').get(() => ({
            get all() { return [{ tabs }]; },
            onDidChangeTabs: ((listener: () => void) => {
                captured.tabsListeners.push(listener);
                return { dispose: () => { } };
            }) as any,
        }));
        visibleEditorsStub = sinon.stub(vscode.window, 'visibleTextEditors').get(
            () => visibleDocuments.map(document => ({ document }) as unknown as vscode.TextEditor));
        onDidChangeVisibleStub = sinon.stub(vscode.window, 'onDidChangeVisibleTextEditors').callsFake(((listener: () => void) => {
            captured.visibleEditorsListeners.push(listener);
            return { dispose: () => { } };
        }) as any);
        onDidChangeActiveStub = sinon.stub(vscode.window, 'onDidChangeActiveTextEditor').callsFake(((listener: () => void) => {
            captured.activeEditorListeners.push(listener);
            return { dispose: () => { } };
        }) as any);
        onDidChangeTextStub = sinon.stub(vscode.workspace, 'onDidChangeTextDocument').callsFake(((listener: (event: vscode.TextDocumentChangeEvent) => void) => {
            captured.documentListeners.push(listener);
            return { dispose: () => { } };
        }) as any);
        textDocumentsStub = sinon.stub(vscode.workspace, 'textDocuments').get(() => textDocuments);
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake(() => new TestChildProcess() as any);

        const terminalProvider = new AspireTerminalProvider([]);
        sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        repository = new AppHostDataRepository(terminalProvider);
        setOpenSpy = sinon.spy(repository, 'setAppHostFilesOpen');
        clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
    });

    teardown(() => {
        clock.restore();
        setOpenSpy.restore();
        repository.dispose();
        spawnStub.restore();
        textDocumentsStub.restore();
        onDidChangeTextStub.restore();
        onDidChangeActiveStub.restore();
        onDidChangeVisibleStub.restore();
        visibleEditorsStub.restore();
        tabGroupsStub.restore();
    });

    function reportedPaths(call: sinon.SinonSpyCall): string[] {
        return (call.args[0] as readonly string[]).slice().sort();
    }

    test('does not report when no open tab is an AppHost file', async () => {
        setOpenDocuments([makeDocument('/test/Program.cs', nonAppHostCsContent)]);

        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);

        // The initial reported set and the resolved set are both empty, so no notification fires.
        assert.strictEqual(setOpenSpy.called, false);
        watcher.dispose();
    });

    test('reports the AppHost path when a tab is already open', async () => {
        setOpenDocuments([makeDocument('/test/AppHost.cs', appHostCsContent)]);

        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.deepStrictEqual(reportedPaths(setOpenSpy.firstCall), [fsPath('/test/AppHost.cs')]);
        watcher.dispose();
    });

    test('reports a backgrounded open AppHost tab whose editor is not visible', async () => {
        setOpenDocuments([makeDocument('/test/AppHost.cs', appHostCsContent)], []);

        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.deepStrictEqual(reportedPaths(setOpenSpy.firstCall), [fsPath('/test/AppHost.cs')]);
        watcher.dispose();
    });

    test('reports every open AppHost tab as a single set', async () => {
        setOpenDocuments([
            makeDocument('/test/AppHost.cs', appHostCsContent),
            makeDocument('/other/apphost.ts', appHostTsContent),
            makeDocument('/test/Program.cs', nonAppHostCsContent),
        ]);

        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.deepStrictEqual(reportedPaths(setOpenSpy.firstCall), [fsPath('/other/apphost.ts'), fsPath('/test/AppHost.cs')]);
        watcher.dispose();
    });

    test('opening a new AppHost tab reports the added path', async () => {
        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);
        assert.strictEqual(setOpenSpy.called, false);

        setOpenDocuments([makeDocument('/test/AppHost.cs', appHostCsContent)]);
        captured.tabsListeners.forEach(l => l());
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.deepStrictEqual(reportedPaths(setOpenSpy.firstCall), [fsPath('/test/AppHost.cs')]);
        watcher.dispose();
    });

    test('a visible-editor change that leaves the open tab set unchanged does not re-report', async () => {
        const document = makeDocument('/test/AppHost.cs', appHostCsContent);
        setOpenDocuments([document]);
        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);
        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.deepStrictEqual(reportedPaths(setOpenSpy.firstCall), [fsPath('/test/AppHost.cs')]);

        visibleDocuments = [];
        captured.visibleEditorsListeners.forEach(l => l());
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.callCount, 1, 'a visibility-only change must not re-report the unchanged open tab set');
        watcher.dispose();
    });

    test('closing the last AppHost tab reports an empty set', async () => {
        setOpenDocuments([makeDocument('/test/AppHost.cs', appHostCsContent)]);
        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);
        assert.deepStrictEqual(reportedPaths(setOpenSpy.lastCall), [fsPath('/test/AppHost.cs')]);

        setOpenDocuments([makeDocument('/test/Program.cs', nonAppHostCsContent)]);
        captured.tabsListeners.forEach(l => l());
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.callCount, 2);
        assert.deepStrictEqual(setOpenSpy.secondCall.args[0], []);
        watcher.dispose();
    });

    test('redundant tab events with the same open set do not re-notify', async () => {
        setOpenDocuments([makeDocument('/test/AppHost.cs', appHostCsContent)]);
        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);
        const initialCalls = setOpenSpy.callCount;

        captured.tabsListeners.forEach(l => l());
        captured.tabsListeners.forEach(l => l());
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.callCount, initialCalls);
        watcher.dispose();
    });

    test('editing a backgrounded tab that becomes an AppHost reports the path after debounce', async () => {
        setOpenDocuments([makeDocument('/test/Program.cs', nonAppHostCsContent)], []);
        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);
        assert.strictEqual(setOpenSpy.called, false);

        // The backgrounded document's content is edited into an AppHost (same tab/uri, new content).
        const upgraded = makeDocument('/test/Program.cs', appHostCsContent);
        textDocuments = [upgraded];
        captured.documentListeners.forEach(l => l({ document: upgraded, contentChanges: [], reason: undefined } as any));

        // The content listener is debounced; nothing fires until the debounce elapses.
        assert.strictEqual(setOpenSpy.called, false);

        await clock.tickAsync(AppHostFilePresenceWatcher['_changeDebounceMs']);
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.calledOnce, true);
        assert.deepStrictEqual(reportedPaths(setOpenSpy.firstCall), [fsPath('/test/Program.cs')]);
        watcher.dispose();
    });

    test('editing a document that has no open tab is ignored', async () => {
        setOpenDocuments([makeDocument('/test/Program.cs', nonAppHostCsContent)]);
        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);

        // The edited AppHost document has no corresponding open tab, so the watcher must not react.
        const untracked = makeDocument('/elsewhere/AppHost.cs', appHostCsContent);
        captured.documentListeners.forEach(l => l({ document: untracked, contentChanges: [], reason: undefined } as any));
        await clock.tickAsync(500);

        assert.strictEqual(setOpenSpy.called, false);
        watcher.dispose();
    });

    test('rapid edits coalesce into a single update', async () => {
        setOpenDocuments([makeDocument('/test/AppHost.cs', appHostCsContent)]);
        const watcher = new AppHostFilePresenceWatcher(repository);
        await waitForUpdate(watcher);
        const initial = setOpenSpy.callCount;

        // The document is edited down to a non-AppHost while several rapid edits fire.
        const downgraded = makeDocument('/test/AppHost.cs', nonAppHostCsContent);
        textDocuments = [downgraded];
        for (let i = 0; i < 5; i++) {
            captured.documentListeners.forEach(l => l({ document: downgraded, contentChanges: [], reason: undefined } as any));
            await clock.tickAsync(50);
        }
        await clock.tickAsync(AppHostFilePresenceWatcher['_changeDebounceMs']);
        await waitForUpdate(watcher);

        assert.strictEqual(setOpenSpy.callCount, initial + 1);
        assert.deepStrictEqual(setOpenSpy.lastCall.args[0], []);
        watcher.dispose();
    });

    test('dispose removes listeners and pending timer', () => {
        setOpenDocuments([makeDocument('/test/Program.cs', nonAppHostCsContent)]);
        const watcher = new AppHostFilePresenceWatcher(repository);

        const upgraded = makeDocument('/test/Program.cs', appHostCsContent);
        textDocuments = [upgraded];
        captured.documentListeners.forEach(l => l({ document: upgraded, contentChanges: [], reason: undefined } as any));

        watcher.dispose();
        clock.tick(500);

        assert.strictEqual(setOpenSpy.called, false);
    });
});

async function waitForUpdate(watcher: AppHostFilePresenceWatcher): Promise<void> {
    await (watcher as unknown as { _updateTask: Promise<void> })._updateTask;
}
