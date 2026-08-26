import * as vscode from 'vscode';
import { AppHostResourceParser, getParserForDocument } from './parsers/AppHostResourceParser';
import { filterActiveOffsetsInPlainText } from './parsers/plainTextInactiveOffsets';
// Import parsers to trigger self-registration
import './parsers/csharpAppHostParser';
import './parsers/javaAppHostParser';
import './parsers/jsTsAppHostParser';
import './parsers/rustAppHostParser';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { isCommandVisibleToUi, isEnabledCommand } from '../views/treePresentation';
import { compareResourceCommands, getParameterValueDescription, getResourceStateDescription } from '../utils/resourceDisplay';
import { AppHostDataRepository, ResourceJson, AppHostDisplayInfo, ResourceCommandJson } from '../data/AppHostDataRepository';
import { findResourceState, findWorkspaceResourceState, matchesAppHostPathOrDirectory } from './resourceStateUtils';
import { extensionLogOutputChannel } from '../utils/logging';
import { ResourceState, HealthStatus, StateStyle, ResourceType } from './resourceConstants';
import {
    codeLensDebugPipelineStep,
    codeLensResourceRunning,
    codeLensResourceRunningWarning,
    codeLensResourceRunningError,
    codeLensResourceStarting,
    codeLensResourceStopping,
    codeLensResourceNotStarted,
    codeLensResourceWaiting,
    codeLensResourceStopped,
    codeLensResourceStoppedWithExitCode,
    codeLensResourceStoppedError,
    codeLensResourceStoppedErrorWithExitCode,
    codeLensResourceFailedToStart,
    codeLensResourceFailedToStartError,
    codeLensResourceRuntimeUnhealthy,
    codeLensRestart,
    codeLensStop,
    codeLensStart,
    codeLensViewLogs,
    codeLensCommand,
    codeLensOpenDashboard,
    codeLensViewAppHostLogs,
    codeLensRustAppHostAlreadyRunning,
    codeLensRustAppHostAlreadyRunningTooltip,
    codeLensRustAppHostUseAspire,
    codeLensRustAppHostUseAspireTooltip,
    codeLensJavaAppHostAlreadyRunning,
    codeLensJavaAppHostAlreadyRunningTooltip,
    codeLensJavaAppHostUseAspire,
    codeLensJavaAppHostUseAspireTooltip,
    codeLensSpringBootDashboardBypassesAspire,
    codeLensSpringBootDashboardBypassesAspireTooltip,
    codeLensResourceValueMissing,
    codeLensSetUpDebugger,
    debuggerSetupNotification,
} from '../loc/strings';
import { DebuggerInstallHint, getDebuggerInstallHintForResource } from '../debugger/debuggerInstallHints';

/**
 * Extension that contributes the Spring Boot Dashboard view. Its Run/Debug buttons start the
 * application directly, which is the hazard the Spring Boot warning lens exists to flag.
 */
const springBootDashboardExtensionId = 'vscjava.vscode-spring-boot-dashboard';

/**
 * Matches an AppHost statement that launches a Java resource through Spring Boot's own plugin, e.g.
 *
 *   builder.AddJavaApp("api", "../api").WithMavenGoal("spring-boot:run");   // C#
 *   builder.addJavaApp('api', '../api').withGradleTask("bootRun");          // JS/TS
 *   builder.add_java_app("api", "../api")?.with_maven_goal("spring-boot:run")?;  // Rust
 *
 * AddSpringBootApp is matched on the call itself because it configures the same `spring-boot:run` or
 * `bootRun` launch internally, choosing between them from the build file. It is the form the README
 * leads with, so matching only the explicit goal would miss the common case entirely.
 *
 *   builder.AddSpringBootApp("catalog", "../catalog");                      // C#
 *   builder.add_spring_boot_app("catalog", "../catalog")?;                  // Rust, Python
 *
 * The optional underscores plus the case-insensitive flag cover all three casings from one pattern,
 * which matters because the warning is about the Java *resource* and therefore has to work no matter
 * which language the AppHost is written in. C# verbatim/interpolated prefixes are allowed on the
 * literal; raw string literals are not, because nothing in the goal or task name needs escaping.
 *
 * AddQuarkusApp is deliberately absent: the warning names the Spring Boot Dashboard, which does not
 * offer to run a Quarkus resource.
 */
const springBootLaunchPattern = /\b(?:with_?(?:maven_?goal|gradle_?task)\s*\(\s*[@$]*(['"])(?:spring-boot:run|bootRun)\1|add_?spring_?boot_?app\s*\()/gi;

/**
 * Per-language warning shown on the AppHost entry point, for languages whose ecosystem extension
 * offers a Run/Debug action that starts the AppHost outside Aspire. Running an AppHost that way
 * starts it with no Aspire session at all, so no resources launch and no dashboard appears -- quiet
 * enough to look like the AppHost itself is broken.
 *
 * Unlike the Spring Boot Dashboard warning this is not gated on the other extension being installed.
 * Neither rust-analyzer nor the Java extension pack is realistically absent when someone is editing
 * an AppHost in that language, and an ungated warning is also what lets the E2E harness assert on it
 * without standing up a language server first.
 */
const entryPointWarningsByLanguage: ReadonlyMap<string, {
    readonly alreadyRunning: string;
    readonly alreadyRunningTooltip: string;
    readonly useAspire: string;
    readonly useAspireTooltip: string;
}> = new Map([
    ['rust', {
        alreadyRunning: codeLensRustAppHostAlreadyRunning,
        alreadyRunningTooltip: codeLensRustAppHostAlreadyRunningTooltip,
        useAspire: codeLensRustAppHostUseAspire,
        useAspireTooltip: codeLensRustAppHostUseAspireTooltip,
    }],
    ['java', {
        alreadyRunning: codeLensJavaAppHostAlreadyRunning,
        alreadyRunningTooltip: codeLensJavaAppHostAlreadyRunningTooltip,
        useAspire: codeLensJavaAppHostUseAspire,
        useAspireTooltip: codeLensJavaAppHostUseAspireTooltip,
    }],
]);

/**
 * The conventional AppHost file name for each language that has no resource parser, matched
 * case-insensitively to mirror the CLI's own detection.
 *
 * The parser-backed languages are already narrowed by their parser, which only accepts a document it
 * can parse as an AppHost. The parserless languages have nothing playing that role, so without this the
 * provider would scan every Python and Go file in the workspace. Gating on the file name rather
 * than on a discovered AppHost is deliberate: the warning is most useful before the AppHost has ever
 * been run through Aspire, which is exactly when discovery has nothing to match against.
 */
const parserlessAppHostFileNames: ReadonlyMap<string, string> = new Map([
    ['python', 'apphost.py'],
    ['go', 'apphost.go'],
]);

function isParserlessAppHostDocument(document: vscode.TextDocument): boolean {
    const expected = parserlessAppHostFileNames.get(document.languageId);
    if (expected === undefined) {
        return false;
    }

    // uri.path is always '/'-separated, including on Windows, so this does not need the platform separator.
    const fileName = document.uri.path.split('/').pop() ?? '';

    return fileName.toLowerCase() === expected;
}

export class AspireCodeLensProvider implements vscode.CodeLensProvider {
    private readonly _onDidChangeCodeLenses = new vscode.EventEmitter<void>();
    readonly onDidChangeCodeLenses = this._onDidChangeCodeLenses.event;

    private _disposables: vscode.Disposable[] = [];

    constructor(
        private readonly _treeProvider: AspireAppHostTreeProvider,
        private readonly _dataRepository: AppHostDataRepository,
        private readonly _isExtensionInstalled: (extensionId: string) => boolean = extensionId => vscode.extensions.getExtension(extensionId) !== undefined,
    ) {
        // Re-compute lenses whenever the polling data changes
        this._disposables.push(
            _treeProvider.onDidChangeTreeData(() => this._onDidChangeCodeLenses.fire()),
            vscode.extensions.onDidChange(() => this._onDidChangeCodeLenses.fire()),
        );
    }

    provideCodeLenses(document: vscode.TextDocument, token: vscode.CancellationToken): vscode.ProviderResult<vscode.CodeLens[]> {
        // A parser failure (a tree-sitter grammar that will not load, an unparseable document) would
        // otherwise surface as an editor with no lenses and nothing written anywhere, which is
        // indistinguishable from "this document legitimately has no lenses". Log it and degrade to no
        // lenses rather than letting the rejection disable the provider for the rest of the session.
        return this._provideCodeLensesAsync(document, token).then(lenses => {
            extensionLogOutputChannel.debug(`Computed ${lenses?.length ?? 0} Aspire CodeLens(es) for ${document.uri.fsPath} (languageId '${document.languageId}')${lenses?.length ? `: ${lenses.map(lens => lens.command?.title).join(' | ')}` : ''}`);
            return lenses;
        }).catch(error => {
            extensionLogOutputChannel.error(`Failed to compute Aspire CodeLenses for ${document.uri.fsPath}: ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
            return [];
        });
    }

    private async _provideCodeLensesAsync(document: vscode.TextDocument, token: vscode.CancellationToken): Promise<vscode.CodeLens[] | undefined> {
        if (!vscode.workspace.getConfiguration('aspire').get<boolean>('enableCodeLens', true)) {
            return [];
        }

        const parser = await getParserForDocument(document);
        if (token.isCancellationRequested) {
            return undefined;
        }

        if (!parser) {
            // Python and Go AppHosts have no resource parser, so none of the state or action
            // lenses apply. The Spring Boot warning still does: it is about the Java resource being
            // declared, not about the language declaring it.
            if (!isParserlessAppHostDocument(document)) {
                return [];
            }

            const warningOnlyLenses: vscode.CodeLens[] = [];
            await this._addSpringBootDashboardLenses(warningOnlyLenses, document, parser);

            return warningOnlyLenses;
        }

        const resources = await parser.parseResources(document);
        if (token.isCancellationRequested) {
            return undefined;
        }

        const appHosts = this._treeProvider.appHosts;
        const workspaceResources = this._treeProvider.workspaceResources;
        const workspaceAppHost = this._treeProvider.workspaceAppHost;
        const workspaceAppHostPath = this._treeProvider.workspaceAppHostPath ?? '';
        const globalAppHost = this._resolveGlobalAppHostForDocument(document, appHosts);
        const workspaceAppHostMatchesDocument = workspaceAppHostPath !== '' && this._documentMatchesAppHostPath(document, workspaceAppHostPath);
        const hasRunningData = globalAppHost !== undefined || (workspaceAppHostMatchesDocument && (workspaceResources.length > 0 || workspaceAppHost !== undefined));
        const findWorkspace = workspaceAppHostMatchesDocument
            ? findWorkspaceResourceState(workspaceResources, workspaceAppHostPath)
            : () => undefined;

        const lenses: vscode.CodeLens[] = [];

        // Builder-statement lenses (Open Dashboard + View Logs) appear only when this
        // document maps to a concretely-running AppHost — independent of whether any
        // Add* resource calls were found in the file.
        await this._addBuilderStatementLenses(lenses, document, parser, workspaceAppHostPath, workspaceResources);

        await this._addSpringBootDashboardLenses(lenses, document, parser);

        if (resources.length === 0) {
            return lenses;
        }

        for (const resource of resources) {
            // For pipeline steps the whole statement maps to a single Add*(...) call, so
            // anchoring at the top of the chain reads naturally.
            //
            // For resources, a single fluent chain can declare several (e.g.
            // `builder.AddPostgres("pg").AddDatabase("db")`). If we collapsed all of those
            // to the chain's start line their state/action lenses would stack on the same
            // line and the user couldn't tell which "Stopped" / which "Stop" belongs to
            // which resource. So when more than one resource shares a statement we anchor
            // each at its own call line; when a chain declares just one resource we use
            // the statement-start line so the lens sits above the whole declaration
            // (e.g. above `const nodePlayer = await builder` rather than between that
            // line and the `.addNodeApp(...)` call).
            const statementStart = resource.statementStartLine ?? resource.range.start.line;
            const sharedWithOthers = resource.kind === 'resource'
                && resources.some(other =>
                    other !== resource
                    && other.kind === 'resource'
                    && (other.statementStartLine ?? other.range.start.line) === statementStart);
            const lensLine = (resource.kind === 'pipelineStep' || !sharedWithOthers)
                ? statementStart
                : resource.range.start.line;
            const lineRange = new vscode.Range(lensLine, 0, lensLine, 0);

            if (resource.kind === 'pipelineStep') {
                // Pipeline steps get Debug lens when no AppHost is running
                if (!hasRunningData) {
                    this._addPipelineStepLenses(lenses, lineRange, resource.name);
                }
            } else if (resource.kind === 'resource') {
                // Resources get state lenses when live data is available
                if (hasRunningData) {
                    const match = (globalAppHost ? findResourceState([globalAppHost], resource.name) : undefined)
                        ?? findWorkspace(resource.name);
                    if (match) {
                        this._addStateLenses(lenses, lineRange, match.resource, match.appHost);
                        const debuggerInstallHint = getDebuggerInstallHintForResource(match.resource);
                        if (match.resource.state === ResourceState.Running && debuggerInstallHint) {
                            this._addDebuggerInstallHintLens(lenses, lineRange, debuggerInstallHint);
                        }
                    }
                }
            }
        }

        return lenses;
    }

    private _addPipelineStepLenses(lenses: vscode.CodeLens[], range: vscode.Range, stepName: string): void {
        lenses.push(new vscode.CodeLens(range, {
            title: codeLensDebugPipelineStep,
            command: 'aspire-vscode.codeLensDebugPipelineStep',
            tooltip: codeLensDebugPipelineStep,
            arguments: [stepName],
        }));
    }

    private _addDebuggerInstallHintLens(lenses: vscode.CodeLens[], range: vscode.Range, hint: DebuggerInstallHint): void {
        lenses.push(new vscode.CodeLens(range, {
            title: codeLensSetUpDebugger(hint.debuggerName),
            command: 'aspire-vscode.installDebuggerExtension',
            tooltip: debuggerSetupNotification(hint.debuggerName),
            arguments: [hint],
        }));
    }

    /**
     * Warns, on each Java resource launched through Spring Boot's Maven plugin or Gradle task, that
     * the Spring Boot Dashboard's Run/Debug buttons start the app outside Aspire.
     *
     * Only shown when that extension is installed: unlike rust-analyzer on a Rust AppHost, the
     * Spring Boot Dashboard is not implied by the presence of a Java resource, and warning about an
     * extension the user does not have is noise.
     *
     * The scan is textual rather than parser-driven because a Java resource can be declared from any
     * AppHost language and the goal or task name is the only reliable in-document signal that the
     * resource is a Spring Boot app. Deciding it from the project's own `pom.xml`/`build.gradle`
     * would mean reading files off disk on every keystroke. The matches are then filtered through the
     * document's parser so a commented-out or quoted example does not produce a warning about a
     * resource that does not exist.
     */
    private async _addSpringBootDashboardLenses(
        lenses: vscode.CodeLens[],
        document: vscode.TextDocument,
        parser: AppHostResourceParser | undefined,
    ): Promise<void> {
        if (!this._isExtensionInstalled(springBootDashboardExtensionId)) {
            return;
        }

        const text = document.getText();
        // Shared regex literals keep `lastIndex` between calls, so reset before iterating.
        springBootLaunchPattern.lastIndex = 0;

        const offsets: number[] = [];
        let match: RegExpExecArray | null;
        while ((match = springBootLaunchPattern.exec(text)) !== null) {
            offsets.push(match.index);
        }

        if (offsets.length === 0) {
            return;
        }

        // Without a parser the same question is answered by scanning the text. Skipping the filter
        // entirely would make a commented-out call warn, which is the behaviour the parser-backed
        // languages deliberately do not have.
        const activeOffsets = parser?.filterActiveOffsets
            ? await parser.filterActiveOffsets(document, offsets)
            : filterActiveOffsetsInPlainText(document.languageId, text, offsets);

        const warnedLines = new Set<number>();
        for (const offset of activeOffsets) {
            const line = document.positionAt(offset).line;
            if (warnedLines.has(line)) {
                continue;
            }

            warnedLines.add(line);
            const range = new vscode.Range(line, 0, line, 0);
            // An empty command id renders the warning as plain text rather than an inert link.
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensSpringBootDashboardBypassesAspire,
                command: '',
                tooltip: codeLensSpringBootDashboardBypassesAspireTooltip,
            }));
        }
    }

    private async _addBuilderStatementLenses(
        lenses: vscode.CodeLens[],
        document: vscode.TextDocument,
        parser: AppHostResourceParser,
        workspaceAppHostPath: string,
        workspaceResources: readonly ResourceJson[],
    ): Promise<void> {
        const builderLine = await parser.findBuilderStatementLine?.(document);
        if (builderLine === undefined) {
            return;
        }

        const runningAppHostPath = this._resolveAppHostPathForDocument(document, workspaceAppHostPath, workspaceResources);

        const entryPointWarning = entryPointWarningsByLanguage.get(document.languageId);
        if (entryPointWarning) {
            const entryPointLine = await parser.findAppHostEntryPointLine?.(document) ?? builderLine;
            const range = new vscode.Range(entryPointLine, 0, entryPointLine, 0);
            // The tree only holds running AppHosts, so revealing a stopped one has nothing to select.
            // An empty command id makes VS Code render the warning as plain text instead of a link
            // whose click does nothing.
            lenses.push(new vscode.CodeLens(range, runningAppHostPath
                ? {
                    title: entryPointWarning.alreadyRunning,
                    command: 'aspire-vscode.codeLensRevealAppHost',
                    tooltip: entryPointWarning.alreadyRunningTooltip,
                    arguments: [runningAppHostPath],
                }
                : {
                    title: entryPointWarning.useAspire,
                    command: '',
                    tooltip: entryPointWarning.useAspireTooltip,
                }));
        }

        // Dashboard and log actions require a concretely-running AppHost path. In particular,
        // C# source documents cannot safely fall back to their sibling source path here because
        // the CLI expects the project path.
        if (runningAppHostPath === undefined) {
            return;
        }

        const range = new vscode.Range(builderLine, 0, builderLine, 0);

        lenses.push(new vscode.CodeLens(range, {
            title: codeLensOpenDashboard,
            command: 'aspire-vscode.codeLensOpenDashboard',
            tooltip: codeLensOpenDashboard,
            arguments: [runningAppHostPath],
        }));

        lenses.push(new vscode.CodeLens(range, {
            title: codeLensViewAppHostLogs,
            command: 'aspire-vscode.codeLensViewAppHostLogs',
            tooltip: codeLensViewAppHostLogs,
            arguments: [runningAppHostPath],
        }));
    }

    /**
     * Resolves the running-AppHost path that the given document represents, or
     * `undefined` when the document cannot be tied to a running host.
     *
     * Resolution order:
     *  1. Exact path or same-directory match against {@link AppHostDataRepository.appHosts}
     *     (covers global mode and any workspace AppHosts that surface there).
     *  2. The repository's `workspaceAppHostPath` when workspace live data identifies
     *     a running AppHost and the document lives in the same directory as that AppHost.
     *
     * The document path itself is intentionally not used as a fallback — for C#
     * AppHosts the CLI requires a `.csproj`, not a `.cs` file.
     */
    private _resolveAppHostPathForDocument(
        document: vscode.TextDocument,
        workspaceAppHostPath: string,
        workspaceResources: readonly ResourceJson[],
    ): string | undefined {
        const docPath = document.uri.fsPath;
        const match = this._dataRepository.appHosts.find(host => {
            const hostPath = host.appHostPath;
            return matchesAppHostPathOrDirectory(docPath, hostPath);
        });
        if (match) {
            return match.appHostPath;
        }
        if (workspaceAppHostPath && (workspaceResources.length > 0 || this._dataRepository.workspaceAppHost !== undefined)) {
            if (matchesAppHostPathOrDirectory(docPath, workspaceAppHostPath)) {
                return workspaceAppHostPath;
            }
        }
        return undefined;
    }

    private _resolveGlobalAppHostForDocument(document: vscode.TextDocument, appHosts: readonly AppHostDisplayInfo[]): AppHostDisplayInfo | undefined {
        return appHosts.find(host => this._documentMatchesAppHostPath(document, host.appHostPath));
    }

    private _documentMatchesAppHostPath(document: vscode.TextDocument, appHostPath: string | undefined): boolean {
        if (!appHostPath) {
            return false;
        }

        return matchesAppHostPathOrDirectory(document.uri.fsPath, appHostPath);
    }

    private _addStateLenses(
        lenses: vscode.CodeLens[],
        range: vscode.Range,
        resource: ResourceJson,
        appHost: AppHostDisplayInfo,
    ): void {
        const state = resource.state ?? '';
        const stateStyle = resource.stateStyle ?? '';
        const healthStatus = resource.healthStatus;
        const commands = resource.commands ?? {};

        // State indicator lens (clickable — reveals resource in tree view)
        let stateLabel = getCodeLensStateLabel(state, stateStyle, resource.exitCode);
        if (healthStatus && healthStatus !== HealthStatus.Healthy) {
            const reports = resource.healthReports;
            if (reports) {
                const entries = Object.values(reports);
                const healthy = entries.filter(r => r.status === HealthStatus.Healthy).length;
                stateLabel += ` - (${healthStatus} ${healthy}/${entries.length})`;
            } else {
                stateLabel += ` - (${healthStatus})`;
            }
        }

        let tooltipText = `${resource.displayName ?? resource.name}: ${getResourceStateDescription(state)}${healthStatus ? ` (${healthStatus})` : ''}`;
        const reports = resource.healthReports;
        if (reports && healthStatus && healthStatus !== HealthStatus.Healthy) {
            const failing = Object.entries(reports).filter(([, r]) => r.status !== HealthStatus.Healthy);
            if (failing.length > 0) {
                tooltipText += '\n' + failing.map(([name, r]) => `  ${name}: ${r.status}${r.description ? ` - ${r.description}` : ''}`).join('\n');
            }
        }

        lenses.push(new vscode.CodeLens(range, {
            title: stateLabel,
            command: 'aspire-vscode.codeLensRevealResource',
            tooltip: tooltipText,
            arguments: [resource.displayName ?? resource.name, appHost.appHostPath],
        }));

        // Parameter value lens (secrets masked, long values truncated) so the value is
        // visible inline next to the state, matching the dashboard and tree view.
        const parameterValue = getParameterValueDescription(resource);
        if (parameterValue !== undefined) {
            lenses.push(new vscode.CodeLens(range, {
                title: parameterValue,
                command: 'aspire-vscode.codeLensRevealResource',
                tooltip: parameterValue,
                arguments: [resource.displayName ?? resource.name, appHost.appHostPath],
            }));
        }

        // Action lenses based on available commands
        const restartCommand = getEnabledCommand(commands, 'restart', 'resource-restart');
        if (restartCommand) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensRestart,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensRestart,
                arguments: [resource.name, 'restart', appHost.appHostPath, restartCommand],
            }));
        }

        const stopCommand = getEnabledCommand(commands, 'stop', 'resource-stop');
        if (stopCommand) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensStop,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensStop,
                arguments: [resource.name, 'stop', appHost.appHostPath, stopCommand],
            }));
        }

        const startCommand = getEnabledCommand(commands, 'start', 'resource-start');
        if (startCommand) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensStart,
                command: 'aspire-vscode.codeLensResourceAction',
                tooltip: codeLensStart,
                arguments: [resource.name, 'start', appHost.appHostPath, startCommand],
            }));
        }

        // View Logs lens (not applicable to parameters)
        if (resource.resourceType !== ResourceType.Parameter) {
            lenses.push(new vscode.CodeLens(range, {
                title: codeLensViewLogs,
                command: 'aspire-vscode.codeLensViewLogs',
                tooltip: codeLensViewLogs,
                arguments: [resource.displayName ?? resource.name, appHost.appHostPath],
            }));
        }

        // Custom commands (non-standard ones like "Reset Database")
        const standardCommands = new Set(['restart', 'resource-restart', 'stop', 'resource-stop', 'start', 'resource-start']);
        // Sort by (order, name) so custom command lenses appear in the dashboard registration order.
        const customCommands = (Object.entries(commands) as [string, ResourceCommandJson][])
            .sort(compareResourceCommands);
        for (const [cmdName, cmd] of customCommands) {
            if (!standardCommands.has(cmdName) && isEnabledCommand(cmd) && isCommandVisibleToUi(cmd)) {
                const displayName = getNormalizedCommandText(cmd.displayName);
                const description = getNormalizedCommandText(cmd.description);
                const label = codeLensCommand(displayName ?? cmdName);
                lenses.push(new vscode.CodeLens(range, {
                    title: label,
                    command: 'aspire-vscode.codeLensResourceAction',
                    tooltip: description ?? displayName ?? cmdName,
                    arguments: [resource.name, cmdName, appHost.appHostPath, cmd],
                }));
            }
        }
    }

    dispose(): void {
        this._disposables.forEach(d => d.dispose());
        this._onDidChangeCodeLenses.dispose();
    }
}

function getEnabledCommand(commands: Record<string, ResourceCommandJson>, ...commandNames: string[]): ResourceCommandJson | undefined {
    return commandNames
        .map(commandName => commands[commandName])
        .find(command => isEnabledCommand(command) && isCommandVisibleToUi(command));
}

export function getCodeLensStateLabel(state: string, stateStyle: string, exitCode?: number | null): string {
    switch (state) {
        case ResourceState.Running:
        case ResourceState.Active:
            if (stateStyle === StateStyle.Error) {
                return codeLensResourceRunningError;
            }
            if (stateStyle === StateStyle.Warning) {
                return codeLensResourceRunningWarning;
            }
            return codeLensResourceRunning;
        case ResourceState.Starting:
        case ResourceState.Building:
            return codeLensResourceStarting;
        case ResourceState.Waiting:
            return codeLensResourceWaiting;
        case ResourceState.NotStarted:
            return codeLensResourceNotStarted;
        case ResourceState.FailedToStart:
            return exitCode != null && exitCode !== 0 ? codeLensResourceFailedToStartError : codeLensResourceFailedToStart;
        case ResourceState.RuntimeUnhealthy:
            return codeLensResourceRuntimeUnhealthy;
        case ResourceState.Stopping:
            return codeLensResourceStopping;
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.Stopped:
            if (stateStyle === StateStyle.Error) {
                return exitCode != null && exitCode !== 0 ? codeLensResourceStoppedErrorWithExitCode(exitCode) : codeLensResourceStoppedError;
            }
            return exitCode != null && exitCode !== 0 ? codeLensResourceStoppedWithExitCode(exitCode) : codeLensResourceStopped;
        case ResourceState.ValueMissing:
            return codeLensResourceValueMissing;
        default:
            return state || codeLensResourceStopped;
    }
}

function getNormalizedCommandText(value: string | null | undefined): string | undefined {
    const normalized = value?.trim();
    return normalized ? normalized : undefined;
}
