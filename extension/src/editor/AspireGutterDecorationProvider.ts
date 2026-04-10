import * as vscode from 'vscode';
import { getParserForDocument } from './parsers/AppHostResourceParser';
// Trigger parser self-registration
import './parsers/csharpAppHostParser';
import './parsers/jsTsAppHostParser';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { findResourceState, findWorkspaceResourceState } from './resourceStateUtils';
import { ResourceState, StateStyle, HealthStatus } from './resourceConstants';

type GutterCategory = 'running' | 'warning' | 'error' | 'starting' | 'stopped' | 'completed';

const gutterCategories: GutterCategory[] = ['running', 'warning', 'error', 'starting', 'stopped', 'completed'];

/**
 * Creates a data-URI SVG gutter icon for each category.
 * Uses distinct shapes (not just colored dots) so they aren't confused with breakpoints.
 */
function makeGutterSvgUri(category: GutterCategory): vscode.Uri {
    let svg: string;
    switch (category) {
        case 'running':
            // Green checkmark ✅
            svg = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
                <path d="M3 8.5 L6.5 12 L13 4" stroke="#28a745" stroke-width="2.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>`;
            break;
        case 'warning':
            // Yellow warning triangle ⚠️
            svg = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
                <path d="M8 2 L14.5 13 H1.5 Z" fill="#e0a30b" stroke="#e0a30b" stroke-width="0.5"/>
                <text x="8" y="12.5" text-anchor="middle" font-size="9" font-weight="bold" fill="#000" font-family="sans-serif">!</text>
            </svg>`;
            break;
        case 'error':
            // Red X ❌
            svg = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
                <path d="M4 4 L12 12 M12 4 L4 12" stroke="#d73a49" stroke-width="2.5" stroke-linecap="round"/>
            </svg>`;
            break;
        case 'starting':
            // Blue hourglass ⌛
            svg = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
                <path d="M4 2 H12 L8 8 L12 14 H4 L8 8 Z" fill="none" stroke="#2188ff" stroke-width="1.5" stroke-linejoin="round"/>
            </svg>`;
            break;
        case 'stopped':
            // Grey hollow circle (clearly distinct from solid breakpoint dot)
            svg = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
                <circle cx="8" cy="8" r="5.5" fill="none" stroke="#6a737d" stroke-width="1.5"/>
            </svg>`;
            break;
        case 'completed':
            // Pale green checkmark (lighter than running)
            svg = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
                <path d="M3 8.5 L6.5 12 L13 4" stroke="#69d1a0" stroke-width="2.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
            </svg>`;
            break;
    }
    return vscode.Uri.parse(`data:image/svg+xml;utf8,${encodeURIComponent(svg)}`);
}

const decorationTypes = Object.fromEntries(
    gutterCategories.map(c => [c, vscode.window.createTextEditorDecorationType({
        gutterIconPath: makeGutterSvgUri(c),
        gutterIconSize: '70%',
    })])
) as Record<GutterCategory, vscode.TextEditorDecorationType>;

function classifyState(state: string, stateStyle: string, healthStatus: string, exitCode?: number | null): GutterCategory {
    switch (state) {
        case ResourceState.Running:
        case ResourceState.Active:
            if (stateStyle === StateStyle.Error) {
                return 'error';
            }
            if (healthStatus === HealthStatus.Unhealthy || healthStatus === HealthStatus.Degraded || stateStyle === StateStyle.Warning) {
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
            return 'starting';
        case ResourceState.NotStarted:
            return 'stopped';
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.Stopped:
            if (stateStyle === StateStyle.Error || (exitCode != null && exitCode !== 0)) {
                return 'error';
            }
            return 'completed';
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
            const category = classifyState(resource.state ?? '', resource.stateStyle ?? '', resource.healthStatus ?? '', resource.exitCode);
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
