import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

type ManifestMenuItem = {
    command?: string;
    when?: string;
    group?: string;
};

type ManifestCommand = {
    command?: string;
    icon?: string;
};

type DebuggerProperty = {
    type?: string | string[];
    description?: string;
    additionalProperties?: { type?: string };
    items?: { type?: string };
    default?: unknown;
};

type DebuggerContribution = {
    type?: string;
    configurationAttributes?: {
        launch?: {
            properties?: { [key: string]: DebuggerProperty };
        };
    };
};

type ExtensionManifest = {
    activationEvents?: string[];
    contributes: {
        commands?: ManifestCommand[];
        viewsWelcome?: Array<{ view?: string; contents?: string; when?: string }>;
        menus?: {
            commandPalette?: ManifestMenuItem[];
            'explorer/context'?: ManifestMenuItem[];
            'view/title'?: ManifestMenuItem[];
            'view/item/context'?: ManifestMenuItem[];
        };
        debuggers?: DebuggerContribution[];
    };
};

function readManifest(): ExtensionManifest {
    const manifestPath = path.resolve(__dirname, '../../package.json');
    return JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as ExtensionManifest;
}

function assertContains(whenClause: string | undefined, fragment: string): void {
    assert.ok(whenClause?.includes(fragment), `Expected "${whenClause}" to contain "${fragment}"`);
}

suite('extension/package.json', () => {
    test('running apphosts welcome states use string view mode checks', () => {
        const manifest = readManifest();
        const runningAppHostsWelcome = manifest.contributes.viewsWelcome?.filter(item => item.view === 'aspire-vscode.appHosts') ?? [];

        const workspaceWelcome = runningAppHostsWelcome.find(item => item.contents === '%views.appHosts.welcome%');
        const globalWelcome = runningAppHostsWelcome.find(item => item.contents === '%views.appHosts.globalWelcome%');

        assertContains(workspaceWelcome?.when, "aspire.viewMode != 'global'");
        assertContains(globalWelcome?.when, "aspire.viewMode == 'global'");
    });

    test('running apphosts title actions use string view and view mode checks', () => {
        const manifest = readManifest();
        const titleMenus = manifest.contributes.menus?.['view/title'] ?? [];

        const switchToGlobal = titleMenus.find(item => item.command === 'aspire-vscode.switchToGlobalView');
        const switchToWorkspace = titleMenus.find(item => item.command === 'aspire-vscode.switchToWorkspaceView');
        const globalRefreshAppHosts = titleMenus.find(item => item.command === 'aspire-vscode.globalRefreshAppHosts');

        assertContains(switchToGlobal?.when, "view == 'aspire-vscode.appHosts'");
        assertContains(switchToGlobal?.when, "aspire.viewMode != 'global'");
        assertContains(switchToWorkspace?.when, "view == 'aspire-vscode.appHosts'");
        assertContains(switchToWorkspace?.when, "aspire.viewMode == 'global'");
        assertContains(globalRefreshAppHosts?.when, "view == 'aspire-vscode.appHosts'");
    });

    test('workspace non-running apphost context actions include run and debug', () => {
        const manifest = readManifest();
        const contextMenus = manifest.contributes.menus?.['view/item/context'] ?? [];

        const runAppHost = contextMenus.find(item => item.command === 'aspire-vscode.runAppHost');
        const debugAppHost = contextMenus.find(item => item.command === 'aspire-vscode.debugAppHost');

        assertContains(runAppHost?.when, "view == aspire-vscode.appHosts");
        assertContains(runAppHost?.when, 'viewItem == workspaceAppHost');
        assertContains(debugAppHost?.when, "view == aspire-vscode.appHosts");
        assertContains(debugAppHost?.when, 'viewItem == workspaceAppHost');
    });

    test('resource command context action targets apphosts view', () => {
        const manifest = readManifest();
        const contextMenus = manifest.contributes.menus?.['view/item/context'] ?? [];

        const executeResourceCommandItem = contextMenus.find(item => item.command === 'aspire-vscode.executeResourceCommandItem');

        assertContains(executeResourceCommandItem?.when, 'view == aspire-vscode.appHosts');
        assertContains(executeResourceCommandItem?.when, 'viewItem == resourceCommand:enabled');
    });

    test('running apphost context actions only target running apphost contexts', () => {
        const manifest = readManifest();
        const contextMenus = manifest.contributes.menus?.['view/item/context'] ?? [];

        const openDashboard = contextMenus.find(item => item.command === 'aspire-vscode.openDashboard');
        const expandAll = contextMenus.find(item => item.command === 'aspire-vscode.expandAll');
        const openAppHostSource = contextMenus.find(item => item.command === 'aspire-vscode.openAppHostSource');

        assertContains(openDashboard?.when, 'workspaceResources');
        assertContains(expandAll?.when, 'workspaceResources');
        assertContains(openAppHostSource?.when, 'workspaceResources');
    });

    test('dashboard inline actions have distinct icons', () => {
        const manifest = readManifest();
        const commands = manifest.contributes.commands ?? [];
        const contextMenus = manifest.contributes.menus?.['view/item/context'] ?? [];

        const openDashboard = commands.find(item => item.command === 'aspire-vscode.openDashboard');
        const openDashboardToSide = commands.find(item => item.command === 'aspire-vscode.openDashboardToSide');
        const openDashboardMenu = contextMenus.find(item => item.command === 'aspire-vscode.openDashboard');
        const openDashboardToSideMenu = contextMenus.find(item => item.command === 'aspire-vscode.openDashboardToSide');

        assert.strictEqual(openDashboardMenu?.group, 'inline');
        assert.strictEqual(openDashboardToSideMenu?.group, 'inline');
        assert.ok(openDashboard?.icon);
        assert.ok(openDashboardToSide?.icon);
        assert.notStrictEqual(openDashboardToSide.icon, openDashboard.icon);
    });

    test('dashboard commands use noRunningAppHosts gate in the command palette', () => {
        const manifest = readManifest();
        const commandPaletteMenus = manifest.contributes.menus?.commandPalette ?? [];

        const openDashboard = commandPaletteMenus.find(item => item.command === 'aspire-vscode.openDashboard');
        const openDashboardToSide = commandPaletteMenus.find(item => item.command === 'aspire-vscode.openDashboardToSide');

        assert.strictEqual(openDashboard?.when, '!aspire.noRunningAppHosts');
        assert.strictEqual(openDashboardToSide?.when, '!aspire.noRunningAppHosts');
    });

    test('Node module AppHost files activate the extension', () => {
        const manifest = readManifest();
        const activationEvents = manifest.activationEvents ?? [];

        assert.ok(activationEvents.includes('workspaceContains:**/apphost.ts'));
        assert.ok(activationEvents.includes('workspaceContains:**/apphost.mts'));
        assert.ok(activationEvents.includes('workspaceContains:**/apphost.cts'));
        assert.ok(activationEvents.includes('workspaceContains:**/apphost.js'));
        assert.ok(activationEvents.includes('workspaceContains:**/apphost.mjs'));
        assert.ok(activationEvents.includes('workspaceContains:**/apphost.cjs'));
    });

    test('Explorer AppHost commands include Node module filenames', () => {
        const manifest = readManifest();
        const explorerMenus = manifest.contributes.menus?.['explorer/context'] ?? [];
        const expectedAppHostFiles = ['apphost.ts', 'apphost.mts', 'apphost.cts', 'apphost.js', 'apphost.mjs', 'apphost.cjs'];

        for (const commandName of ['aspire-vscode.runAppHostCommand', 'aspire-vscode.debugAppHostCommand']) {
            const menuItem = explorerMenus.find(item => item.command === commandName);
            assert.ok(menuItem?.when, `Expected ${commandName} to have a when clause`);

            const match = menuItem.when.match(/resourceFilename =~ \/(.+)\/i/);
            assert.ok(match, `Expected ${commandName} to use a resourceFilename regex`);

            const regex = new RegExp(match[1], 'i');
            for (const fileName of expectedAppHostFiles) {
                assert.ok(regex.test(fileName), `Expected ${commandName} to match ${fileName}`);
            }
        }
    });

    test('aspire launch configuration declares an env property as a string-valued object', () => {
        const manifest = readManifest();
        const aspireDebugger = manifest.contributes.debuggers?.find(d => d.type === 'aspire');
        const properties = aspireDebugger?.configurationAttributes?.launch?.properties;

        assert.ok(properties, 'Expected aspire debugger to declare launch configuration properties');
        const envProperty = properties.env;
        assert.ok(envProperty, 'Expected aspire launch configuration to declare an env property');
        assert.strictEqual(envProperty.type, 'object');
        assert.strictEqual(envProperty.additionalProperties?.type, 'string');
        assert.strictEqual(envProperty.description, '%extension.debug.env%');
    });

    test('aspire launch configuration declares an args property as a string array', () => {
        const manifest = readManifest();
        const aspireDebugger = manifest.contributes.debuggers?.find(d => d.type === 'aspire');
        const properties = aspireDebugger?.configurationAttributes?.launch?.properties;

        assert.ok(properties, 'Expected aspire debugger to declare launch configuration properties');
        const argsProperty = properties.args;
        assert.ok(argsProperty, 'Expected aspire launch configuration to declare an args property');
        assert.strictEqual(argsProperty.type, 'array');
        assert.strictEqual(argsProperty.items?.type, 'string');
        assert.strictEqual(argsProperty.description, '%extension.debug.args%');
    });
});
