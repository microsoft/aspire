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
    enum?: string[];
    enumDescriptions?: string[];
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
    scripts?: { [key: string]: string };
    devDependencies?: { [key: string]: string };
    contributes: {
        commands?: ManifestCommand[];
        viewsWelcome?: Array<{ view?: string; contents?: string; when?: string }>;
        menus?: {
            commandPalette?: ManifestMenuItem[];
            'editor/title/run'?: ManifestMenuItem[];
            'explorer/context'?: ManifestMenuItem[];
            'view/title'?: ManifestMenuItem[];
            'view/item/context'?: ManifestMenuItem[];
        };
        debuggers?: DebuggerContribution[];
    };
};

const extensionRoot = path.resolve(__dirname, '../..');

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
        const compatibilityErrorWelcome = runningAppHostsWelcome.find(item => item.contents === '%views.appHosts.errorWelcome%');
        const genericErrorWelcome = runningAppHostsWelcome.find(item => item.contents === '%views.appHosts.genericErrorWelcome%');

        assertContains(workspaceWelcome?.when, "aspire.viewMode != 'global'");
        assertContains(globalWelcome?.when, "aspire.viewMode == 'global'");
        assertContains(compatibilityErrorWelcome?.when, 'aspire.fetchAppHostsCompatibilityError');
        assertContains(genericErrorWelcome?.when, '!aspire.fetchAppHostsCompatibilityError');
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

        // The idle workspace AppHost context value carries the actions its CLI supports as
        // `:can*` suffixes, so run and debug match the shape rather than the bare string.
        assertContains(runAppHost?.when, "view == aspire-vscode.appHosts");
        assertContains(runAppHost?.when, 'viewItem =~ /^workspaceAppHost(:[A-Za-z]+)*$/');
        assertContains(debugAppHost?.when, "view == aspire-vscode.appHosts");
        assertContains(debugAppHost?.when, 'viewItem =~ /^workspaceAppHost(:[A-Za-z]+)*$/');
    });

    test('resource command context action targets apphosts view', () => {
        const manifest = readManifest();
        const contextMenus = manifest.contributes.menus?.['view/item/context'] ?? [];

        const executeResourceCommandItem = contextMenus.find(item => item.command === 'aspire-vscode.executeResourceCommandItem');
        const openResourceTerminal = contextMenus.find(item => item.command === 'aspire-vscode.openResourceTerminal');

        assertContains(executeResourceCommandItem?.when, 'view == aspire-vscode.appHosts');
        assertContains(executeResourceCommandItem?.when, 'viewItem == resourceCommand:enabled');
        assertContains(openResourceTerminal?.when, 'view == aspire-vscode.appHosts');
        assertContains(openResourceTerminal?.when, 'viewItem =~ /^resource.*:canOpenTerminal/');
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

    test('Create with Aspire is pane-only while new and init remain in the command palette', () => {
        const manifest = readManifest();
        const hiddenFromPalette = new Set((manifest.contributes.menus?.commandPalette ?? [])
            .filter(item => item.when === 'false')
            .map(item => item.command));

        assert.ok(hiddenFromPalette.has('aspire-vscode.createWithAspire'));
        assert.ok(!hiddenFromPalette.has('aspire-vscode.new'));
        assert.ok(!hiddenFromPalette.has('aspire-vscode.init'));
    });

    test('active AppHost Run action does not require a language debugger', () => {
        const manifest = readManifest();
        const editorRunMenus = manifest.contributes.menus?.['editor/title/run'] ?? [];

        const runAppHost = editorRunMenus.find(item => item.command === 'aspire-vscode.runAppHostCommand');
        const debugAppHost = editorRunMenus.find(item => item.command === 'aspire-vscode.debugAppHostCommand');

        assert.strictEqual(runAppHost?.when, 'aspire.fileIsAppHost || (aspire.workspaceHasAppHost && aspire.editorSupportsRunDebug)');
        assert.strictEqual(debugAppHost?.when, '(aspire.fileIsAppHost || aspire.workspaceHasAppHost) && aspire.editorSupportsRunDebug');
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

    test('Rust AppHost files activate the extension', () => {
        const manifest = readManifest();
        const activationEvents = manifest.activationEvents ?? [];

        assert.ok(activationEvents.includes('workspaceContains:**/apphost.rs'));
    });

    // A Java-only workspace contains no project file the other activation events match: no .csproj,
    // and `.aspire/` only exists once a restore has already run. Without an explicit Java event the
    // extension never activates on a fresh clone, so the AppHost never appears in the Aspire view
    // and none of the editor features register. Both casings are listed because `workspaceContains`
    // globs are case-sensitive on Linux and macOS, and Java ties the file name to the class name:
    // `aspire init` writes `AppHost.java` while the lowercase spelling matches the other languages.
    test('Java AppHost files activate the extension', () => {
        const manifest = readManifest();
        const activationEvents = manifest.activationEvents ?? [];

        assert.ok(activationEvents.includes('workspaceContains:**/AppHost.java'), 'aspire init writes AppHost.java');
        assert.ok(activationEvents.includes('workspaceContains:**/apphost.java'), 'lowercase matches the other languages');
        // Maven and Gradle AppHosts nest the source under the standard layout rather than the root.
        assert.ok(activationEvents.includes('workspaceContains:**/src/main/java/AppHost.java'));
    });

    test('FSharp and Visual Basic AppHost projects activate the extension', () => {
        const manifest = readManifest();
        const activationEvents = manifest.activationEvents ?? [];

        assert.ok(activationEvents.includes('workspaceContains:**/*.fsproj'));
        assert.ok(activationEvents.includes('workspaceContains:**/*.vbproj'));
    });

    test('Explorer AppHost commands include guest AppHost filenames', () => {
        const manifest = readManifest();
        const explorerMenus = manifest.contributes.menus?.['explorer/context'] ?? [];
        const expectedAppHostFiles = ['apphost.ts', 'apphost.mts', 'apphost.cts', 'apphost.js', 'apphost.mjs', 'apphost.cjs', 'apphost.rs'];

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

    test('CodeLens command handlers are contributed and hidden from the command palette', () => {
        // Commands registered by the CodeLens module are invoked from lenses, never typed by the user,
        // so each registration needs a contributes.commands entry (otherwise the title is
        // unlocalized/undeclared) plus a commandPalette "when": "false" entry.
        const manifest = readManifest();
        const registrationSource = fs.readFileSync(path.resolve(__dirname, '../../src/activation/registerCodeLensCommands.ts'), 'utf8');
        const registeredCodeLensCommands = [...registrationSource.matchAll(
            /registerInstrumentedCommand\(\s*(['"])(aspire-vscode\.[A-Za-z0-9]+)\1/g)]
            .map(match => match[2]);

        assert.ok(registeredCodeLensCommands.includes('aspire-vscode.codeLensRevealAppHost'), 'Expected codeLensRevealAppHost to be registered.');
        assert.ok(registeredCodeLensCommands.includes('aspire-vscode.installDebuggerExtension'), 'Expected installDebuggerExtension to be registered.');

        const contributedCommands = new Set((manifest.contributes.commands ?? []).map(item => item.command));
        const hiddenFromPalette = new Set((manifest.contributes.menus?.commandPalette ?? [])
            .filter(item => item.when === 'false')
            .map(item => item.command));

        assert.deepStrictEqual(registeredCodeLensCommands.filter(command => !contributedCommands.has(command)), []);
        assert.deepStrictEqual(registeredCodeLensCommands.filter(command => !hiddenFromPalette.has(command)), []);
    });

    test('aspire launch configuration declares dashboard browser choices', () => {
        const manifest = readManifest();
        const aspireDebugger = manifest.contributes.debuggers?.find(d => d.type === 'aspire');
        const properties = aspireDebugger?.configurationAttributes?.launch?.properties;

        assert.ok(properties, 'Expected aspire debugger to declare launch configuration properties');
        const dashboardBrowserProperty = properties.dashboardBrowser;
        assert.ok(dashboardBrowserProperty, 'Expected aspire launch configuration to declare a dashboardBrowser property');
        assert.strictEqual(dashboardBrowserProperty.type, 'string');
        assert.strictEqual(dashboardBrowserProperty.description, '%extension.debug.dashboardBrowser%');
        assert.deepStrictEqual(dashboardBrowserProperty.enum, [
            'none',
            'notification',
            'openExternalBrowser',
            'integratedBrowser',
            'debugChrome',
            'debugEdge',
            'debugFirefox',
        ]);
        assert.deepStrictEqual(dashboardBrowserProperty.enumDescriptions, [
            '%configuration.aspire.dashboardBrowser.none%',
            '%configuration.aspire.dashboardBrowser.notification%',
            '%configuration.aspire.dashboardBrowser.openExternalBrowser%',
            '%configuration.aspire.dashboardBrowser.integratedBrowser%',
            '%configuration.aspire.dashboardBrowser.debugChrome%',
            '%configuration.aspire.dashboardBrowser.debugEdge%',
            '%configuration.aspire.dashboardBrowser.debugFirefox%',
        ]);
    });
});

// TypeScript 7 is a native (Go) compiler that ships no JavaScript compiler API, so the
// extension cannot simply move `typescript` to 7.x: `src/editor/parsers/jsTsAppHostParser.ts`,
// `src/test/telemetryInventory.test.ts`, ts-loader, gulp-typescript and typescript-eslint all
// import the `typescript` module. The TypeScript team's supported bridge is to alias the
// `typescript` module name onto the `@typescript/typescript6` compatibility package (which
// re-exports the 6.0 API and ships a `tsc6` binary) while installing the native compiler under
// a second alias. See "Running Side-by-Side with TypeScript 6.0" in
// https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/.
suite('extension/package.json TypeScript 7 bridge', () => {
    test('typescript module name is aliased to the TypeScript 6 API compatibility package', () => {
        const manifest = readManifest();
        const typescriptSpecifier = manifest.devDependencies?.typescript;

        assert.ok(
            typescriptSpecifier?.startsWith('npm:@typescript/typescript6@'),
            `Expected the typescript devDependency to alias @typescript/typescript6, got "${typescriptSpecifier}"`);
    });

    test('TypeScript 7 native compiler is installed under the @typescript/native alias', () => {
        const manifest = readManifest();
        const nativeSpecifier = manifest.devDependencies?.['@typescript/native'];

        assert.ok(
            nativeSpecifier?.startsWith('npm:typescript@7.'),
            `Expected @typescript/native to alias typescript@7.x, got "${nativeSpecifier}"`);
    });

    test('emitting scripts invoke tsc6 because the alias removes the tsc binary from typescript', () => {
        const manifest = readManifest();

        for (const scriptName of ['compile-tests', 'watch-tests', 'compile-e2e']) {
            const script = manifest.scripts?.[scriptName];
            assert.ok(script, `Expected a "${scriptName}" script`);
            assert.ok(
                /(^|\s|&&\s)tsc6\s/.test(script),
                `Expected "${scriptName}" to emit with tsc6, got "${script}"`);
        }
    });

    test('a native typecheck script runs the TypeScript 7 compiler without emitting', () => {
        const manifest = readManifest();
        const typecheck = manifest.scripts?.['typecheck'];

        assert.ok(typecheck, 'Expected a "typecheck" script that runs the TypeScript 7 native compiler');
        assert.ok(
            typecheck.includes('scripts/typecheck-native.js'),
            `Expected the typecheck script to delegate to scripts/typecheck-native.js, got "${typecheck}"`);

        // The resolver exists so the compiler is not addressed by a hardcoded node_modules path.
        // Asserting on how it locates the binary — module resolution, then the package's own `bin`
        // declaration — pins the mechanism rather than just the absence of one bad string.
        const resolver = fs.readFileSync(path.join(extensionRoot, 'scripts', 'typecheck-native.js'), 'utf8');
        assert.ok(
            resolver.includes("require.resolve(`${nativePackageName}/package.json`"),
            'Expected typecheck-native.js to locate the compiler with require.resolve');
        assert.ok(
            resolver.includes('packageJson.bin'),
            'Expected typecheck-native.js to read the executable path from the package\'s own bin field');

        // Both TypeScript projects have to pass the native compiler. tsconfig.json omits src/test-e2e,
        // which is compiled separately, so checking only tsconfig.json would leave the e2e sources
        // unverified against TypeScript 7.
        for (const project of ['tsconfig.json', 'tsconfig.e2e.json']) {
            assert.ok(
                resolver.includes(`'${project}'`),
                `Expected typecheck-native.js to type-check ${project}`);
            assert.ok(
                fs.existsSync(path.join(extensionRoot, project)),
                `Expected ${project} to exist for the native typecheck`);
        }
    });

    // The native compiler is a Go binary delivered through per-platform optional dependencies, so a
    // lockfile that is missing an entry fails at run time on that platform only. CI runs the
    // extension build on Linux, Windows and macOS, and contributors work on arm64 and x64 on all
    // three, so every one of those combinations has to be present in the resolved lockfile.
    test('the lockfile carries the native compiler binary for every platform CI and contributors build on', () => {
        const lockfile = fs.readFileSync(path.join(extensionRoot, 'yarn.lock'), 'utf8');

        const requiredPlatformPackages = [
            'typescript-linux-x64',
            'typescript-linux-arm64',
            'typescript-win32-x64',
            'typescript-win32-arm64',
            'typescript-darwin-x64',
            'typescript-darwin-arm64',
        ];

        for (const platformPackage of requiredPlatformPackages) {
            assert.ok(
                lockfile.includes(`@typescript/${platformPackage}@`),
                `Expected yarn.lock to resolve @typescript/${platformPackage}. Regenerate it with "corepack yarn install" so the native compiler works on that platform.`);
        }
    });

    test('pretest runs the native typecheck alongside the existing compile and lint steps', () => {
        const manifest = readManifest();
        const pretest = manifest.scripts?.pretest;

        assert.ok(pretest, 'Expected a "pretest" script');
        for (const step of ['compile-tests', 'compile', 'lint', 'typecheck']) {
            assert.ok(
                pretest.includes(`yarn run ${step}`),
                `Expected pretest to run "${step}", got "${pretest}"`);
        }
    });
});
