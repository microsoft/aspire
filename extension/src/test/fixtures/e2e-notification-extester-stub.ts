interface NotificationLike {
    getMessage(): Promise<string>;
    dismiss(): Promise<void>;
}

const state: {
    editorPolls: Array<string[] | Error>;
    codeLensPolls: Array<string[] | Error>;
    lastCodeLensTexts: string[];
    notificationPolls: Array<NotificationLike[] | Error>;
    terminalPolls: Array<string | Error>;
    pollResults: Array<NotificationLike | false>;
    waitMessages: string[];
    notificationPollCount: number;
} = {
    editorPolls: [],
    codeLensPolls: [],
    lastCodeLensTexts: [],
    notificationPolls: [],
    terminalPolls: [],
    pollResults: [],
    waitMessages: [],
    notificationPollCount: 0,
};

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
}

export function getNotificationWaitState(): {
    notificationPollCount: number;
    pollResults: Array<NotificationLike | false>;
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
            wait: async (condition: () => Promise<NotificationLike | false>, _timeout: number | undefined, message?: string): Promise<NotificationLike | false> => {
                state.waitMessages.push(message ?? '');
                const maxAttempts = Math.max(state.editorPolls.length, state.codeLensPolls.length, state.notificationPolls.length, state.terminalPolls.length, 1) + 1;

                for (let attempt = 0; attempt < maxAttempts; attempt++) {
                    const result = await condition();
                    state.pollResults.push(result);

                    if (result) {
                        return result;
                    }
                }

                throw new Error(message ?? 'Timed out waiting for notification.');
            },
            executeScript: async (): Promise<string[]> => {
                // The only script the helpers run reads CodeLens widget text, so drain the queued
                // code lens polls here. Once the queue is exhausted the last result keeps being
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
