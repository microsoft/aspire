import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { error as webDriverError } from 'selenium-webdriver';

interface NotificationLike {
    getMessage(): Promise<string>;
    dismiss(): Promise<void>;
}

interface NotificationExtesterStubModule {
    getNotificationWaitState(): {
        notificationPollCount: number;
        pollResults: Array<NotificationLike | false>;
        waitMessages: string[];
    };
    resetNotificationWaitState(): void;
    setEditorPolls(editorPolls: Array<string[] | Error>): void;
    setCodeLensPolls(codeLensPolls: Array<string[] | Error>): void;
    setNotificationPolls(notificationPolls: Array<NotificationLike[] | Error>): void;
    setTerminalPolls(terminalPolls: Array<string | Error>): void;
}

interface VscodeHelpersModule {
    dismissAllNotifications(timeoutMs?: number): Promise<void>;
    waitForCodeLensText(fileName: string, expectedText: string, timeoutMs?: number): Promise<string[]>;
    waitForEditorTitle(expectedText: string, timeoutMs?: number): Promise<string>;
    waitForNotificationMessage(expectedText: string, timeoutMs?: number): Promise<NotificationLike>;
    waitForTerminalChannel(expectedText: string, timeoutMs?: number): Promise<string>;
}

const extensionRoot = path.resolve(__dirname, '..', '..');
const extesterStubModulePath = path.join(extensionRoot, 'out', 'test', 'fixtures', 'e2e-notification-extester-stub.js');
const compiledE2eHelpersPath = path.join(extensionRoot, 'out', 'test-e2e', 'helpers');
const compiledExtesterModulePath = path.join(compiledE2eHelpersPath, 'extester.js');
const compiledVscodeHelpersModulePath = path.join(compiledE2eHelpersPath, 'vscode.js');
const e2eBridgeEnvironmentVariables = ['ASPIRE_EXTENSION_E2E_CONTROL_FILE', 'ASPIRE_EXTENSION_E2E_STATE_FILE', 'ASPIRE_EXTENSION_E2E_RUN_ID'];

suite('waitForNotificationMessage', () => {
    const originalExtesterModule = process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE;

    teardown(() => {
        resetLoadedNotificationModules();
        if (originalExtesterModule === undefined) {
            delete process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE;
        }
        else {
            process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE = originalExtesterModule;
        }
    });

    test('retries when a notification message read hits a replaced VS Code element', async () => {
        const staleNotification = createNotification(() => {
            throw new webDriverError.StaleElementReferenceError('stale element reference');
        });
        const freshNotification = createNotification('Aspire Dashboard ready');
        const { stub, vscode } = loadNotificationWaitModules();

        stub.setNotificationPolls([[staleNotification], [freshNotification]]);

        const notification = await vscode.waitForNotificationMessage('Dashboard ready', 5000);
        const waitState = stub.getNotificationWaitState();

        assert.strictEqual(notification, freshNotification);
        assert.strictEqual(waitState.notificationPollCount, 2);
        assert.deepStrictEqual(waitState.pollResults, [false, freshNotification]);
        assert.deepStrictEqual(waitState.waitMessages, [
            "Timed out waiting for notification containing 'Dashboard ready'.",
        ]);
    });

    test('retries when VS Code replaces the notification list before Selenium can read it', async () => {
        const freshNotification = createNotification('Aspire Dashboard ready');
        const { stub, vscode } = loadNotificationWaitModules();

        stub.setNotificationPolls([
            new webDriverError.StaleElementReferenceError('notifications list replaced'),
            [freshNotification],
        ]);

        const notification = await vscode.waitForNotificationMessage('Dashboard ready', 5000);
        const waitState = stub.getNotificationWaitState();

        assert.strictEqual(notification, freshNotification);
        assert.strictEqual(waitState.notificationPollCount, 2);
        assert.deepStrictEqual(waitState.pollResults, [false, freshNotification]);
    });

    test('propagates non-stale WebDriver failures', async () => {
        const sessionError = new webDriverError.NoSuchSessionError('session closed');
        const { stub, vscode } = loadNotificationWaitModules();

        stub.setNotificationPolls([sessionError]);

        await assert.rejects(
            vscode.waitForNotificationMessage('Dashboard ready', 5000),
            error => error === sessionError,
        );
    });

    test('propagates browser lifecycle failures from retrying waits', async () => {
        const { stub, vscode } = loadNotificationWaitModules();

        for (const lifecycleError of [
            new webDriverError.NoSuchSessionError('session closed'),
            new webDriverError.NoSuchWindowError('window closed'),
            new webDriverError.WebDriverError('unknown error: disconnected: not connected to DevTools'),
        ]) {
            stub.setTerminalPolls([lifecycleError]);

            await assert.rejects(
                vscode.waitForTerminalChannel('Now listening', 5000),
                error => error === lifecycleError,
            );
        }
    });

    test('retries transient generic WebDriver failures', async () => {
        const transientError = new webDriverError.WebDriverError('unknown error: element is not clickable at point');
        const { stub, vscode } = loadNotificationWaitModules();

        stub.setTerminalPolls([transientError, 'Now listening on: http://localhost']);

        const terminalText = await vscode.waitForTerminalChannel('Now listening', 5000);

        assert.strictEqual(terminalText, 'Now listening on: http://localhost');
    });

    test('preserves session failure classification when adding wait diagnostics', async () => {
        const sessionError = new webDriverError.NoSuchSessionError('session closed');
        const { stub, vscode } = loadNotificationWaitModules();

        stub.setEditorPolls([sessionError]);

        await assert.rejects(
            vscode.waitForEditorTitle('Dashboard', 5000),
            error => error instanceof Error &&
                error.name === 'NoSuchSessionError' &&
                error.message.includes('Open editor titles:'),
        );
    });

    test('dismisses notifications through VS Code clear-all instead of hovering each toast', async () => {
        const { stub, vscode } = loadNotificationWaitModules();
        const notification: NotificationLike = {
            getMessage: async () => "Required option '--message' was not provided.",
            // VS Code hides a toast's clear action once the toast moves out from under the pointer.
            dismiss: async () => {
                throw new webDriverError.ElementNotInteractableError('element not interactable');
            },
        };
        const bridge = startFakeE2eBridge(commandName => {
            if (commandName === 'clearAllNotifications') {
                stub.setNotificationPolls([]);
            }
        });

        stub.setNotificationPolls([[notification], [notification], [notification]]);

        try {
            await vscode.dismissAllNotifications(5000);
        }
        finally {
            bridge.dispose();
        }

        assert.deepStrictEqual(bridge.commandNames, ['clearAllNotifications']);
        // Clearing replaced the remaining polls, so a single poll after it found no notifications.
        assert.strictEqual(stub.getNotificationWaitState().notificationPollCount, 1);
    });

    test('waits for the tab to exist and for the provider to produce lenses', async () => {
        const { stub, vscode } = loadNotificationWaitModules();

        // openResources returns before VS Code creates the tab, so openEditor throws; once it exists the
        // provider has usually not produced anything yet, so the first read is empty.
        stub.setCodeLensPolls([
            new Error('No editor with title AppHost.java'),
            [],
            ['Run | Debug — these bypass Aspire'],
        ]);

        const texts = await vscode.waitForCodeLensText('AppHost.java', 'bypass Aspire', 5000);

        assert.deepStrictEqual(texts, ['Run | Debug — these bypass Aspire']);
    });

    test('reports the lenses it did see when none ever match', async () => {
        const { stub, vscode } = loadNotificationWaitModules();

        stub.setCodeLensPolls([['Run', 'Debug']]);

        await assert.rejects(
            vscode.waitForCodeLensText('AppHost.java', 'bypass Aspire', 5000),
            error => error instanceof Error
                && error.message.includes("Timed out waiting for a CodeLens containing 'bypass Aspire'")
                && error.message.includes('CodeLenses:')
                && error.message.includes('Run')
                && error.message.includes('Debug'),
        );
    });
});


function loadNotificationWaitModules(): {
    stub: NotificationExtesterStubModule;
    vscode: VscodeHelpersModule;
} {
    process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE = extesterStubModulePath;
    resetLoadedNotificationModules();

    const stub = require(extesterStubModulePath) as NotificationExtesterStubModule;
    stub.resetNotificationWaitState();

    const vscode = require(compiledVscodeHelpersModulePath) as VscodeHelpersModule;
    return { stub, vscode };
}

/**
 * Answers E2E control commands the way the extension host bridge does: it reads each new control
 * revision, reports it applied in the state file and records the command name.
 */
function startFakeE2eBridge(onCommand: (commandName: string) => void): { commandNames: string[]; dispose(): void } {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-e2e-bridge-'));
    const controlFile = path.join(directory, 'control.json');
    const stateFile = path.join(directory, 'state.json');
    const originalEnvironment = new Map(e2eBridgeEnvironmentVariables.map(name => [name, process.env[name]]));
    const commandNames: string[] = [];
    let lastRevision = -1;

    process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE = controlFile;
    process.env.ASPIRE_EXTENSION_E2E_STATE_FILE = stateFile;
    delete process.env.ASPIRE_EXTENSION_E2E_RUN_ID;
    fs.writeFileSync(stateFile, JSON.stringify({}));

    const interval = setInterval(() => {
        if (!fs.existsSync(controlFile)) {
            return;
        }

        const payload = JSON.parse(fs.readFileSync(controlFile, 'utf8')) as { revision: number; command?: { name: string } };
        if (payload.revision <= lastRevision) {
            return;
        }

        lastRevision = payload.revision;
        if (payload.command) {
            commandNames.push(payload.command.name);
            onCommand(payload.command.name);
        }

        fs.writeFileSync(stateFile, JSON.stringify({ control: { revision: payload.revision, status: 'applied', startedObserved: true } }));
    }, 10);

    return {
        commandNames,
        dispose: () => {
            clearInterval(interval);
            for (const [name, value] of originalEnvironment) {
                if (value === undefined) {
                    delete process.env[name];
                }
                else {
                    process.env[name] = value;
                }
            }
            fs.rmSync(directory, { recursive: true, force: true });
        },
    };
}

function resetLoadedNotificationModules(): void {
    for (const modulePath of [compiledVscodeHelpersModulePath, compiledExtesterModulePath, extesterStubModulePath]) {
        try {
            delete require.cache[require.resolve(modulePath)];
        }
        catch {
        }
    }
}

function createNotification(messageOrFactory: string | (() => Promise<string> | string)): NotificationLike {
    return {
        dismiss: async () => { },
        getMessage: async () => typeof messageOrFactory === 'function'
            ? await messageOrFactory()
            : messageOrFactory,
    };
}
