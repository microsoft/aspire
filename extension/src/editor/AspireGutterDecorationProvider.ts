import * as vscode from 'vscode';
import { getParserForDocument } from './parsers/AppHostResourceParser';
// Trigger parser self-registration
import './parsers/csharpAppHostParser';
import './parsers/jsTsAppHostParser';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { findResourceState, findWorkspaceResourceState } from './resourceStateUtils';
import { ResourceState, StateStyle, HealthStatus } from './resourceConstants';

type GutterCategory = 'running' | 'warning' | 'error' | 'starting' | 'stopped';

const gutterCategories: GutterCategory[] = ['running', 'warning', 'error', 'starting', 'stopped'];

const gutterColors: Record<GutterCategory, string> = {
    running: '#28a745',  // green
    warning: '#e0a30b',  // yellow/amber
    error: '#d73a49',    // red
    starting: '#2188ff', // blue
    stopped: '#6a737d',  // gray
};

/** Creates a data-URI SVG of a filled circle with the given color. */
function makeGutterSvgUri(color: string): vscode.Uri {
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><circle cx="8" cy="8" r="6" fill="${color}"/></svg>`;
    return vscode.Uri.parse(`data:image/svg+xml;utf8,${encodeURIComponent(svg)}`);
}

const decorationTypes = Object.fromEntries(
    gutterCategories.map(c => [c, vscode.window.createTextEditorDecorationType({
        gutterIconPath: makeGutterSvgUri(gutterColors[c]),
        gutterIconSize: '70%',
    })])
) as Record<GutterCategory, vscode.TextEditorDecorationType>;

function classifyState(state: string, stateStyle: string, healthStatus: string): GutterCategory {
    switch (state) {
        case ResourceState.Running:
        case ResourceState.Active:
            if (stateStyle === StateStyle.Error || healthStatus === HealthStatus.Unhealthy) {
                return 'error';
            }
            if (stateStyle === StateStyle.Warning || healthStatus === HealthStatus.Degraded) {
                return 'warning';
            }
            return 'running';
        case ResourceState.FailedToStart:
        case ResourceState.RuntimeUnhealthy:
            return 'error';
        case ResourceState.Starting:
        case ResourceState.Stopping:
        case ResourceState.Building:
        case ResourceState.Waiting:
        case ResourceState.NotStarted:
            return 'starting';
        case ResourceState.Finished:
        case ResourceState.Exited:
            return stateStyle === StateStyle.Error ? 'error' : 'stopped';
        default:
            return 'stopped';
    }
}

export class AspireGutterDecorationProvider implements vscode.Disposable {
    private readonly _disposables: vscode.Disposable[] = [];
    private _debounceTimer: ReturnType<typeof setTimeout> | undefined;

    constructor(private readonly _treeProvider: AspireAppHostTreeProvider) {
        this._disposables.push(
            _treeProvider.onDidChangeTreeData(() => this._updateAllVisibleEditors()),
            vscode.window.onDidChangeActiveTextEditor(() => this._updateAllVisibleEditors()),
            vscode.workspace.onDidChangeTextDocument(e => {
                this._debouncedUpdate(e.document);
            }),
        );

        // Apply immediately for any already-open editors
        this._updateAllVisibleEditors();
    }

    private _debouncedUpdate(document: vscode.TextDocument): void {
        if (this._debounceTimer) {
            clearTimeout(this._debounceTimer);
        }
        this._debounceTimer = setTimeout(() => {
            this._debounceTimer = undefined;
            for (const editor of vscode.window.visibleTextEditors) {
                if (editor.document === document) {
                    this._applyDecorations(editor);
                }
            }
        }, 250);
    }

    private _updateAllVisibleEditors(): void {
        for (const editor of vscode.window.visibleTextEditors) {
            this._applyDecorations(editor);
        }
    }

    private _applyDecorations(editor: vscode.TextEditor): void {
        if (!vscode.workspace.getConfiguration('aspire').get<boolean>('enableGutterDecorations', true)) {
            this._clearDecorations(editor);
            return;
        }

        const parser = getParserForDocument(editor.document);
        if (!parser) {
            this._clearDecorations(editor);
            return;
        }

        const appHosts = this._treeProvider.appHosts;
        const workspaceResources = this._treeProvider.workspaceResources;
        if (appHosts.length === 0 && workspaceResources.length === 0) {
            this._clearDecorations(editor);
            return;
        }

        const resources = parser.parseResources(editor.document);
        if (resources.length === 0) {
            this._clearDecorations(editor);
            return;
        }

        const findWorkspace = findWorkspaceResourceState(workspaceResources, this._treeProvider.workspaceAppHostPath ?? '');
        const buckets = new Map<GutterCategory, vscode.DecorationOptions[]>(
            gutterCategories.map(c => [c, []])
        );

        for (const parsed of resources) {
            if (parsed.kind !== 'resource') {
                continue;
            }
            const match = findResourceState(appHosts, parsed.name)
                ?? findWorkspace(parsed.name);
            if (!match) {
                continue;
            }

            const { resource } = match;
            const category = classifyState(resource.state ?? '', resource.stateStyle ?? '', resource.healthStatus ?? '');
            buckets.get(category)!.push({ range: editor.document.lineAt(parsed.range.start.line).range });
        }

        for (const [category, options] of buckets) {
            editor.setDecorations(decorationTypes[category], options);
        }
    }

    private _clearDecorations(editor: vscode.TextEditor): void {
        for (const type of Object.values(decorationTypes)) {
            editor.setDecorations(type, []);
        }
    }

    dispose(): void {
        if (this._debounceTimer) {
            clearTimeout(this._debounceTimer);
        }
        this._disposables.forEach(d => d.dispose());
        for (const type of Object.values(decorationTypes)) {
            type.dispose();
        }
    }
}
