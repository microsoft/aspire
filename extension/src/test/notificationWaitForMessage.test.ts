import * as assert from 'assert';
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
    setNotificationPolls(notificationPolls: Array<NotificationLike[] | Error>): void;
    setTerminalPolls(terminalPolls: Array<string | Error>): void;
}

interface VscodeHelpersModule {
    waitForEditorTitle(expectedText: string, timeoutMs?: number): Promise<string>;
    waitForNotificationMessage(expectedText: string, timeoutMs?: number): Promise<NotificationLike>;
    waitForTerminalChannel(expectedText: string, timeoutMs?: number): Promise<string>;
}

const extensionRoot = path.resolve(__dirname, '..', '..');
const extesterStubModulePath = path.join(extensionRoot, 'out', 'test', 'fixtures', 'e2e-notification-extester-stub.js');
const compiledE2eHelpersPath = path.join(extensionRoot, 'out', 'test-e2e', 'helpers');
const compiledExtesterModulePath = path.join(compiledE2eHelpersPath, 'extester.js');
const compiledVscodeHelpersModulePath = path.join(compiledE2eHelpersPath, 'vscode.js');

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
