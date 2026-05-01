import * as vscode from 'vscode';
import { getParserForDocument } from './parsers/AppHostResourceParser';
import { AppHostDataRepository } from '../views/AppHostDataRepository';

/**
 * Watches the set of visible text editors and reports to {@link AppHostDataRepository}
 * whether at least one of them is an AppHost file.
 *
 * This is what makes code-lens decorations on a freshly-created AppHost file see live
 * resource state without the user first opening the Aspire side panel.
 *
 * The watcher reacts to:
 *  - editor visibility changes (`onDidChangeVisibleTextEditors`),
 *  - text edits to a visible document (`onDidChangeTextDocument`, debounced),
 * so that toggling AppHost-ness by editing the file (e.g. adding the
 * `DistributedApplication.CreateBuilder` call or the `#:sdk Aspire.AppHost.Sdk`
 * directive) is picked up without requiring a save or focus change.
 */
export class AppHostFilePresenceWatcher implements vscode.Disposable {
    private static readonly _changeDebounceMs = 250;

    private readonly _disposables: vscode.Disposable[] = [];
    private _lastValue = false;
    private _changeTimer: NodeJS.Timeout | undefined;

    constructor(private readonly _repository: AppHostDataRepository) {
        this._disposables.push(
            vscode.window.onDidChangeVisibleTextEditors(() => this._update()),
            vscode.workspace.onDidChangeTextDocument(e => this._onTextDocumentChanged(e)),
        );
        this._update();
    }

    private _onTextDocumentChanged(event: vscode.TextDocumentChangeEvent): void {
        // Only re-evaluate when the changed document is currently visible — edits to
        // background documents cannot change the visible-AppHost state.
        const isVisible = vscode.window.visibleTextEditors.some(editor => editor.document === event.document);
        if (!isVisible) {
            return;
        }
        this._scheduleUpdate();
    }

    private _scheduleUpdate(): void {
        if (this._changeTimer) {
            clearTimeout(this._changeTimer);
        }
        this._changeTimer = setTimeout(() => {
            this._changeTimer = undefined;
            this._update();
        }, AppHostFilePresenceWatcher._changeDebounceMs);
    }

    private _update(): void {
        const value = this._anyVisibleEditorIsAppHost();
        if (value === this._lastValue) {
            return;
        }
        this._lastValue = value;
        this._repository.setAppHostFileOpen(value);
    }

    private _anyVisibleEditorIsAppHost(): boolean {
        for (const editor of vscode.window.visibleTextEditors) {
            if (getParserForDocument(editor.document)) {
                return true;
            }
        }
        return false;
    }

    dispose(): void {
        if (this._changeTimer) {
            clearTimeout(this._changeTimer);
            this._changeTimer = undefined;
        }
        this._disposables.forEach(d => d.dispose());
    }
}
