import * as vscode from 'vscode';
import { getParserForDocument } from './parsers/AppHostResourceParser';
import { AppHostDataRepository } from '../views/AppHostDataRepository';

/**
 * Watches the set of visible text editors and reports to {@link AppHostDataRepository}
 * whether at least one of them is an AppHost file.
 *
 * This is what makes code-lens decorations on a freshly-created AppHost file see live
 * resource state without the user first opening the Aspire side panel.
 */
export class AppHostFilePresenceWatcher implements vscode.Disposable {
    private readonly _disposables: vscode.Disposable[] = [];
    private _lastValue = false;

    constructor(private readonly _repository: AppHostDataRepository) {
        this._disposables.push(
            vscode.window.onDidChangeVisibleTextEditors(() => this._update()),
        );
        this._update();
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
        this._disposables.forEach(d => d.dispose());
    }
}
