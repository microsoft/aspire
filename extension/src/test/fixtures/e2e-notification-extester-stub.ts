import { runInNewContext } from 'vm';

interface NotificationLike {
    getMessage(): Promise<string>;
    dismiss(): Promise<void>;
}

export interface TreeItemLike {
    getLabel(): Promise<string>;
}

export interface TreeSectionLike {
    getTitle(): Promise<string>;
    findItem(label: string, maxLevel?: number): Promise<TreeItemLike | undefined>;
}

export interface TreeRow {
    label: string;
    level: number;
    index?: number;
    visible?: boolean;
}

const state: {
    editorPolls: Array<string[] | Error>;
    codeLensPolls: Array<string[] | Error>;
    lastCodeLensTexts: string[];
    notificationPolls: Array<NotificationLike[] | Error>;
    terminalPolls: Array<string | Error>;
    treeItemPolls: Array<TreeItemLike | undefined | Error>;
    treeSectionPolls: Array<TreeSectionLike[] | Error>;
    treeRowPolls: Array<TreeRow[] | Error> | undefined;
    lastTreeRows: TreeRow[];
    pollResults: unknown[];
    waitMessages: string[];
    notificationPollCount: number;
} = {
    editorPolls: [],
    codeLensPolls: [],
    lastCodeLensTexts: [],
    notificationPolls: [],
    terminalPolls: [],
    treeItemPolls: [],
    treeSectionPolls: [],
    treeRowPolls: undefined,
    lastTreeRows: [],
    pollResults: [],
    waitMessages: [],
    notificationPollCount: 0,
};

export function setTreeItemPolls(polls: Array<TreeItemLike | undefined | Error>): void {
    state.treeItemPolls = [...polls];
    state.pollResults = [];
    state.waitMessages = [];
}

export function setTreeSectionPolls(polls: Array<TreeSectionLike[] | Error>): void {
    state.treeSectionPolls = [...polls];
}

export function setTreeRowPolls(polls: Array<TreeRow[] | Error>): void {
    state.treeRowPolls = [...polls];
    state.lastTreeRows = [];
    state.pollResults = [];
    state.waitMessages = [];
}

export function createTreeItem(label: string | Error): TreeItemLike {
    return {
        getLabel: async () => {
            if (label instanceof Error) {
                throw label;
            }
            return label;
        },
    };
}

export function createTreeSection(title = 'AppHosts'): TreeSectionLike {
    return {
        getTitle: async () => title,
        findItem: async () => {
            const item = state.treeItemPolls.shift();
            if (item instanceof Error) {
                throw item;
            }
            return item;
        },
    };
}

export function setNotificationPolls(notificationPolls: Array<NotificationLike[] | Error>): void {
    state.notificationPolls = [...notificationPolls];
    state.pollResults = [];
    state.waitMessages = [];
    state.notificationPollCount = 0;
}

export function setTerminalPolls(terminalPolls: Array<string | Error>): void {
    state.terminalPolls = [...terminalPolls];
    state.pollResults = [];
    state.waitMessages = [];
}

export function setEditorPolls(editorPolls: Array<string[] | Error>): void {
    state.editorPolls = [...editorPolls];
    state.pollResults = [];
    state.waitMessages = [];
}

/**
 * Each entry is one `getCodeLenses()` result. An `Error` entry stands for the tab not being open yet,
 * which is what `openEditor` throws before VS Code has created it.
 */
export function setCodeLensPolls(codeLensPolls: Array<string[] | Error>): void {
    state.codeLensPolls = [...codeLensPolls];
    state.lastCodeLensTexts = [];
    state.pollResults = [];
    state.waitMessages = [];
}

export function resetNotificationWaitState(): void {
    setEditorPolls([]);
    setCodeLensPolls([]);
    setNotificationPolls([]);
    setTerminalPolls([]);
    setTreeItemPolls([]);
    setTreeSectionPolls([]);
    state.treeRowPolls = undefined;
    state.lastTreeRows = [];
}

export function getNotificationWaitState(): {
    notificationPollCount: number;
    pollResults: unknown[];
    waitMessages: string[];
} {
    return {
        notificationPollCount: state.notificationPollCount,
        pollResults: [...state.pollResults],
        waitMessages: [...state.waitMessages],
    };
}

export class Workbench {
    async getNotifications(): Promise<NotificationLike[]> {
        state.notificationPollCount++;

        if (state.notificationPolls.length === 0) {
            return [];
        }

        const nextPoll = state.notificationPolls.shift();
        if (nextPoll === undefined) {
            return [];
        }

        if (nextPoll instanceof Error) {
            throw nextPoll;
        }

        return nextPoll;
    }
}

export const VSBrowser = {
    instance: {
        driver: {
            wait: async <T>(condition: () => Promise<T | false>, _timeout: number | undefined, message?: string): Promise<T> => {
                state.waitMessages.push(message ?? '');
                const maxAttempts = Math.max(state.editorPolls.length, state.codeLensPolls.length, state.notificationPolls.length, state.terminalPolls.length, state.treeItemPolls.length, state.treeSectionPolls.length, state.treeRowPolls?.length ?? 0, 1) + 1;

                for (let attempt = 0; attempt < maxAttempts; attempt++) {
                    const result = await condition();
                    state.pollResults.push(result);

                    if (result) {
                        return result;
                    }
                }

                throw new Error(message ?? 'Timed out waiting for notification.');
            },
            executeScript: async (script: string, ...args: unknown[]): Promise<unknown> => {
                if (state.treeRowPolls) {
                    const nextPoll = state.treeRowPolls.shift();
                    if (nextPoll instanceof Error) {
                        throw nextPoll;
                    }
                    state.lastTreeRows = nextPoll ?? state.lastTreeRows;
                    return readTreeRows(script, args, state.lastTreeRows);
                }
                // CodeLens polls read widget text. Once the queue is exhausted the last result keeps being
                // returned, which is how a real editor behaves when its lenses stop changing, and
                // it lets a wait time out with the lenses it actually saw.
                const nextPoll = state.codeLensPolls.shift();
                if (nextPoll === undefined) {
                    return state.lastCodeLensTexts;
                }

                if (nextPoll instanceof Error) {
                    throw nextPoll;
                }

                state.lastCodeLensTexts = nextPoll;
                return nextPoll;
            },
            actions: () => ({
                sendKeys: () => ({
                    perform: async (): Promise<void> => { },
                }),
            }),
        },
        waitForWorkbench: async (): Promise<void> => { },
        takeScreenshot: async (): Promise<void> => { },
    },
};

export class BottomBarPanel {
    async openTerminalView(): Promise<{ getCurrentChannel(): Promise<string> }> {
        return {
            getCurrentChannel: async () => {
                const nextPoll = state.terminalPolls.shift() ?? '';
                if (nextPoll instanceof Error) {
                    throw nextPoll;
                }

                return nextPoll;
            },
        };
    }
}

export class SideBarView {
    getContent(): { getSections(): Promise<TreeSectionLike[]> } {
        return {
            getSections: async () => {
                const sections = state.treeSectionPolls.shift() ?? [];
                if (sections instanceof Error) {
                    throw sections;
                }
                return sections;
            },
        };
    }
}

export class EditorView {
    async getOpenEditorTitles(): Promise<string[]> {
        const nextPoll = state.editorPolls.shift() ?? [];
        if (nextPoll instanceof Error) {
            throw nextPoll;
        }

        return nextPoll;
    }

    /**
     * Simulates opening the tab. An `Error` at the head of the queue stands for the tab not
     * existing yet, so it is consumed and thrown. A successful open does not consume the entry:
     * the lens text it holds is read by `executeScript`, which is how the helper reads lenses.
     */
    async openEditor(_title: string): Promise<{ getCodeLenses(): Promise<Array<{ getText(): Promise<string> }>> }> {
        const nextPoll = state.codeLensPolls[0];
        if (nextPoll instanceof Error) {
            state.codeLensPolls.shift();
            throw nextPoll;
        }

        const texts = nextPoll ?? [];
        return {
            getCodeLenses: async () => texts.map(text => ({ getText: async () => text })),
        };
    }
}

export class InputBox {
    static async create(): Promise<never> {
        throw new Error('InputBox.create is not implemented in the notification stub.');
    }
}

export class WebView {
}

export const By = {
    css: (selector: string): string => selector,
};

function readTreeRows(script: string, args: unknown[], rows: TreeRow[]): unknown {
    const pane = {
        getClientRects: () => [{}],
        querySelectorAll: (selector: string) => {
            if (selector !== '.monaco-list-row') {
                throw new Error(`Unexpected tree row selector: ${selector}`);
            }
            return rows.map((row, index) => ({
                getClientRects: () => row.visible === false ? [] : [{}],
                getAttribute: (name: string) => name === 'aria-level' ? String(row.level)
                    : name === 'data-index' ? String(row.index ?? index) : null,
                querySelector: (labelSelector: string) => {
                    if (labelSelector !== '.monaco-highlighted-label') {
                        throw new Error(`Unexpected tree label selector: ${labelSelector}`);
                    }
                    return { textContent: row.label };
                },
            }));
        },
    };
    const document = {
        querySelectorAll: (selector: string) => {
            if (selector !== '.part.sidebar .pane > .pane-header > .title') {
                throw new Error(`Unexpected tree pane selector: ${selector}`);
            }
            return [{ textContent: 'AppHosts', closest: () => pane }];
        },
    };
    // Execute the real browser script against a small DOM fixture, then copy its result across
    // the boundary like WebDriver serialization. Tests must not keep mutable row references.
    return structuredClone(runInNewContext(`(function () { ${script} }).apply(undefined, args)`, { document, args }));
}
