import * as vscode from 'vscode';
import { getParserForDocument, getAllParsers, getFileExtension } from './parsers/AppHostResourceParser';
import { AppHostDataRepository } from '../views/AppHostDataRepository';
import { projectContentsReferencesRunnableAspireAppHost } from '../utils/appHostLanguage';

/**
 * Watches the set of open editor tabs and reports to {@link AppHostDataRepository}
 * which ones are AppHost files.
 *
 * This is what makes code-lens decorations on a freshly-created AppHost file see live
 * resource state without the user first opening the Aspire side panel.
 *
 * The watcher reports every open AppHost tab (focused or backgrounded). The set of open AppHost tabs
 * drives both which running hosts surface in the workspace panel and whether the data sources stay
 * alive.
 *
 * The watcher reacts to:
 *  - tab open/close/activate (`onDidChangeTabs`),
 *  - visible editor changes (`onDidChangeVisibleTextEditors`) so a tab whose document only loads
 *    when it is first shown is picked up,
 *  - active editor changes (`onDidChangeActiveTextEditor`) so a newly-loaded document is seen,
 *  - text edits to an open tab's document (`onDidChangeTextDocument`, debounced),
 * so that toggling AppHost-ness by editing the file (e.g. adding the
 * `DistributedApplication.CreateBuilder` call or the `#:sdk Aspire.AppHost.Sdk`
 * directive) is picked up without requiring a save or focus change.
 */
export class AppHostFilePresenceWatcher implements vscode.Disposable {
    private static readonly _changeDebounceMs = 250;
    private static readonly _projectAppHostExtensions = new Set(['.csproj', '.fsproj', '.vbproj']);

    private readonly _disposables: vscode.Disposable[] = [];
    private _lastReportedOpenPaths: readonly string[] = [];
    private _changeTimer: NodeJS.Timeout | undefined;
    private _updateVersion = 0;
    private _updateTask: Promise<void> = Promise.resolve();

    constructor(private readonly _repository: AppHostDataRepository) {
        this._disposables.push(
            vscode.window.tabGroups.onDidChangeTabs(() => this._queueUpdate()),
            vscode.window.onDidChangeVisibleTextEditors(() => this._queueUpdate()),
            vscode.window.onDidChangeActiveTextEditor(() => this._queueUpdate()),
            vscode.workspace.onDidChangeTextDocument(e => this._onTextDocumentChanged(e)),
        );
        this._queueUpdate();
    }

    private _onTextDocumentChanged(event: vscode.TextDocumentChangeEvent): void {
        const uri = event.document.uri;
        if (!this._isOpenAppHostCandidateUri(uri)) {
            return;
        }
        this._scheduleUpdate();
    }

    private _isOpenAppHostCandidateUri(uri: vscode.Uri): boolean {
        return AppHostFilePresenceWatcher._openAppHostExtensions().has(getFileExtension(uri.fsPath))
            && this._openTabUris().some(openUri => openUri.toString() === uri.toString());
    }

    private _openTabUris(): vscode.Uri[] {
        const uris: vscode.Uri[] = [];
        for (const group of vscode.window.tabGroups.all) {
            for (const tab of group.tabs) {
                const input = tab.input as { uri?: vscode.Uri } | undefined;
                if (input?.uri instanceof vscode.Uri) {
                    uris.push(input.uri);
                }
            }
        }
        return uris;
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
        const openPaths = await this._openAppHostPaths();
        if (version !== this._updateVersion) {
            return;
        }

        if (openPathsEqual(this._lastReportedOpenPaths, openPaths)) {
            return;
        }
        this._lastReportedOpenPaths = openPaths;
        this._repository.setAppHostFilesOpen(openPaths);
    }

    private async _openAppHostPaths(): Promise<string[]> {
        // Every open AppHost *tab* (focused or backgrounded), deduped by fsPath.
        const supportedExtensions = AppHostFilePresenceWatcher._openAppHostExtensions();
        const seenPaths = new Set<string>();
        const openPaths: string[] = [];
        for (const uri of this._openTabUris()) {
            // Pre-filter by extension (and skip files already seen in another split) so we never
            // force-load unrelated tabs (logs, images, ...) just to ask the parser about them.
            if (seenPaths.has(uri.fsPath) || !supportedExtensions.has(getFileExtension(uri.fsPath))) {
                continue;
            }
            // Only consider documents VS Code has already loaded; never force-open a tab's
            // document just to inspect it.
            const document = vscode.workspace.textDocuments.find(d => d.uri.toString() === uri.toString());
            if (document && await this._isOpenAppHostDocument(document)) {
                seenPaths.add(document.uri.fsPath);
                openPaths.push(document.uri.fsPath);
            }
        }
        return openPaths;
    }

    private async _isOpenAppHostDocument(document: vscode.TextDocument): Promise<boolean> {
        if (await getParserForDocument(document)) {
            return true;
        }

        // A project AppHost (.csproj/.fsproj/.vbproj) has no inline resources to parse, but opening it
        // should still surface and follow its running instance.
        if (AppHostFilePresenceWatcher._projectAppHostExtensions.has(getFileExtension(document.uri.fsPath))) {
            return projectContentsReferencesRunnableAspireAppHost(document.getText());
        }

        return false;
    }

    // Extensions handled by the language parsers (source AppHosts: .cs/.ts/.js).
    private static _supportedAppHostExtensions(): Set<string> {
        const extensions = new Set<string>();
        for (const parser of getAllParsers()) {
            for (const ext of parser.getSupportedExtensions()) {
                extensions.add(ext.toLowerCase());
            }
        }
        return extensions;
    }

    // The full set of tab extensions that can represent an open AppHost: source files handled by the
    // language parsers plus AppHost project files. Used to pre-filter tabs before loading their content.
    private static _openAppHostExtensions(): Set<string> {
        const extensions = AppHostFilePresenceWatcher._supportedAppHostExtensions();
        for (const ext of AppHostFilePresenceWatcher._projectAppHostExtensions) {
            extensions.add(ext);
        }
        return extensions;
    }

    dispose(): void {
        if (this._changeTimer) {
            clearTimeout(this._changeTimer);
            this._changeTimer = undefined;
        }
        this._disposables.forEach(d => d.dispose());
    }
}

function openPathsEqual(left: readonly string[], right: readonly string[]): boolean {
    return left.length === right.length && left.every(path => right.includes(path));
}
