import path from "path";
import { ExecutableLaunchConfiguration, EnvVar, LaunchOptions, AspireResourceExtendedDebugConfiguration, AspireExtendedDebugConfiguration, AspireResourceDebugSession, DebugLaunchSettings, ResourceTerminationSignal } from "../dcp/types";
import { debugProject, runProject } from "../loc/strings";
import { getEnvironmentWithoutE2EBridgeVariables, mergeEnvs } from "../utils/environment";
import { extensionLogOutputChannel } from "../utils/logging";
import { projectDebuggerExtension } from "./languages/dotnet";
import { isAzureFunctionsExtensionInstalled, isBunInstalled, isCsharpInstalled, isGoInstalled, isMauiInstalled, isPythonInstalled } from '../capabilities';
import { pythonDebuggerExtension } from "./languages/python";
import { nodeDebuggerExtension } from "./languages/node";
import { browserDebuggerExtension } from "./languages/browser";
import { azureFunctionsDebuggerExtension } from "./languages/azureFunctions";
import { goDebuggerExtension } from "./languages/go";
import { bunDebuggerExtension } from "./languages/bun";
import { mauiDebuggerExtension } from "./languages/maui";
import { isDirectory } from "../utils/io";
import { waitForRunStartIdle } from "./runStartRegistry";

// Represents a resource-specific debugger extension for when the default session configuration is not sufficient to launch the resource.
export interface ResourceDebuggerExtension {
    resourceType: string;
    debugAdapter: string;
    extensionId: string | null;
    /**
     * Which observable event ends a run of this resource type. Required so that adding a debugger
     * integration is a deliberate decision about run lifetime rather than an inherited default,
     * and so the choice lives in code the workspace cannot influence.
     */
    terminationSignal: ResourceTerminationSignal;
    getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => string;
    getProjectFile: (launchConfig: ExecutableLaunchConfiguration) => string;
    getSupportedFileTypes: () => string[];
    createDebugSessionConfigurationCallback?: (launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration) => Promise<AlreadyStartedResourceDebugSession | void>;
}

export interface AlreadyStartedResourceDebugSession extends AspireResourceDebugSession {
    processId: number;
    termination: Promise<number>;
}

export interface PreparedDebugSession {
    debugConfiguration: AspireResourceExtendedDebugConfiguration;
    alreadyStartedSession?: AlreadyStartedResourceDebugSession;
}

export async function createDebugSessionConfiguration(debugSessionConfig: AspireExtendedDebugConfiguration, launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debuggerExtension: ResourceDebuggerExtension): Promise<AspireResourceExtendedDebugConfiguration> {
    return (await prepareDebugSession(debugSessionConfig, launchConfig, args, env, launchOptions, debuggerExtension)).debugConfiguration;
}

export async function prepareDebugSession(debugSessionConfig: AspireExtendedDebugConfiguration, launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debuggerExtension: ResourceDebuggerExtension): Promise<PreparedDebugSession> {
    if (debuggerExtension === null) {
        extensionLogOutputChannel.warn(`Unknown type: ${launchConfig.type}.`);
    }

    const projectPath = debuggerExtension.getProjectFile(launchConfig);
    await waitForRunStartIdle();

    const aspireOwnedFieldSources: AspireOwnedFieldSources = { launchOptions, debuggerExtension };

    const configuration: AspireResourceExtendedDebugConfiguration = {
        type: debuggerExtension.debugAdapter || launchConfig.type,
        request: 'launch',
        name: launchOptions.debug ? debugProject(debuggerExtension.getDisplayName(launchConfig)) : runProject(debuggerExtension.getDisplayName(launchConfig)),
        program: projectPath,
        args: args,
        cwd: await isDirectory(projectPath) ? projectPath : path.dirname(projectPath),
        env: mergeEnvs(getEnvironmentWithoutE2EBridgeVariables(), env),
        justMyCode: false,
        stopAtEntry: false,
        noDebug: !launchOptions.debug,
        console: 'internalConsole',
        // Spread rather than listed out, so the map below is the only place that says what an
        // Aspire-owned field is worth. Writing them here as well would be a second copy that could
        // disagree with the re-application after the workspace merge.
        ...resolveAspireOwnedFields(aspireOwnedFieldSources)
    };

    if (debugSessionConfig.debuggers) {
        // 1. Check if this is the apphost
        if (launchOptions.isApphost && debugSessionConfig.debuggers['apphost']) {
            applyUserDebuggerSettings(configuration, debugSessionConfig.debuggers['apphost']);
        }

        // 2. Check for resource type specific debugger settings
        if (debugSessionConfig.debuggers[launchConfig.type]) {
            applyUserDebuggerSettings(configuration, debugSessionConfig.debuggers[launchConfig.type]);
        }
    }

    // Re-apply the fields Aspire owns *after* the workspace merge, so a workspace setting can
    // never win no matter what it contains. applyUserDebuggerSettings() also refuses to write
    // them, but a refusal is only as good as the list it consults; this write-last ordering is
    // what makes the guarantee hold even if that list is ever wrong. Both read from the same map,
    // so they cannot disagree about which fields are Aspire's.
    Object.assign(configuration, resolveAspireOwnedFields(aspireOwnedFieldSources));

    let alreadyStartedSession: AlreadyStartedResourceDebugSession | undefined;
    if (debuggerExtension.createDebugSessionConfigurationCallback) {
        alreadyStartedSession = await debuggerExtension.createDebugSessionConfigurationCallback(launchConfig, args, env, launchOptions, configuration) ?? undefined;
    }

    return {
        debugConfiguration: configuration,
        alreadyStartedSession
    };
}

/**
 * The fields `AspireResourceExtendedDebugConfiguration` actually declares.
 *
 * The interface extends `vscode.DebugConfiguration`, which carries a `[key: string]: any` index
 * signature so adapter-specific options can be passed through. That index signature widens
 * `keyof` to `string | number`, which would make every key check below vacuous. The `string
 * extends K` / `number extends K` remapping drops the index signature and keeps only the
 * explicitly declared properties, so `keyof` becomes the finite list this file reasons about.
 * See https://github.com/microsoft/TypeScript/issues/25987 for the underlying limitation.
 */
type DeclaredResourceDebugConfigurationFields = {
    [K in keyof AspireResourceExtendedDebugConfiguration as string extends K ? never : number extends K ? never : K]: AspireResourceExtendedDebugConfiguration[K]
};

/** Everything an Aspire-owned field is allowed to derive its value from. */
interface AspireOwnedFieldSources {
    launchOptions: LaunchOptions;
    debuggerExtension: ResourceDebuggerExtension;
}

/**
 * Fields on the resource debug configuration that Aspire owns, mapped to the authoritative value
 * for each. The workspace `debuggers` setting must never influence any of them.
 *
 * These are not user-facing knobs. They correlate the VS Code session with the DCP run
 * (`runId`, `debugSessionId`), decide which event ends the run and therefore who reports it
 * (`terminationSignal`), and select AppHost-specific behavior (`isApphost`).
 *
 * Letting workspace-controlled JSON reach any of them is a safety problem rather than just a
 * confusing override:
 *
 * - `runId` is used as the key for the per-run cleanup handlers that delete on-disk scratch
 *   directories (`createBrowserUserDataDir` in `languages/browser.ts`), so a workspace-supplied
 *   `runId` could aim that delete at a directory belonging to another run.
 * - `debugSessionId` is written as `dcp_id` onto DCP wire notifications, so a workspace-supplied
 *   value would let settings address another run's lifecycle messages.
 * - `terminationSignal` decides whether the adapter tracker or the debug session emits
 *   `sessionTerminated`, so a workspace-supplied value could suppress or duplicate the terminal
 *   notification for resource types whose callbacks never set it (`node`, `dotnet`, ...).
 *
 * This is deliberately one map rather than a list of names plus a separate block of assignments.
 * Those were two structures that had to agree: a field could be re-applied but not refused (the
 * workspace value would land and then be overwritten, silently, with no warning explaining why),
 * or refused but not re-applied (the placeholder set before the merge would stand). Deriving both
 * behaviors from the same entry makes that class of drift unrepresentable. `satisfies` is what
 * makes the keys and value types checked while keeping the literal keys for
 * {@link AspireOwnedResourceDebugConfigurationField}: a misspelled field, or a resolver returning
 * the wrong type for the field it names, is a compile error rather than a silent no-op.
 */
const aspireOwnedResourceDebugConfigurationFields = {
    runId: ({ launchOptions }) => launchOptions.runId,
    debugSessionId: ({ launchOptions }) => launchOptions.debugSessionId,
    isApphost: ({ launchOptions }) => launchOptions.isApphost,
    // Declared by the debugger integration at authoring time, never read back off the configuration
    // object, so workspace settings have no path to it.
    terminationSignal: ({ debuggerExtension }) => debuggerExtension.terminationSignal
} satisfies {
    [K in keyof DeclaredResourceDebugConfigurationFields]?: (sources: AspireOwnedFieldSources) => DeclaredResourceDebugConfigurationFields[K]
};

type AspireOwnedResourceDebugConfigurationField = keyof typeof aspireOwnedResourceDebugConfigurationFields;

/**
 * Declared fields the workspace `debuggers` setting is allowed to set.
 *
 * `type`, `name`, and `request` are ordinary VS Code launch configuration properties, and
 * `projectFile` is informational. Overriding them affects only the workspace doing the overriding.
 */
type WorkspaceWritableResourceDebugConfigurationField = 'type' | 'name' | 'request' | 'projectFile';

/**
 * Forces every declared field to be classified as either Aspire-owned or workspace-writable.
 *
 * Adding a field to `AspireResourceExtendedDebugConfiguration` without deciding which side it
 * belongs on fails to compile here, with the unclassified field named in the error. That decision
 * cannot be inferred — whether a new field is a user knob or a correctness-critical one is a
 * judgment — so the type system's job is to refuse to let it be skipped. Without this, a new field
 * silently defaults to workspace-writable, which is the unsafe direction and exactly the failure
 * mode a denylist has.
 *
 * The alias is intentionally never referenced: instantiating it is the whole check.
 */
type UnclassifiedResourceDebugConfigurationField = Exclude<
    keyof DeclaredResourceDebugConfigurationFields,
    AspireOwnedResourceDebugConfigurationField | WorkspaceWritableResourceDebugConfigurationField>;

type AssertEveryDeclaredFieldIsClassified<T extends never = UnclassifiedResourceDebugConfigurationField> = T;

/**
 * Field names the workspace merge must refuse. Derived from the map so it cannot fall out of sync
 * with what {@link resolveAspireOwnedFields} produces.
 *
 * Exported so tests can assert the refusal over whatever the map currently contains, rather than
 * over a list copied into the test that would go stale the moment a field is added.
 */
export const aspireOwnedResourceDebugConfigurationFieldNames: ReadonlySet<string> =
    new Set<string>(Object.keys(aspireOwnedResourceDebugConfigurationFields));

/** The Aspire-owned fields with their authoritative values, ready to be written onto a configuration. */
type AspireOwnedFieldValues = Pick<DeclaredResourceDebugConfigurationFields, AspireOwnedResourceDebugConfigurationField>;

/**
 * Computes the authoritative value of every Aspire-owned field.
 *
 * Used both when the configuration is first built and again after the workspace merge, so the two
 * cannot disagree. The cast is confined to this function: iterating the map erases the per-key
 * relationship between name and value type that `Object.entries` cannot express, and the map's
 * `satisfies` clause is what establishes that relationship in the first place.
 */
function resolveAspireOwnedFields(sources: AspireOwnedFieldSources): AspireOwnedFieldValues {
    const resolved: Record<string, unknown> = {};

    for (const [field, resolveValue] of Object.entries(aspireOwnedResourceDebugConfigurationFields)) {
        resolved[field] = resolveValue(sources);
    }

    return resolved as AspireOwnedFieldValues;
}

/**
 * Merges a workspace `debuggers.<key>` block into the generated debug configuration, refusing the
 * fields Aspire owns.
 *
 * `DebugLaunchSettings` declares only user-facing properties, but the value comes from unvalidated
 * `launch.json` JSON and the contributed schema for `debuggers` is an open object, so arbitrary
 * keys reach this code at runtime. Unknown keys are still forwarded on purpose: passing extra
 * options through to the underlying debug adapter is the feature. Only the Aspire-owned fields are
 * refused, and refusing them is logged rather than silent so a workspace author can see why their
 * setting had no effect.
 */
function applyUserDebuggerSettings(configuration: AspireResourceExtendedDebugConfiguration, settings: DebugLaunchSettings): void {
    for (const [key, value] of Object.entries(settings)) {
        if (aspireOwnedResourceDebugConfigurationFieldNames.has(key)) {
            extensionLogOutputChannel.warn(`Ignoring '${key}' from the 'debuggers' debug configuration because it is managed by Aspire.`);
            continue;
        }

        (configuration as Record<string, unknown>)[key] = value;
    }
}

export function getResourceDebuggerExtensions(): ResourceDebuggerExtension[] {
    const extensions = [];
    if (isCsharpInstalled()) {
        extensions.push(projectDebuggerExtension);

        if (isAzureFunctionsExtensionInstalled()) {
            extensions.push(azureFunctionsDebuggerExtension);
        }
    }

    if (isPythonInstalled()) {
        extensions.push(pythonDebuggerExtension);
    }

    if (isGoInstalled()) {
        extensions.push(goDebuggerExtension);
    }

    extensions.push(nodeDebuggerExtension);
    extensions.push(browserDebuggerExtension);

    if (isBunInstalled()) {
        extensions.push(bunDebuggerExtension);
    }

    if (isMauiInstalled()) {
        extensions.push(mauiDebuggerExtension);
    }

    return extensions;
}
