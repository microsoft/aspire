import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, getDebugLaunchCount, getTerminalCommandCount, isSamePath, waitForCommandOutcome, waitForDebugLaunch, waitForRepositoryIdle, waitForTerminalCommand, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, setCliUnavailableForE2E, setDebugLaunchSuppressedForE2E, setTerminalCommandExecutionSuppressedForE2E } from './helpers/fixtures';
import { ensureDiagnosticsDir, getExtensionRoot, getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView, waitForEditorTitle } from './helpers/vscode';

interface PackageJson {
    name?: string;
    version?: string;
    preRelease?: boolean;
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
                    properties?: Record<string, { type?: string; enum?: string[]; items?: { type?: string }; additionalProperties?: { type?: string } }>;
                };
            };
            configurationSnippets?: Array<{ body?: Record<string, unknown> }>;
            initialConfigurations?: Array<Record<string, unknown>>;
        }>;
        walkthroughs?: Array<{ steps?: Array<{ media?: { markdown?: string }; completionEvents?: string[] }> }>;
        colors?: Array<{ id?: string; defaults?: Record<string, string> }>;
        mcpServerDefinitionProviders?: Array<{ id?: string; label?: string }>;
    };
}

interface DiagnosticResult {
    message: string;
    severity: number;
    code?: string | number;
}

interface AspireExtensionEnvironment {
    version?: string;
    channel?: string;
    source?: string;
    hasConfiguredExtensionGallery?: boolean;
}

type AspireExtensionChannel = 'stable' | 'prerelease';

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
        const extensionEnvironment = (await executeE2eControlCommand({ name: 'getAspireExtensionEnvironment' })).result as AspireExtensionEnvironment;
        const registeredCommands = (await executeE2eControlCommand({ name: 'getRegisteredAspireCommands' })).result as string[];
        const requireConfiguredGallery = process.env.ASPIRE_EXTENSION_E2E_USE_CONFIGURED_GALLERY === 'true';
        const hasConfiguredExtensionGallery = extensionEnvironment.hasConfiguredExtensionGallery;

        assert.strictEqual(installedPackage.preRelease, false, 'Offline VSIX installation should exercise the package-time channel marker rather than Marketplace metadata.');
        assert.strictEqual(typeof hasConfiguredExtensionGallery, 'boolean');
        // Organization policy can configure the gallery even when the isolated user and workspace
        // settings do not. The opt-in row must take the configured path, while the control row must
        // remain consistent with whatever effective policy the Extension Host receives.
        if (requireConfiguredGallery) {
            assert.strictEqual(hasConfiguredExtensionGallery, true);
        }
        assert.deepStrictEqual(extensionEnvironment, {
            version: installedPackage.version,
            channel: getExpectedExtensionChannel(),
            source: hasConfiguredExtensionGallery ? 'other' : 'microsoft-marketplace',
            hasConfiguredExtensionGallery,
        });
        const sourceCommandIds = getPackageCommandIds(sourcePackage);
        const installedCommandIds = getPackageCommandIds(installedPackage);
        assert.deepStrictEqual(sourcePackage.activationEvents, expectedActivationEvents);
        assert.deepStrictEqual(getLanguageModelTools(sourcePackage), expectedSourceLanguageModelTools);
        assert.deepStrictEqual(getLanguageModelTools(installedPackage), expectedInstalledLanguageModelTools);
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
            'aspire-vscode.runAppHost',
            'aspire-vscode.debugAppHost',
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
        assert.deepStrictEqual(explorerCommands, ['aspire-vscode.runAppHostCommand', 'aspire-vscode.debugAppHostCommand']);
        assert.deepStrictEqual(Object.keys(installedPackage.contributes?.menus ?? {}).sort(), expectedMenuLocations);
        assert.deepStrictEqual(getMenuCommands(installedPackage, 'editor/title/run'), ['aspire-vscode.runAppHostCommand', 'aspire-vscode.debugAppHostCommand']);
        assert.deepStrictEqual(getMenuCommands(installedPackage, 'view/title'), ['aspire-vscode.switchToGlobalView', 'aspire-vscode.switchToWorkspaceView', 'aspire-vscode.globalRefreshAppHosts', 'aspire-vscode.refreshAppHosts']);
        for (const commandId of expectedViewItemContextCommands) {
            assert.ok(getMenuCommands(installedPackage, 'view/item/context').includes(commandId), `view/item/context should include ${commandId}.`);
        }
        assert.ok(installedPackage.contributes?.viewsContainers?.activitybar?.some(container => container.id === 'aspire-panel' && container.icon === 'resources/aspire-activity-bar.svg'));
        assert.deepStrictEqual((installedPackage.contributes?.viewsWelcome ?? []).map(welcome => welcome.when), expectedWelcomeWhenClauses);
        assert.ok(installedPackage.contributes?.colors?.some(color => color.id === 'aspire.brandPurple' && color.defaults?.highContrast));

        const debuggerContribution = getAspireDebugger(installedPackage);
        assert.ok(debuggerContribution.configurationAttributes?.launch?.required?.includes('program'));
        assert.deepStrictEqual(debuggerContribution.configurationAttributes?.launch?.properties?.command?.enum, ['run', 'deploy', 'publish', 'do']);
        assert.strictEqual(debuggerContribution.configurationAttributes?.launch?.properties?.args?.items?.type, 'string');
        assert.strictEqual(debuggerContribution.configurationAttributes?.launch?.properties?.env?.additionalProperties?.type, 'string');
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
            { commandId: 'aspire-vscode.runAppHostCommand', expectedCommand: 'run', expectedNoDebug: true },
            { commandId: 'aspire-vscode.debugAppHostCommand', expectedCommand: 'run', expectedNoDebug: false },
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

function getExpectedExtensionChannel(): AspireExtensionChannel {
    const channel = process.env.ASPIRE_EXTENSION_E2E_EXPECTED_CHANNEL;
    assert.ok(
        channel === 'stable' || channel === 'prerelease',
        `ASPIRE_EXTENSION_E2E_EXPECTED_CHANNEL must be 'stable' or 'prerelease'. Actual value: ${channel ?? '(unset)'}.`);
    return channel;
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
];

const expectedSourceLanguageModelTools = createExpectedLanguageModelTools({
    startDisplayName: '%languageModelTool.aspireAppHostStart.displayName%',
    startModelDescription: '%languageModelTool.aspireAppHostStart.modelDescription%',
    startUserDescription: '%languageModelTool.aspireAppHostStart.userDescription%',
    startModeDescription: '%languageModelTool.aspireAppHostStart.mode.description%',
    startIsolatedDescription: '%languageModelTool.aspireAppHostStart.isolated.description%',
    stopDisplayName: '%languageModelTool.aspireAppHostStop.displayName%',
    stopModelDescription: '%languageModelTool.aspireAppHostStop.modelDescription%',
    stopUserDescription: '%languageModelTool.aspireAppHostStop.userDescription%',
    appHostPathDescription: '%languageModelTool.aspireAppHost.appHostPath.description%',
});

const expectedInstalledLanguageModelTools = createExpectedLanguageModelTools({
    startDisplayName: 'Start Aspire AppHost',
    startModelDescription: 'Prefer this tool over invoking Aspire AppHost lifecycle commands in a terminal whenever VS Code is active. Start an Aspire AppHost that Aspire has already discovered in the current workspace, using the editor\'s own debug lifecycle. Requires the workspace-relative path of one of the discovered AppHosts; absolute paths are rejected. Also requires whether to start it in \'run\' mode (no debugger attached) or \'debug\' mode (debugger attached). Optional \'isolated\' starts the AppHost with randomized ports and isolated user secrets; when omitted, linked git worktrees start isolated automatically so they do not collide with the primary checkout. Explicit true or false overrides that inference. Does not create, pick, or guess an AppHost: if the path does not name a discovered AppHost, or names more than one, the call fails and the result lists the AppHosts you can pass. If the AppHost is already starting or already running, no second process is started. A successful new launch includes the verified effective \'isolated\' value; idempotent or uncertain results omit it.',
    startUserDescription: 'Start an Aspire AppHost from this workspace in run or debug mode.',
    startModeDescription: 'How to start the AppHost: \'run\' starts it without attaching the debugger, \'debug\' starts it with the debugger attached.',
    startIsolatedDescription: 'When true, start with randomized ports and isolated user secrets. When false, do not isolate. When omitted, linked git worktrees start isolated automatically.',
    stopDisplayName: 'Stop Aspire AppHost',
    stopModelDescription: 'Prefer this tool over invoking Aspire AppHost lifecycle commands in a terminal whenever VS Code is active. Stop a running Aspire AppHost that Aspire has already discovered in the current workspace. Requires the workspace-relative path of one of the discovered AppHosts; absolute paths are rejected. AppHosts started by this editor stop through the coordinated debug lifecycle. AppHosts started outside the editor stop through \'aspire stop --apphost\' for the same discovered path. The extension never kills arbitrary processes. If it cannot determine whether the AppHost is running, the call fails rather than reporting that nothing is running.',
    stopUserDescription: 'Stop a running Aspire AppHost from this workspace.',
    appHostPathDescription: 'Workspace-relative path of an AppHost that Aspire has already discovered in this workspace, for example \'AppHost/AppHost.csproj\' or \'apphost.cs\'. The value must match one of the discovered AppHosts exactly; arbitrary paths, absolute paths, and files Aspire did not discover are rejected. In a multi-root workspace, always prefix the path with the workspace folder name (for example \'backend/AppHost/AppHost.csproj\').',
});

function createExpectedLanguageModelTools(strings: {
    startDisplayName: string;
    startModelDescription: string;
    startUserDescription: string;
    startModeDescription: string;
    startIsolatedDescription?: string;
    stopDisplayName: string;
    stopModelDescription: string;
    stopUserDescription: string;
    appHostPathDescription: string;
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
    'aspire-vscode.debugAppHost',
    'aspire-vscode.debugAppHostCommand',
    'aspire-vscode.deploy',
    'aspire-vscode.do',
    'aspire-vscode.executeResourceCommand',
    'aspire-vscode.executeResourceCommandItem',
    'aspire-vscode.expandAll',
    'aspire-vscode.globalRefreshAppHosts',
    'aspire-vscode.init',
    'aspire-vscode.installCli',
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
    'aspire-vscode.refreshAppHosts',
    'aspire-vscode.restartResource',
    'aspire-vscode.restore',
    'aspire-vscode.runAppHost',
    'aspire-vscode.runAppHostCommand',
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

const expectedWelcomeWhenClauses = [
    'aspire.loading',
    'aspire.noAppHosts && !aspire.fetchAppHostsError && !aspire.loading && aspire.viewMode != \'global\'',
    'aspire.noAppHosts && !aspire.fetchAppHostsError && !aspire.loading && aspire.viewMode == \'global\'',
    'aspire.fetchAppHostsError && aspire.fetchAppHostsCompatibilityError && !aspire.loading',
    'aspire.fetchAppHostsError && !aspire.fetchAppHostsCompatibilityError && !aspire.loading',
];
