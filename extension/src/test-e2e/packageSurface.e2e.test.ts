import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, getDebugLaunchCount, getTerminalCommandCount, isSamePath, waitForCommandOutcome, waitForDebugLaunch, waitForRepositoryIdle, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, setCliUnavailableForE2E, setDebugLaunchSuppressedForE2E, setTerminalCommandExecutionSuppressedForE2E } from './helpers/fixtures';
import { ensureDiagnosticsDir, getExtensionRoot, getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView, waitForEditorTitle } from './helpers/vscode';

interface PackageJson {
    name?: string;
    icon?: string;
    activationEvents?: string[];
    contributes?: {
        languageModelTools?: Array<{
            name?: string;
            toolReferenceName?: string;
            displayName?: string;
            modelDescription?: string;
            userDescription?: string;
            icon?: string;
            canBeReferencedInPrompt?: boolean;
            when?: string;
            tags?: string[];
            inputSchema?: {
                type?: string;
                properties?: Record<string, {
                    type?: string;
                    description?: string;
                    enum?: string[];
                    minLength?: number;
                    maxLength?: number;
                }>;
                required?: string[];
                additionalProperties?: boolean;
            };
        }>;
        commands?: Array<{ command: string; title: string }>;
        menus?: Record<string, Array<{ command?: string; when?: string; group?: string }>>;
        configuration?: { properties?: Record<string, unknown> };
        jsonValidation?: Array<{ fileMatch?: string | string[]; url?: string }>;
        viewsContainers?: { activitybar?: Array<{ id?: string; title?: string; icon?: string }> };
        views?: Record<string, Array<{ id?: string; name?: string }>>;
        viewsWelcome?: Array<{ view?: string; contents?: string; when?: string }>;
        debuggers?: Array<{
            type?: string;
            configurationAttributes?: {
                launch?: {
                    required?: string[];
                    properties?: Record<string, { type?: string; description?: string; enum?: string[]; minLength?: number; maxLength?: number; items?: { type?: string }; additionalProperties?: { type?: string } }>;
                };
            };
            configurationSnippets?: Array<{ body?: Record<string, unknown> }>;
            initialConfigurations?: Array<Record<string, unknown>>;
        }>;
        walkthroughs?: Array<{ steps?: Array<{ id?: string; media?: { markdown?: string }; completionEvents?: string[] }> }>;
        colors?: Array<{ id?: string; defaults?: Record<string, string> }>;
        mcpServerDefinitionProviders?: Array<{ id?: string; label?: string }>;
    };
}

interface DiagnosticResult {
    message: string;
    severity: number;
    code?: string | number;
}

suite('Aspire package contribution surface E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await setCliUnavailableForE2E(false);
        await setTerminalCommandExecutionSuppressedForE2E(false);
        await setDebugLaunchSuppressedForE2E(false);
        await restoreWorkspaceCliPath();
    });

    test('keeps contributed package commands, views, settings, schemas, walkthroughs, and debug type registered', async () => {
        await openAspireView();
        await waitForRepositoryIdle();

        const sourcePackage = readSourcePackageJson();
        const installedPackage = (await executeE2eControlCommand({ name: 'getExtensionPackageJson' })).result as PackageJson;
        const registeredCommands = (await executeE2eControlCommand({ name: 'getRegisteredAspireCommands' })).result as string[];

        const sourceCommandIds = getPackageCommandIds(sourcePackage);
        const installedCommandIds = getPackageCommandIds(installedPackage);
        assert.deepStrictEqual(sourcePackage.activationEvents, expectedActivationEvents);
        assert.deepStrictEqual(getLanguageModelTools(sourcePackage), expectedSourceLanguageModelTools);
        assert.deepStrictEqual(getLanguageModelTools(installedPackage), expectedInstalledLanguageModelTools);
        // Guard against `expectedInstalledLanguageModelTools` drifting from package.nls.json: resolve every
        // `%key%` placeholder in `expectedSourceLanguageModelTools` against the real nls bundle and assert the
        // result is identical to the hardcoded installed expectations above, so a translated string that isn't
        // mirrored into this test file fails immediately instead of silently passing.
        assert.deepStrictEqual(
            resolveNlsPlaceholders(expectedSourceLanguageModelTools, readSourcePackageNlsJson()),
            expectedInstalledLanguageModelTools);
        assert.deepStrictEqual(sourcePackage.contributes?.mcpServerDefinitionProviders, [{
            id: 'aspire-mcp-server',
            label: 'Aspire',
        }], 'Editor-assistance tools must not add an MCP contribution.');
        assert.deepStrictEqual(
            getLanguageModelTools(sourcePackage).map(tool => tool.name),
            expectedSourceLanguageModelTools.map(tool => tool.name));
        assert.ok(!getLanguageModelTools(sourcePackage).some(tool =>
            tool.name === 'aspire_reveal_resource' ||
            tool.name?.includes('diagnose')));
        assert.deepStrictEqual(getConfigurationKeys(sourcePackage), expectedConfigurationKeys);
        assert.deepStrictEqual(sourceCommandIds, expectedCommandIds);
        assert.deepStrictEqual(installedCommandIds, sourceCommandIds);
        assert.ok(sourceCommandIds.length >= 40, `Expected the package audit to cover the Aspire command surface, got ${sourceCommandIds.length} commands.`);

        for (const commandId of sourceCommandIds) {
            assert.ok(registeredCommands.includes(commandId), `Contributed command '${commandId}' was not registered after extension activation.`);
        }

        assert.ok(installedPackage.contributes?.views?.['aspire-panel']?.some(view => view.id === 'aspire-vscode.appHosts'));
        assert.ok(installedPackage.contributes?.mcpServerDefinitionProviders?.some(provider => provider.id === 'aspire-mcp-server' && provider.label === 'Aspire'));
        assert.ok(installedPackage.contributes?.debuggers?.some(debuggerContribution => debuggerContribution.type === 'aspire'));
        assert.deepStrictEqual(getAspireDebugger(sourcePackage).configurationAttributes?.launch?.required, ['program']);
        assert.ok(installedPackage.contributes?.jsonValidation?.some(validation => getFileMatches(validation.fileMatch).includes('aspire.config.json')));
        assert.ok(installedPackage.contributes?.configuration?.properties?.['aspire.aspireCliExecutablePath']);
        assert.ok(getWalkthroughCompletionEvents(installedPackage).includes('onCommand:aspire-vscode.installCli'));
        const runAppWalkthroughStep = (installedPackage.contributes?.walkthroughs ?? [])
            .flatMap(walkthrough => walkthrough.steps ?? [])
            .find(step => step.id === 'aspire-vscode.getStarted.runApp');
        assert.deepStrictEqual(runAppWalkthroughStep?.completionEvents, [
            'onCommand:aspire-vscode.runAppHostCommand',
            'onCommand:aspire-vscode.debugAppHostCommand',
            'onCommand:aspire-vscode.runAppHostFromExplorer',
            'onCommand:aspire-vscode.debugAppHostFromExplorer',
            'onCommand:aspire-vscode.runAppHostFromEditorCommand',
            'onCommand:aspire-vscode.debugAppHostFromEditorCommand',
        ]);
        assert.ok(sourceCommandIds.includes('aspire-vscode.installCli'));
        assert.ok(installedPackage.activationEvents?.includes('onCommand:aspire-vscode.installCli'));
        assert.ok(sourceCommandIds.includes('aspire-vscode.verifyCliInstalled'));
        assert.ok(installedPackage.activationEvents?.includes('onCommand:aspire-vscode.verifyCliInstalled'));
    });

    test('keeps hidden menus, debugger schema, welcome states, colors, and packaged assets intact', async () => {
        await openAspireView();
        await waitForRepositoryIdle();

        const installedPackage = (await executeE2eControlCommand({ name: 'getExtensionPackageJson' })).result as PackageJson;
        const hiddenPaletteCommands = getHiddenCommandPaletteCommands(installedPackage);
        for (const commandId of [
            'aspire-vscode.createWithAspire',
            'aspire-vscode.runAppHost',
            'aspire-vscode.debugAppHost',
            'aspire-vscode.deployAppHost',
            'aspire-vscode.publishAppHost',
            'aspire-vscode.runPipelineStepAppHost',
            'aspire-vscode.debugPipelineStepAppHost',
            'aspire-vscode.runAppHostFromExplorer',
            'aspire-vscode.debugAppHostFromExplorer',
            'aspire-vscode.runAppHostFromEditorCommand',
            'aspire-vscode.debugAppHostFromEditorCommand',
            'aspire-vscode.refreshAppHosts',
            'aspire-vscode.codeLensRevealResource',
            'aspire-vscode.codeLensRevealAppHost',
            'aspire-vscode.openInIntegratedBrowser',
            'aspire-vscode.copyEndpointUrl',
            'aspire-vscode.openResourceTerminal',
        ]) {
            assert.ok(hiddenPaletteCommands.includes(commandId), `${commandId} should stay hidden from the command palette.`);
        }

        for (const commandId of ['aspire-vscode.new', 'aspire-vscode.openTerminal', 'aspire-vscode.settings']) {
            assert.ok(!hiddenPaletteCommands.includes(commandId), `${commandId} should remain user-facing in the command palette.`);
        }

        const explorerCommands = getMenuCommands(installedPackage, 'explorer/context');
        assert.deepStrictEqual(explorerCommands, ['aspire-vscode.runAppHostFromExplorer', 'aspire-vscode.debugAppHostFromExplorer']);
        assert.deepStrictEqual(Object.keys(installedPackage.contributes?.menus ?? {}).sort(), expectedMenuLocations);
        assert.deepStrictEqual(getMenuCommands(installedPackage, 'editor/title/run'), ['aspire-vscode.runAppHostFromEditorCommand', 'aspire-vscode.debugAppHostFromEditorCommand']);
        assert.deepStrictEqual(getMenuCommands(installedPackage, 'view/title'), ['aspire-vscode.createWithAspire', 'aspire-vscode.switchToGlobalView', 'aspire-vscode.switchToWorkspaceView', 'aspire-vscode.globalRefreshAppHosts', 'aspire-vscode.refreshAppHosts']);
        for (const commandId of expectedViewItemContextCommands) {
            assert.ok(getMenuCommands(installedPackage, 'view/item/context').includes(commandId), `view/item/context should include ${commandId}.`);
        }
        assert.deepStrictEqual(
            getMenuItems(installedPackage, 'view/item/context', expectedAppHostActionCommandIds),
            expectedAppHostActionMenuItems);
        assert.ok(installedPackage.contributes?.viewsContainers?.activitybar?.some(container => container.id === 'aspire-panel' && container.icon === 'resources/aspire-activity-bar.svg'));
        assert.deepStrictEqual((installedPackage.contributes?.viewsWelcome ?? []).map(welcome => welcome.when), expectedWelcomeWhenClauses);
        assert.ok(installedPackage.contributes?.viewsWelcome?.some(welcome =>
            welcome.view === 'aspire-vscode.appHosts' &&
            welcome.contents?.includes('command:aspire-vscode.createWithAspire')));
        assert.ok(installedPackage.contributes?.colors?.some(color => color.id === 'aspire.brandPurple' && color.defaults?.highContrast));

        const debuggerContribution = getAspireDebugger(installedPackage);
        assert.ok(debuggerContribution.configurationAttributes?.launch?.required?.includes('program'));
        assert.deepStrictEqual(debuggerContribution.configurationAttributes?.launch?.properties?.command?.enum, ['run', 'deploy', 'publish', 'do']);
        assert.strictEqual(debuggerContribution.configurationAttributes?.launch?.properties?.args?.items?.type, 'string');
        assert.strictEqual(debuggerContribution.configurationAttributes?.launch?.properties?.env?.additionalProperties?.type, 'string');
        const launchProfileProperty = debuggerContribution.configurationAttributes?.launch?.properties?.launchProfile;
        assert.strictEqual(launchProfileProperty?.type, 'string');
        assert.strictEqual(launchProfileProperty?.description, 'The launch profile to use when running the Aspire AppHost.');
        assert.strictEqual(launchProfileProperty?.minLength, 1);
        assert.ok(debuggerContribution.configurationSnippets?.some(snippet => snippet.body?.type === 'aspire' && snippet.body.program === '${workspaceFolder}'));
        assert.ok(debuggerContribution.initialConfigurations?.some(configuration => configuration.type === 'aspire' && configuration.program === '${workspaceFolder}'));

        const assetStatus = (await executeE2eControlCommand({
            name: 'getExtensionFileStatus',
            relativePaths: [
                installedPackage.icon ?? 'dotnet-aspire-logo-128.png',
                'resources/aspire-activity-bar.svg',
                'schemas/aspire-config.schema.json',
                'schemas/aspire-settings.schema.json',
                'schemas/aspire-global-settings.schema.json',
                ...getWalkthroughMarkdownFiles(installedPackage),
            ],
        })).result as Record<string, boolean>;
        assert.deepStrictEqual(Object.entries(assetStatus).filter(([, exists]) => !exists), []);
    });

    test('applies the shared CLI availability path to visible CLI-dependent package commands', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await setCliUnavailableForE2E(true);

        const commandIds = [
            'aspire-vscode.add',
            'aspire-vscode.new',
            'aspire-vscode.init',
            'aspire-vscode.deploy',
            'aspire-vscode.publish',
            'aspire-vscode.do',
            'aspire-vscode.update',
            'aspire-vscode.openTerminal',
            'aspire-vscode.openLocalSettings',
            'aspire-vscode.openGlobalSettings',
        ];

        for (const commandId of commandIds) {
            const before = getCommandInvocationCount(commandId);
            await executeE2eControlCommand({ name: 'executeAspireCommand', commandId });
            await waitForCommandOutcome(commandId, 'canceled', 60000, before);
        }
    });

    test('routes update self even when the shared CLI availability path would cancel other commands', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await setCliUnavailableForE2E(true);
        await setTerminalCommandExecutionSuppressedForE2E(true);

        const beforeInvocation = getCommandInvocationCount('aspire-vscode.updateSelf');
        const beforeTerminalCommand = getTerminalCommandCount();
        await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: 'aspire-vscode.updateSelf' });
        await waitForCommandOutcome('aspire-vscode.updateSelf', 'success', 60000, beforeInvocation);

        const terminalCommand = await waitForTerminalCommand(
            event => event.executionSuppressed && event.subcommand === 'update --self',
            'update self terminal command',
            60000,
            beforeTerminalCommand);

        assert.strictEqual(terminalCommand.executionSuppressed, true);
    });

    test('routes package terminal and CodeLens commands without executing shell text', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        await setTerminalCommandExecutionSuppressedForE2E(true);

        const appHostPath = getPrimaryAppHostProjectPath();
        const cases: Array<{
            commandId: string;
            args?: readonly unknown[];
            expectedSubcommand: string;
        }> = [
            { commandId: 'aspire-vscode.new', expectedSubcommand: 'new' },
            { commandId: 'aspire-vscode.init', expectedSubcommand: 'init' },
            { commandId: 'aspire-vscode.add', expectedSubcommand: 'add' },
            { commandId: 'aspire-vscode.update', expectedSubcommand: 'update' },
            { commandId: 'aspire-vscode.updateSelf', expectedSubcommand: 'update --self' },
            { commandId: 'aspire-vscode.codeLensViewLogs', args: ['e2e-worker', appHostPath], expectedSubcommand: `logs ${quoteExpectedShellArg('e2e-worker')}` },
            { commandId: 'aspire-vscode.codeLensViewAppHostLogs', args: [appHostPath], expectedSubcommand: 'logs' },
        ];

        for (const item of cases) {
            const beforeInvocation = getCommandInvocationCount(item.commandId);
            const beforeTerminalCommand = getTerminalCommandCount();
            await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: item.commandId, args: item.args });
            await waitForCommandOutcome(item.commandId, 'success', 60000, beforeInvocation);

            const terminalCommand = await waitForTerminalCommand(
                event => event.executionSuppressed && event.subcommand.startsWith(item.expectedSubcommand),
                `${item.commandId} terminal command`,
                60000,
                beforeTerminalCommand);

            assert.strictEqual(terminalCommand.executionSuppressed, true);
        }
    });

    test('routes editor and CodeLens debug commands to the expected Aspire launch types', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        await setDebugLaunchSuppressedForE2E(true);

        const appHostPath = getPrimaryAppHostProjectPath();
        await executeE2eControlCommand({ name: 'openAppHostSource', appHostPath });
        assert.ok((await waitForEditorTitle('AppHost.cs')).includes('AppHost.cs'));

        const cases: Array<{
            commandId: string;
            args?: readonly unknown[];
            expectedCommand: string;
            expectedNoDebug: boolean;
            expectedDoStep?: string;
        }> = [
            { commandId: 'aspire-vscode.runAppHostFromEditorCommand', expectedCommand: 'run', expectedNoDebug: true },
            { commandId: 'aspire-vscode.debugAppHostFromEditorCommand', expectedCommand: 'run', expectedNoDebug: false },
            { commandId: 'aspire-vscode.deploy', expectedCommand: 'deploy', expectedNoDebug: false },
            { commandId: 'aspire-vscode.publish', expectedCommand: 'publish', expectedNoDebug: false },
            { commandId: 'aspire-vscode.codeLensDebugPipelineStep', args: ['deploy'], expectedCommand: 'do', expectedNoDebug: false, expectedDoStep: 'deploy' },
        ];

        for (const item of cases) {
            const beforeInvocation = getCommandInvocationCount(item.commandId);
            const beforeLaunch = getDebugLaunchCount();
            await executeE2eControlCommand({ name: 'executeAspireCommand', commandId: item.commandId, args: item.args });
            await waitForCommandOutcome(item.commandId, 'success', 60000, beforeInvocation);

            const launch = await waitForDebugLaunch(
                event => event.executionSuppressed &&
                    event.command === item.expectedCommand &&
                    event.noDebug === item.expectedNoDebug &&
                    event.doStep === item.expectedDoStep,
                `${item.commandId} debug launch`,
                60000,
                beforeLaunch);

            assert.ok(isSamePath(launch.appHostPath, appHostPath));
        }
    });

    test('reports diagnostics from all packaged Aspire JSON schemas', async () => {
        const probeDirectory = path.join(ensureDiagnosticsDir(), 'schema-probe');
        fs.mkdirSync(probeDirectory, { recursive: true });
        const localSettingsDirectory = path.join(probeDirectory, '.aspire');
        fs.mkdirSync(localSettingsDirectory, { recursive: true });

        const probes = [
            {
                filePath: path.join(probeDirectory, 'aspire.config.json'),
                value: {
                    appHost: { path: 123, extra: true },
                    channel: 1,
                    features: { showAllTemplates: 1 },
                    packages: { package: 1 },
                    profiles: { http: { applicationUrl: 7, environmentVariables: { ASPNETCORE_ENVIRONMENT: 1 } } },
                    sdk: { version: 5 },
                    unknownRoot: true,
                },
            },
            {
                filePath: path.join(localSettingsDirectory, 'settings.json'),
                value: {
                    channel: 1,
                    features: { showAllTemplates: 1 },
                    language: 2,
                    packages: { package: 1 },
                    sdkVersion: 3,
                    unknownRoot: true,
                },
            },
            {
                filePath: path.join(localSettingsDirectory, 'globalsettings.json'),
                value: {
                    channel: 1,
                    features: { showAllTemplates: 1 },
                    language: 2,
                    packages: { package: 1 },
                    sdkVersion: 3,
                    unknownRoot: true,
                },
            },
        ];

        for (const probe of probes) {
            fs.writeFileSync(probe.filePath, JSON.stringify(probe.value, undefined, 2));
        }

        try {
            for (const probe of probes) {
                const diagnostics = await waitForDiagnostics(probe.filePath);
                assert.ok(diagnostics.some(diagnostic => diagnostic.message.length > 0), `Expected schema diagnostics for ${probe.filePath}.`);
            }
        }
        finally {
            removeProbeDirectory(probeDirectory);
        }
    });
});

function removeProbeDirectory(probeDirectory: string): void {
    try {
        fs.rmSync(probeDirectory, {
            recursive: true,
            force: true,
            maxRetries: process.platform === 'win32' ? 20 : 0,
            retryDelay: 250,
        });
    }
    catch (error) {
        if (process.platform === 'win32' && error && typeof error === 'object' && 'code' in error && error.code === 'EBUSY') {
            return;
        }

        throw error;
    }
}

function readSourcePackageJson(): PackageJson {
    return JSON.parse(fs.readFileSync(path.join(getExtensionRoot(), 'package.json'), 'utf8')) as PackageJson;
}

function readSourcePackageNlsJson(): Record<string, string> {
    return JSON.parse(fs.readFileSync(path.join(getExtensionRoot(), 'package.nls.json'), 'utf8')) as Record<string, string>;
}

// Recursively replaces any string of the form '%key%' with `nls[key]`, leaving every other value
// (including strings that are not a bare '%key%' placeholder) untouched. Used to turn the
// %key%-based `expectedSourceLanguageModelTools` fixture into the fully-resolved shape VS Code
// produces once package.nls.json substitution runs, so it can be diffed against the hardcoded
// `expectedInstalledLanguageModelTools` fixture without maintaining the two by hand.
function resolveNlsPlaceholders<T>(value: T, nls: Record<string, string>): T {
    if (typeof value === 'string') {
        const match = /^%(.+)%$/.exec(value);
        if (match) {
            const key = match[1];
            assert.ok(Object.prototype.hasOwnProperty.call(nls, key), `Missing package.nls.json key referenced by test fixture: ${key}`);
            return nls[key] as unknown as T;
        }
        return value;
    }
    if (Array.isArray(value)) {
        return value.map(item => resolveNlsPlaceholders(item, nls)) as unknown as T;
    }
    if (value !== null && typeof value === 'object') {
        const result: Record<string, unknown> = {};
        for (const [key, entryValue] of Object.entries(value as Record<string, unknown>)) {
            result[key] = resolveNlsPlaceholders(entryValue, nls);
        }
        return result as unknown as T;
    }
    return value;
}

function getPackageCommandIds(packageJson: PackageJson): string[] {
    return (packageJson.contributes?.commands ?? [])
        .map(command => command.command)
        .filter(commandId => commandId.startsWith('aspire-vscode.'))
        .sort();
}

function getConfigurationKeys(packageJson: PackageJson): string[] {
    return Object.keys(packageJson.contributes?.configuration?.properties ?? {}).sort();
}

function getLanguageModelTools(packageJson: PackageJson): NonNullable<NonNullable<PackageJson['contributes']>['languageModelTools']> {
    return packageJson.contributes?.languageModelTools ?? [];
}

function getFileMatches(fileMatch: string | string[] | undefined): string[] {
    return typeof fileMatch === 'string' ? [fileMatch] : fileMatch ?? [];
}

function getWalkthroughMarkdownFiles(packageJson: PackageJson): string[] {
    return (packageJson.contributes?.walkthroughs ?? [])
        .flatMap(walkthrough => walkthrough.steps ?? [])
        .map(step => step.media?.markdown)
        .filter((markdown): markdown is string => typeof markdown === 'string');
}

function getWalkthroughCompletionEvents(packageJson: PackageJson): string[] {
    return (packageJson.contributes?.walkthroughs ?? [])
        .flatMap(walkthrough => walkthrough.steps ?? [])
        .flatMap(step => step.completionEvents ?? []);
}

function getHiddenCommandPaletteCommands(packageJson: PackageJson): string[] {
    return (packageJson.contributes?.menus?.commandPalette ?? [])
        .filter(menu => menu.when === 'false' && typeof menu.command === 'string')
        .map(menu => menu.command as string)
        .sort();
}

function getMenuCommands(packageJson: PackageJson, menuId: string): string[] {
    return (packageJson.contributes?.menus?.[menuId] ?? [])
        .map(menu => menu.command)
        .filter((command): command is string => typeof command === 'string');
}

function getMenuItems(packageJson: PackageJson, menuId: string, commandIds: readonly string[]): Array<{ command?: string; when?: string; group?: string }> {
    return (packageJson.contributes?.menus?.[menuId] ?? [])
        .filter(menu => typeof menu.command === 'string' && commandIds.includes(menu.command));
}

function getAspireDebugger(packageJson: PackageJson): NonNullable<NonNullable<PackageJson['contributes']>['debuggers']>[number] {
    const debuggerContribution = packageJson.contributes?.debuggers?.find(candidate => candidate.type === 'aspire');
    assert.ok(debuggerContribution);
    return debuggerContribution;
}

async function waitForDiagnostics(filePath: string, timeoutMs = 60000): Promise<DiagnosticResult[]> {
    const started = Date.now();
    let lastDiagnostics: DiagnosticResult[] = [];
    while (Date.now() - started < timeoutMs) {
        const result = (await executeE2eControlCommand({ name: 'getDiagnostics', filePath })).result;
        lastDiagnostics = Array.isArray(result) ? result as DiagnosticResult[] : [];
        if (lastDiagnostics.length > 0) {
            return lastDiagnostics;
        }

        await delay(500);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for schema diagnostics for ${filePath}. Last diagnostics: ${JSON.stringify(lastDiagnostics, undefined, 2)}`);
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function quoteExpectedShellArg(arg: string): string {
    if (process.platform === 'win32') {
        return `"${arg.replace(/`/g, '``').replace(/"/g, '`"').replace(/\$/g, '`$')}"`;
    }

    return `'${arg.replace(/'/g, "'\"'\"'")}'`;
}

const expectedActivationEvents = [
    'onDebugResolve:aspire',
    'onDebugInitialConfigurations:aspire',
    'onDebugDynamicConfigurations:aspire',
    'workspaceContains:**/*.csproj',
    'workspaceContains:**/*.fsproj',
    'workspaceContains:**/*.vbproj',
    'workspaceContains:**/aspire.config.json',
    'workspaceContains:**/.aspire/**',
    'onView:workbench.view.debug',
    'workspaceContains:**/apphost.cs',
    'workspaceContains:**/apphost.ts',
    'workspaceContains:**/apphost.mts',
    'workspaceContains:**/apphost.cts',
    'workspaceContains:**/apphost.js',
    'workspaceContains:**/apphost.mjs',
    'workspaceContains:**/apphost.cjs',
    'workspaceContains:**/apphost.rs',
    'workspaceContains:**/AppHost.java',
    'workspaceContains:**/apphost.java',
    'workspaceContains:**/src/main/java/AppHost.java',
    'onCommand:aspire-vscode.installCli',
    'onCommand:aspire-vscode.verifyCliInstalled',
    'onLanguageModelTool:aspire_apphost_start',
    'onLanguageModelTool:aspire_apphost_stop',
    'onLanguageModelTool:aspire_debug_session_status',
    'onLanguageModelTool:aspire_explain_launch_failure',
    'onLanguageModelTool:aspire_open_dashboard',
    'onLanguageModelTool:aspire_open_output',
    'onLanguageModelTool:aspire_list_debug_sessions',
    'onLanguageModelTool:aspire_hot_reload_status',
];

const expectedSourceLanguageModelTools = createExpectedLanguageModelTools({
    startDisplayName: '%languageModelTool.aspireAppHostStart.displayName%',
    startModelDescription: '%languageModelTool.aspireAppHostStart.modelDescription%',
    startUserDescription: '%languageModelTool.aspireAppHostStart.userDescription%',
    startModeDescription: '%languageModelTool.aspireAppHostStart.mode.description%',
    startIsolatedDescription: '%languageModelTool.aspireAppHostStart.isolated.description%',
    startLaunchProfileDescription: '%languageModelTool.aspireAppHostStart.launchProfile.description%',
    stopDisplayName: '%languageModelTool.aspireAppHostStop.displayName%',
    stopModelDescription: '%languageModelTool.aspireAppHostStop.modelDescription%',
    stopUserDescription: '%languageModelTool.aspireAppHostStop.userDescription%',
    appHostPathDescription: '%languageModelTool.aspireAppHost.appHostPath.description%',
    statusDisplayName: '%languageModelTool.aspireDebugSessionStatus.displayName%',
    statusModelDescription: '%languageModelTool.aspireDebugSessionStatus.modelDescription%',
    statusUserDescription: '%languageModelTool.aspireDebugSessionStatus.userDescription%',
    resourceNameDescription: '%languageModelTool.aspireDebugSessionStatus.resourceName.description%',
    explainDisplayName: '%languageModelTool.aspireExplainLaunchFailure.displayName%',
    explainModelDescription: '%languageModelTool.aspireExplainLaunchFailure.modelDescription%',
    explainUserDescription: '%languageModelTool.aspireExplainLaunchFailure.userDescription%',
    dashboardDisplayName: '%languageModelTool.aspireOpenDashboard.displayName%',
    dashboardModelDescription: '%languageModelTool.aspireOpenDashboard.modelDescription%',
    dashboardUserDescription: '%languageModelTool.aspireOpenDashboard.userDescription%',
    outputDisplayName: '%languageModelTool.aspireOpenOutput.displayName%',
    outputModelDescription: '%languageModelTool.aspireOpenOutput.modelDescription%',
    outputUserDescription: '%languageModelTool.aspireOpenOutput.userDescription%',
    listDisplayName: '%languageModelTool.aspireListDebugSessions.displayName%',
    listModelDescription: '%languageModelTool.aspireListDebugSessions.modelDescription%',
    listUserDescription: '%languageModelTool.aspireListDebugSessions.userDescription%',
    hotReloadDisplayName: '%languageModelTool.aspireHotReloadStatus.displayName%',
    hotReloadModelDescription: '%languageModelTool.aspireHotReloadStatus.modelDescription%',
    hotReloadUserDescription: '%languageModelTool.aspireHotReloadStatus.userDescription%',
    hotReloadResourceNameDescription: '%languageModelTool.aspireHotReloadStatus.resourceName.description%',
    hotReloadAppHostPathDescription: '%languageModelTool.aspireHotReloadStatus.appHostPath.description%',
});

const expectedInstalledLanguageModelTools = createExpectedLanguageModelTools({
    startDisplayName: 'Start Aspire AppHost',
    startModelDescription: 'Prefer this tool over invoking Aspire AppHost lifecycle commands in a terminal whenever VS Code is active. Start an Aspire AppHost that Aspire has already discovered in the current workspace, using the editor\'s own debug lifecycle. Requires the workspace-relative path of one of the discovered AppHosts; absolute paths are rejected. Also requires whether to start it in \'run\' mode (no debugger attached) or \'debug\' mode (debugger attached). Optional \'isolated\' starts the AppHost with randomized ports and isolated user secrets; when omitted, linked git worktrees start isolated automatically so they do not collide with the primary checkout. Explicit true or false overrides that inference. Optional \'launchProfile\' selects a profile from the AppHost launchSettings.json by its exact name. Does not create, pick, or guess an AppHost: if the path does not name a discovered AppHost, or names more than one, the call fails and the result lists the AppHosts you can pass. If the AppHost is already starting or already running, no second process is started. A successful new launch includes the verified effective \'isolated\' value; idempotent or uncertain results omit it.',
    startUserDescription: 'Start an Aspire AppHost from this workspace in run or debug mode.',
    startModeDescription: 'How to start the AppHost: \'run\' starts it without attaching the debugger, \'debug\' starts it with the debugger attached.',
    startIsolatedDescription: 'When true, start with randomized ports and isolated user secrets. When false, do not isolate. When omitted, linked git worktrees start isolated automatically.',
    startLaunchProfileDescription: 'Optional launch profile name from the AppHost launchSettings.json. Use the profile name exactly as written.',
    stopDisplayName: 'Stop Aspire AppHost',
    stopModelDescription: 'Prefer this tool over invoking Aspire AppHost lifecycle commands in a terminal whenever VS Code is active. Stop a running Aspire AppHost that Aspire has already discovered in the current workspace. Requires the workspace-relative path of one of the discovered AppHosts; absolute paths are rejected. AppHosts started by this editor stop through the coordinated debug lifecycle. AppHosts started outside the editor stop through \'aspire stop --apphost\' for the same discovered path. The extension never kills arbitrary processes. If it cannot determine whether the AppHost is running, the call fails rather than reporting that nothing is running.',
    stopUserDescription: 'Stop a running Aspire AppHost from this workspace.',
    appHostPathDescription: 'Workspace-relative path of an AppHost that Aspire has already discovered in this workspace, for example \'AppHost/AppHost.csproj\' or \'apphost.cs\'. The value must match one of the discovered AppHosts exactly; arbitrary paths, absolute paths, and files Aspire did not discover are rejected. In a multi-root workspace, always prefix the path with the workspace folder name (for example \'backend/AppHost/AppHost.csproj\').',
    statusDisplayName: 'Get Aspire debug session status',
    statusModelDescription: 'Inspect the bounded status of one discovered Aspire AppHost controlled by the editor or an external process, optionally scoped to an exact resource name. Resource results include only resource type, runtime state, health status, exit code, and source; the tool never returns logs, output, process identifiers, debug configurations, URLs, or arbitrary resource data.',
    statusUserDescription: 'Get bounded status for an Aspire AppHost or resource.',
    resourceNameDescription: 'Optional exact Aspire resource name within the selected AppHost. The resource must already exist in the AppHost model.',
    explainDisplayName: 'Explain Aspire launch failure',
    explainModelDescription: 'Explain the latest sanitized launch failure recorded for one discovered AppHost. The result contains bounded failure categories and fixed recommended actions only; it never returns raw errors, logs, output, arguments, environment variables, URLs, process identifiers, or debug configurations.',
    explainUserDescription: 'Explain the latest safely recorded launch failure for an Aspire AppHost.',
    dashboardDisplayName: 'Open Aspire Dashboard',
    dashboardModelDescription: 'Open the Aspire Dashboard for a running discovered AppHost controlled by this editor using any explicitly configured browser or notification presentation, or the external browser by default when none is configured. This changes editor or browser UI. The result reports only whether it opened and the bounded presentation; it never returns the Dashboard URL.',
    dashboardUserDescription: 'Open the Aspire Dashboard for an AppHost controlled by this editor.',
    outputDisplayName: 'Open Aspire Output',
    outputModelDescription: 'Open the VS Code Output panel and select the Aspire Extension output channel in Visual Studio Code after confirmation, without reading or returning its content.',
    outputUserDescription: 'Open the VS Code Output panel and select the Aspire Extension output channel, without returning its content.',
    listDisplayName: 'List Aspire debug sessions',
    listModelDescription: 'List active discovered Aspire AppHosts controlled by this editor as bounded AppHost-level summaries. Results are capped and include only the safe AppHost path, state, mode, and controller; they never read or return child resources, process identifiers, raw debug configurations, output, logs, URLs, environment variables, arguments, or arbitrary session data.',
    listUserDescription: 'List bounded active Aspire AppHosts controlled by this editor.',
    hotReloadDisplayName: 'Get Aspire Hot Reload status',
    hotReloadModelDescription: 'Report whether C# Dev Kit Hot Reload is enabled in this window and whether it is potentially applicable to one Aspire resource, and what to do instead when it is not. Read-only: it never applies, triggers, or verifies a code edit, and never reports that an edit reached a running process. Optionally takes an exact Aspire resource name and the workspace-relative path of a discovered AppHost. When the name is omitted, the tool answers only if exactly one resource is unambiguously debugged by this editor, and otherwise reports that the target is unavailable instead of guessing. When the AppHost path is omitted, every active AppHost controlled by this editor is considered, so pass it to disambiguate a resource name that several editor-controlled AppHosts use. An explicitly selected AppHost controlled by an external process fails closed without reading its resources. Resources this editor does not debug are never Hot Reload applicable. The result contains the enabled state, whether Hot Reload is potentially applicable, who controls the AppHost, bounded evidence identifiers, and an ordered fallback that restarts the affected resource first and rebuilds and restarts the AppHost only when restarting the resource is not enough. It never returns setting values, file paths, URLs, environment variables, process identifiers, logs, debug configurations, or arbitrary resource data.',
    hotReloadUserDescription: 'Report whether Hot Reload is enabled and could apply to an Aspire resource, and what to restart when it cannot.',
    hotReloadResourceNameDescription: 'Optional exact Aspire resource name to report on. The resource must already exist in an active AppHost. When omitted, the tool reports only when exactly one resource is unambiguously debugged by this editor.',
    hotReloadAppHostPathDescription: 'Optional workspace-relative path of an AppHost that Aspire has already discovered in this workspace, for example \'AppHost/AppHost.csproj\' or \'apphost.cs\'. The value must match one of the discovered AppHosts exactly; arbitrary paths, absolute paths, and files Aspire did not discover are rejected. In a multi-root workspace, always prefix the path with the workspace folder name (for example \'backend/AppHost/AppHost.csproj\'). When omitted, the tool looks across every active AppHost controlled by this editor.',
});

function createExpectedLanguageModelTools(strings: {
    startDisplayName: string;
    startModelDescription: string;
    startUserDescription: string;
    startModeDescription: string;
    startIsolatedDescription?: string;
    startLaunchProfileDescription: string;
    stopDisplayName: string;
    stopModelDescription: string;
    stopUserDescription: string;
    appHostPathDescription: string;
    statusDisplayName: string;
    statusModelDescription: string;
    statusUserDescription: string;
    resourceNameDescription: string;
    explainDisplayName: string;
    explainModelDescription: string;
    explainUserDescription: string;
    dashboardDisplayName: string;
    dashboardModelDescription: string;
    dashboardUserDescription: string;
    outputDisplayName: string;
    outputModelDescription: string;
    outputUserDescription: string;
    listDisplayName: string;
    listModelDescription: string;
    listUserDescription: string;
    hotReloadDisplayName: string;
    hotReloadModelDescription: string;
    hotReloadUserDescription: string;
    hotReloadResourceNameDescription: string;
    hotReloadAppHostPathDescription: string;
}) {
    return [
        {
            name: 'aspire_apphost_start',
            toolReferenceName: 'aspireStartAppHost',
            displayName: strings.startDisplayName,
            modelDescription: strings.startModelDescription,
            userDescription: strings.startUserDescription,
            icon: '$(debug-start)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'apphost'],
            inputSchema: {
                type: 'object',
                properties: {
                    appHostPath: { type: 'string', description: strings.appHostPathDescription },
                    mode: {
                        type: 'string',
                        enum: ['run', 'debug'],
                        description: strings.startModeDescription,
                    },
                    isolated: {
                        type: 'boolean',
                        description: strings.startIsolatedDescription,
                    },
                    launchProfile: {
                        type: 'string',
                        minLength: 1,
                        maxLength: 256,
                        description: strings.startLaunchProfileDescription,
                    },
                },
                required: ['appHostPath', 'mode'],
                additionalProperties: false,
            },
        },
        {
            name: 'aspire_apphost_stop',
            toolReferenceName: 'aspireStopAppHost',
            displayName: strings.stopDisplayName,
            modelDescription: strings.stopModelDescription,
            userDescription: strings.stopUserDescription,
            icon: '$(debug-stop)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'apphost'],
            inputSchema: {
                type: 'object',
                properties: {
                    appHostPath: { type: 'string', description: strings.appHostPathDescription },
                },
                required: ['appHostPath'],
                additionalProperties: false,
            },
        },
        {
            name: 'aspire_debug_session_status',
            toolReferenceName: 'aspireDebugSessionStatus',
            displayName: strings.statusDisplayName,
            modelDescription: strings.statusModelDescription,
            userDescription: strings.statusUserDescription,
            icon: '$(debug)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'apphost', 'debug'],
            inputSchema: {
                type: 'object',
                properties: {
                    appHostPath: { type: 'string', description: strings.appHostPathDescription },
                    resourceName: { type: 'string', description: strings.resourceNameDescription },
                },
                required: ['appHostPath'],
                additionalProperties: false,
            },
        },
        {
            name: 'aspire_explain_launch_failure',
            toolReferenceName: 'aspireExplainLaunchFailure',
            displayName: strings.explainDisplayName,
            modelDescription: strings.explainModelDescription,
            userDescription: strings.explainUserDescription,
            icon: '$(lightbulb)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'apphost', 'debug'],
            inputSchema: {
                type: 'object',
                properties: {
                    appHostPath: { type: 'string', description: strings.appHostPathDescription },
                },
                required: ['appHostPath'],
                additionalProperties: false,
            },
        },
        {
            name: 'aspire_open_dashboard',
            toolReferenceName: 'aspireOpenDashboard',
            displayName: strings.dashboardDisplayName,
            modelDescription: strings.dashboardModelDescription,
            userDescription: strings.dashboardUserDescription,
            icon: '$(dashboard)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'apphost', 'dashboard'],
            inputSchema: {
                type: 'object',
                properties: {
                    appHostPath: { type: 'string', description: strings.appHostPathDescription },
                },
                required: ['appHostPath'],
                additionalProperties: false,
            },
        },
        {
            name: 'aspire_open_output',
            toolReferenceName: 'aspireOpenOutput',
            displayName: strings.outputDisplayName,
            modelDescription: strings.outputModelDescription,
            userDescription: strings.outputUserDescription,
            icon: '$(output)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'output'],
            inputSchema: {
                type: 'object',
                properties: {},
                additionalProperties: false,
            },
        },
        {
            name: 'aspire_list_debug_sessions',
            toolReferenceName: 'aspireListDebugSessions',
            displayName: strings.listDisplayName,
            modelDescription: strings.listModelDescription,
            userDescription: strings.listUserDescription,
            icon: '$(list-tree)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'apphost', 'debug'],
            inputSchema: {
                type: 'object',
                properties: {},
                additionalProperties: false,
            },
        },
        {
            name: 'aspire_hot_reload_status',
            toolReferenceName: 'aspireHotReloadStatus',
            displayName: strings.hotReloadDisplayName,
            modelDescription: strings.hotReloadModelDescription,
            userDescription: strings.hotReloadUserDescription,
            icon: '$(flame)',
            canBeReferencedInPrompt: true,
            when: 'isWorkspaceTrusted',
            tags: ['aspire', 'apphost', 'debug'],
            inputSchema: {
                type: 'object',
                properties: {
                    resourceName: { type: 'string', description: strings.hotReloadResourceNameDescription },
                    appHostPath: { type: 'string', description: strings.hotReloadAppHostPathDescription },
                },
                additionalProperties: false,
            },
        },
    ];
}

const expectedCommandIds = [
    'aspire-vscode.add',
    'aspire-vscode.codeLensDebugPipelineStep',
    'aspire-vscode.codeLensOpenDashboard',
    'aspire-vscode.codeLensResourceAction',
    'aspire-vscode.codeLensRevealAppHost',
    'aspire-vscode.codeLensRevealResource',
    'aspire-vscode.codeLensViewAppHostLogs',
    'aspire-vscode.codeLensViewLogs',
    'aspire-vscode.configureLaunchJson',
    'aspire-vscode.copyAppHostPath',
    'aspire-vscode.copyEndpointUrl',
    'aspire-vscode.copyLogFilePath',
    'aspire-vscode.copyResourceName',
    'aspire-vscode.createWithAspire',
    'aspire-vscode.debugAppHost',
    'aspire-vscode.debugAppHostCommand',
    'aspire-vscode.debugAppHostFromEditorCommand',
    'aspire-vscode.debugAppHostFromExplorer',
    'aspire-vscode.debugPipelineStepAppHost',
    'aspire-vscode.deploy',
    'aspire-vscode.deployAppHost',
    'aspire-vscode.do',
    'aspire-vscode.executeResourceCommand',
    'aspire-vscode.executeResourceCommandItem',
    'aspire-vscode.expandAll',
    'aspire-vscode.globalRefreshAppHosts',
    'aspire-vscode.init',
    'aspire-vscode.installCli',
    'aspire-vscode.installDebuggerExtension',
    'aspire-vscode.new',
    'aspire-vscode.openAppHostSource',
    'aspire-vscode.openDashboard',
    'aspire-vscode.openDashboardToSide',
    'aspire-vscode.openGlobalSettings',
    'aspire-vscode.openInExternalBrowser',
    'aspire-vscode.openInIntegratedBrowser',
    'aspire-vscode.openLocalSettings',
    'aspire-vscode.openResourceTerminal',
    'aspire-vscode.openTerminal',
    'aspire-vscode.publish',
    'aspire-vscode.publishAppHost',
    'aspire-vscode.refreshAppHosts',
    'aspire-vscode.restartResource',
    'aspire-vscode.restore',
    'aspire-vscode.runAppHost',
    'aspire-vscode.runAppHostCommand',
    'aspire-vscode.runAppHostFromEditorCommand',
    'aspire-vscode.runAppHostFromExplorer',
    'aspire-vscode.runPipelineStepAppHost',
    'aspire-vscode.settings',
    'aspire-vscode.startResource',
    'aspire-vscode.stopAppHost',
    'aspire-vscode.stopResource',
    'aspire-vscode.switchToGlobalView',
    'aspire-vscode.switchToWorkspaceView',
    'aspire-vscode.update',
    'aspire-vscode.updateSelf',
    'aspire-vscode.viewAppHostLogFile',
    'aspire-vscode.viewAppHostSource',
    'aspire-vscode.viewResourceLogs',
    'aspire-vscode.verifyCliInstalled',
].sort();

const expectedConfigurationKeys = [
    'aspire.appHostDiscoveryTimeoutMs',
    'aspire.appHostsPollingInterval',
    'aspire.aspireCliExecutablePath',
    'aspire.closeDashboardOnDebugEnd',
    'aspire.dashboardBrowser',
    'aspire.enableAspireCliDebugLogging',
    'aspire.enableAspireDashboardAutoLaunch',
    'aspire.enableAspireDcpDebugLogging',
    'aspire.enableAutoRestore',
    'aspire.enableCodeLens',
    'aspire.enableDebugConfigEnvironmentLogging',
    'aspire.enableGutterDecorations',
    'aspire.enableHotReloadNotification',
    'aspire.enableSettingsFileCreationPromptOnStartup',
    'aspire.globalAppHostsPollingInterval',
    'aspire.registerMcpServerInWorkspace',
].sort();

const expectedMenuLocations = [
    'commandPalette',
    'editor/title/run',
    'explorer/context',
    'view/item/context',
    'view/title',
];

const expectedViewItemContextCommands = [
    'aspire-vscode.openDashboard',
    'aspire-vscode.openDashboardToSide',
    'aspire-vscode.expandAll',
    'aspire-vscode.openAppHostSource',
    'aspire-vscode.runAppHost',
    'aspire-vscode.debugAppHost',
    'aspire-vscode.deployAppHost',
    'aspire-vscode.publishAppHost',
    'aspire-vscode.runPipelineStepAppHost',
    'aspire-vscode.debugPipelineStepAppHost',
    'aspire-vscode.stopAppHost',
    'aspire-vscode.copyAppHostPath',
    'aspire-vscode.stopResource',
    'aspire-vscode.startResource',
    'aspire-vscode.restartResource',
    'aspire-vscode.executeResourceCommand',
    'aspire-vscode.executeResourceCommandItem',
    'aspire-vscode.viewResourceLogs',
    'aspire-vscode.openResourceTerminal',
    'aspire-vscode.openInExternalBrowser',
    'aspire-vscode.openInIntegratedBrowser',
    'aspire-vscode.copyEndpointUrl',
    'aspire-vscode.copyResourceName',
    'aspire-vscode.viewAppHostSource',
    'aspire-vscode.viewAppHostLogFile',
    'aspire-vscode.copyLogFilePath',
];

const expectedAppHostActionCommandIds = [
    'aspire-vscode.deployAppHost',
    'aspire-vscode.publishAppHost',
    'aspire-vscode.runPipelineStepAppHost',
    'aspire-vscode.debugPipelineStepAppHost',
];

const expectedAppHostActionMenuItems = [
    {
        command: 'aspire-vscode.deployAppHost',
        when: 'view == aspire-vscode.appHosts && viewItem =~ /^(appHost|workspaceResources:hasAppHost|workspaceAppHost):canDeploy(:|$)/',
        group: '2_actions@3',
    },
    {
        command: 'aspire-vscode.publishAppHost',
        when: 'view == aspire-vscode.appHosts && viewItem =~ /^(appHost|workspaceResources:hasAppHost|workspaceAppHost)(:canDeploy)?:canPublish(:|$)/',
        group: '2_actions@4',
    },
    {
        command: 'aspire-vscode.runPipelineStepAppHost',
        when: 'view == aspire-vscode.appHosts && viewItem =~ /^(appHost|workspaceResources:hasAppHost|workspaceAppHost)(:canDeploy)?(:canPublish)?:canDo$/',
        group: '2_actions@5',
    },
    {
        command: 'aspire-vscode.debugPipelineStepAppHost',
        when: 'view == aspire-vscode.appHosts && viewItem =~ /^(appHost|workspaceResources:hasAppHost|workspaceAppHost)(:canDeploy)?(:canPublish)?:canDo$/',
        group: '2_actions@6',
    },
];

const expectedWelcomeWhenClauses = [
    'aspire.loading',
    'aspire.noAppHosts && !aspire.fetchAppHostsError && !aspire.loading && aspire.viewMode != \'global\'',
    'aspire.noAppHosts && !aspire.fetchAppHostsError && !aspire.loading && aspire.viewMode == \'global\'',
    'aspire.fetchAppHostsError && aspire.fetchAppHostsCompatibilityError && !aspire.loading',
    'aspire.fetchAppHostsError && !aspire.fetchAppHostsCompatibilityError && !aspire.loading',
];
