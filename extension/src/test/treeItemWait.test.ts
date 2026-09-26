import * as assert from 'assert';
import * as path from 'path';
import { error as webDriverError } from 'selenium-webdriver';
import type * as ExtesterStub from './fixtures/e2e-notification-extester-stub';

interface VscodeHelpersModule {
    waitForTreeItem(section: ExtesterStub.TreeSectionLike, label: string, timeoutMs?: number): Promise<ExtesterStub.TreeItemLike>;
    waitForAppHostsTreePath(labels: readonly string[], timeoutMs?: number): Promise<string[]>;
}

const extensionRoot = path.resolve(__dirname, '..', '..');
const stubModulePath = path.join(extensionRoot, 'out', 'test', 'fixtures', 'e2e-notification-extester-stub.js');
const helpersPath = path.join(extensionRoot, 'out', 'test-e2e', 'helpers');
const extesterModulePath = path.join(helpersPath, 'extester.js');
const vscodeHelpersModulePath = path.join(helpersPath, 'vscode.js');
const appHostLabel = 'AspireE2E.AppHost.csproj';

suite('AppHost tree waits', () => {
    let originalExtesterModule: string | undefined;

    setup(() => {
        originalExtesterModule = process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE;
        process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE = stubModulePath;
        resetLoadedModules();
    });

    teardown(() => {
        resetLoadedModules();
        if (originalExtesterModule === undefined) {
            delete process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE;
        }
        else {
            process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE = originalExtesterModule;
        }
    });

    test('reacquires a row recycled into an AppHost action after lookup', async () => {
        const { stub, vscode } = loadModules();
        const appHost = stub.createTreeItem(appHostLabel);
        stub.setTreeItemPolls([stub.createTreeItem('Deploy AppHost'), appHost]);

        const item = await vscode.waitForTreeItem(stub.createTreeSection(), appHostLabel, 5000);

        assert.strictEqual(await item.getLabel(), appHostLabel);
        assert.strictEqual(item, appHost);
    });

    test('also checks the label when reacquiring the AppHosts section', async () => {
        const { stub, vscode } = loadModules();
        const appHost = stub.createTreeItem(appHostLabel);
        stub.setTreeItemPolls([undefined, stub.createTreeItem('Deploy AppHost'), appHost]);
        stub.setTreeSectionPolls([[stub.createTreeSection()]]);

        const item = await vscode.waitForTreeItem(stub.createTreeSection(), appHostLabel, 5000);

        assert.strictEqual(item, appHost);
    });

    test('retries a stale label read with a fresh row', async () => {
        const { stub, vscode } = loadModules();
        const appHost = stub.createTreeItem(appHostLabel);
        stub.setTreeItemPolls([
            stub.createTreeItem(new webDriverError.StaleElementReferenceError('row replaced')),
            appHost,
        ]);

        const item = await vscode.waitForTreeItem(stub.createTreeSection(), appHostLabel, 5000);

        assert.strictEqual(item, appHost);
    });

    test('does not accept a permanently wrong label', async () => {
        const { stub, vscode } = loadModules();
        stub.setTreeItemPolls([stub.createTreeItem('Deploy AppHost')]);

        await assert.rejects(
            vscode.waitForTreeItem(stub.createTreeSection(), appHostLabel, 5000),
            error => error instanceof Error && error.message.includes(appHostLabel) && error.message.includes('Deploy AppHost'));
    });

    test('propagates browser lifecycle failures during label reads', async () => {
        const { stub, vscode } = loadModules();
        const sessionError = new webDriverError.NoSuchSessionError('session closed');
        stub.setTreeItemPolls([stub.createTreeItem(sessionError)]);

        await assert.rejects(
            vscode.waitForTreeItem(stub.createTreeSection(), appHostLabel, 5000),
            error => error instanceof Error && error.name === sessionError.name && error.message.includes(sessionError.message));
    });

    test('waits for the exact visible AppHost and its direct child in one snapshot', async () => {
        const { stub, vscode } = loadModules();
        stub.setTreeRowPolls([
            [{ label: 'Deploy AppHost', level: 1 }],
            [{ label: appHostLabel, level: 1 }],
            [{ label: appHostLabel, level: 1 }, { label: 'Run AppHost', level: 2 }],
        ]);

        const labels = await vscode.waitForAppHostsTreePath([appHostLabel, 'Run AppHost'], 5000);

        assert.deepStrictEqual(labels, [appHostLabel, 'Run AppHost']);
        assert.deepStrictEqual(stub.getNotificationWaitState().pollResults, [false, false, labels]);
    });

    test('captures the matched label without a later live row read', async () => {
        const { stub, vscode } = loadModules();
        const row = { label: appHostLabel, level: 1 };
        stub.setTreeRowPolls([[row]]);

        const labels = await vscode.waitForAppHostsTreePath([appHostLabel], 5000);
        row.label = 'Deploy AppHost';

        assert.deepStrictEqual(labels, [appHostLabel]);
    });

    test('allows a streamed candidate without waiting for children or discovery completion', async () => {
        const { stub, vscode } = loadModules();
        stub.setTreeRowPolls([[{ label: appHostLabel, level: 1 }]]);

        assert.deepStrictEqual(await vscode.waitForAppHostsTreePath([appHostLabel], 5000), [appHostLabel]);
    });

    test('finds a path beneath an AppHost grouping row', async () => {
        const { stub, vscode } = loadModules();
        stub.setTreeRowPolls([[
            { label: 'Workspace AppHosts (2)', level: 1 },
            { label: appHostLabel, level: 2 },
            { label: 'Open AppHost source', level: 3 },
            { label: 'Run AppHost', level: 3 },
        ]]);

        assert.deepStrictEqual(
            await vscode.waitForAppHostsTreePath([appHostLabel, 'Run AppHost'], 5000),
            [appHostLabel, 'Run AppHost']);
    });

    test('uses tree indexes rather than recycled DOM order for parentage', async () => {
        const { stub, vscode } = loadModules();
        stub.setTreeRowPolls([[
            { label: 'Run AppHost', level: 2, index: 2 },
            { label: appHostLabel, level: 1, index: 0 },
            { label: 'Open AppHost source', level: 2, index: 1 },
        ]]);

        assert.deepStrictEqual(
            await vscode.waitForAppHostsTreePath([appHostLabel, 'Run AppHost'], 5000),
            [appHostLabel, 'Run AppHost']);
    });

    for (const [name, rows] of [
        ['another AppHost', [
            { label: appHostLabel, level: 1 },
            { label: 'Other.AppHost.csproj', level: 1 },
            { label: 'Run AppHost', level: 2 },
        ]],
        ['a nested grandchild', [
            { label: appHostLabel, level: 1 },
            { label: 'Commands', level: 2 },
            { label: 'Run AppHost', level: 3 },
        ]],
        ['a partial label match', [
            { label: `${appHostLabel}.backup`, level: 1 },
            { label: 'Run AppHost', level: 2 },
        ]],
        ['a hidden row', [
            { label: appHostLabel, level: 1, visible: false },
            { label: 'Run AppHost', level: 2 },
        ]],
        ['a missing intermediate parent', [
            { label: appHostLabel, level: 1 },
            { label: 'Run AppHost', level: 3 },
        ]],
        ['an unrendered parent in a tree index gap', [
            { label: appHostLabel, level: 1, index: 0 },
            { label: 'Run AppHost', level: 2, index: 10 },
        ]],
    ] satisfies Array<[string, ExtesterStub.TreeRow[]]>) {
        test(`does not match the action under ${name}`, async () => {
            const { stub, vscode } = loadModules();
            stub.setTreeRowPolls([rows]);

            await assert.rejects(
                vscode.waitForAppHostsTreePath([appHostLabel, 'Run AppHost'], 5000),
                error => error instanceof Error && error.message.includes(appHostLabel) && error.message.includes('Run AppHost'));
        });
    }

    test('retries a replaced pane and reports the last snapshot on timeout', async () => {
        const { stub, vscode } = loadModules();
        stub.setTreeRowPolls([
            new webDriverError.StaleElementReferenceError('pane replaced'),
            [{ label: 'Deploy AppHost', level: 1 }],
        ]);

        await assert.rejects(
            vscode.waitForAppHostsTreePath([appHostLabel], 5000),
            error => error instanceof Error && error.message.includes(appHostLabel) && error.message.includes('Deploy AppHost'));
    });

    test('propagates lifecycle failures from snapshot reads', async () => {
        const { stub, vscode } = loadModules();
        const sessionError = new webDriverError.NoSuchWindowError('window closed');
        stub.setTreeRowPolls([sessionError]);

        await assert.rejects(
            vscode.waitForAppHostsTreePath([appHostLabel], 5000),
            error => error instanceof Error && error.name === sessionError.name && error.message.includes(sessionError.message));
    });

    test('does not hide browser script errors behind a timeout', async () => {
        const { stub, vscode } = loadModules();
        stub.setTreeRowPolls([new webDriverError.JavascriptError('invalid tree snapshot script')]);

        await assert.rejects(
            vscode.waitForAppHostsTreePath([appHostLabel], 5000),
            error => error instanceof Error && error.name === 'JavascriptError' && error.message.includes('invalid tree snapshot script'));
    });

    test('rejects an empty tree path', async () => {
        const { vscode } = loadModules();

        await assert.rejects(vscode.waitForAppHostsTreePath([]), /must contain at least one label/);
    });
});

function loadModules(): { stub: typeof ExtesterStub; vscode: VscodeHelpersModule } {
    const stub = require(stubModulePath) as typeof ExtesterStub;
    stub.resetNotificationWaitState();
    return { stub, vscode: require(vscodeHelpersModulePath) as VscodeHelpersModule };
}

function resetLoadedModules(): void {
    for (const modulePath of [vscodeHelpersModulePath, extesterModulePath, stubModulePath]) {
        delete require.cache[modulePath];
    }
}
