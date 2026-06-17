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
    private _updateVersion = 0;
    private _updateTask: Promise<void> = Promise.resolve();

    constructor(private readonly _repository: AppHostDataRepository) {
        this._disposables.push(
            vscode.window.onDidChangeVisibleTextEditors(() => this._queueUpdate()),
            vscode.workspace.onDidChangeTextDocument(e => this._onTextDocumentChanged(e)),
        );
        this._queueUpdate();
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
            this._queueUpdate();
        }, AppHostFilePresenceWatcher._changeDebounceMs);
    }

    private _queueUpdate(): void {
        const version = ++this._updateVersion;
        this._updateTask = this._update(version);
    }

    private async _update(version: number): Promise<void> {
        const value = await this._anyVisibleEditorIsAppHost();
        if (version !== this._updateVersion) {
            return;
        }

        if (value === this._lastValue) {
            return;
        }
        this._lastValue = value;
        this._repository.setAppHostFileOpen(value);
    }

    private async _anyVisibleEditorIsAppHost(): Promise<boolean> {
        for (const editor of vscode.window.visibleTextEditors) {
            if (await getParserForDocument(editor.document)) {
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
