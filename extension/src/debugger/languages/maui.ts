import * as path from 'path';
import * as vscode from 'vscode';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { AspireResourceExtendedDebugConfiguration, EnvVar, ExecutableLaunchConfiguration, MauiLaunchConfiguration, isMauiLaunchConfiguration } from "../../dcp/types";
import { invalidLaunchConfiguration } from "../../loc/strings";
import { addFilteredEnvironmentKeys, removeFilteredEnvironmentKeys } from "../../utils/environment";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { registerRunStartWrapper } from "../runStartRegistry";

const execFileAsync = promisify(execFile);
const mauiLaunchJsonConfigurationsSetting = 'maui.configuration.useLaunchJsonConfigurations';
const mauiCommandRegistrationTimeoutMs = 120_000;
const mauiStartDebugSessionCommand = 'vscode-maui.mauiStartDebugSession';
const mauiBuildTask = 'maui: Build';
const aspireDebuggerInfrastructureEnvironmentVariables = new Set([
    'ASPIRE_BACKCHANNEL_PATH',
    'ASPIRE_CLI_LOG_FILE',
    'ASPIRE_CLI_PID',
    'ASPIRE_CLI_STARTED',
    'ASPIRE_EXTENSION_CAPABILITIES',
    'ASPIRE_EXTENSION_CERT',
    'ASPIRE_EXTENSION_DEBUG_RUN_MODE',
    'ASPIRE_EXTENSION_DEBUG_SESSION_ID',
    'ASPIRE_EXTENSION_ENDPOINT',
    'ASPIRE_EXTENSION_PROMPT_ENABLED',
    'ASPIRE_EXTENSION_TOKEN',
    'ASPIRE_LOCALE_OVERRIDE',
    'ASPIRE_NON_INTERACTIVE',
    'ASPIRE_SUPPRESS_CLI_RUN_HOOK',
    'ASPIRE_TERMINAL_HOST_INVOCATION_ARGS',
    'ASPIRE_TERMINAL_HOST_PATH',
    'DCP_INSTANCE_ID_PREFIX',
    'DEBUG_SESSION_INFO',
    'DEBUG_SESSION_PORT',
    'DEBUG_SESSION_RUN_MODE',
    'DEBUG_SESSION_SERVER_CERTIFICATE',
    'DEBUG_SESSION_TOKEN',
]);
const aspireDebuggerInfrastructureEnvironmentPrefixes = [
    'ASPIRE_CLI_',
    'ASPIRE_EXTENSION_E2E_',
    'ASPIRE_TERMINAL_HOST_',
] as const;
const mauiDebugAdapterInfrastructureEnvironmentVariables = new Set([
    'VSCODE_NLS_CONFIG',
]);
type MauiTargetKind = 'device' | 'emulator' | 'simulator';

interface MauiDeviceInfo {
    identifier: string;
    emulatorId?: string;
    platform?: string;
    platforms: string[];
    isEmulator: boolean;
    isRunning: boolean;
    name?: string;
}

interface ResolvedMauiDeviceTarget {
    device: string;
    useDebugConfigurationDevice: boolean;
}

type MauiDeviceListProvider = () => Promise<MauiDeviceInfo[]>;
let mauiDeviceListProvider: MauiDeviceListProvider = listMauiDevices;

interface MauiExtensionApi {
    maui?: {
        debugTargetsManager?: {
            setActiveDebugTarget?: (target: string, platform: string) => unknown | Thenable<unknown>;
        };
    };
}

export function useMauiDeviceListProviderForTests(provider: MauiDeviceListProvider | undefined): void {
    mauiDeviceListProvider = provider ?? listMauiDevices;
}

export async function executeMauiCommandWithTimeout(command: string, timeoutMs: number, ...args: unknown[]): Promise<{ timedOut: true } | { timedOut: false; value?: unknown; error?: string }> {
    const result = await Promise.race([
        Promise.resolve(vscode.commands.executeCommand(command, ...args))
            .then(value => ({ timedOut: false as const, value }))
            .catch((error: unknown) => ({ timedOut: false as const, error: error instanceof Error ? error.message : String(error) })),
        delay(timeoutMs).then(() => ({ timedOut: true as const }))
    ]);

    if (result.timedOut) {
        await Promise.resolve(vscode.commands.executeCommand('workbench.action.closeQuickOpen')).catch(() => undefined);
    }

    return result;
}

async function waitForMauiCommand(command: string, timeoutMs: number): Promise<boolean> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        const commands = await vscode.commands.getCommands(true);
        if (commands.includes(command)) {
            return true;
        }

        await delay(500);
    }

    return false;
}

function delay(timeoutMs: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, timeoutMs));
}

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    if (isMauiLaunchConfiguration(launchConfig)) {
        return launchConfig.project_path;
    }

    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

function getDisplayName(launchConfig: ExecutableLaunchConfiguration): string {
    const projectName = `MAUI: ${vscode.workspace.asRelativePath(getProjectFile(launchConfig))}`;
    if (!isMauiLaunchConfiguration(launchConfig)) {
        return projectName;
    }

    const targetParts = [
        launchConfig.platform,
        launchConfig.target_kind,
        launchConfig.device
    ].filter(part => part?.trim());

    return targetParts.length > 0 ? `${projectName} (${targetParts.join(' ')})` : projectName;
}

function isAspireDebuggerInfrastructureEnvironmentVariable(name: string): boolean {
    return aspireDebuggerInfrastructureEnvironmentVariables.has(name) ||
        aspireDebuggerInfrastructureEnvironmentPrefixes.some(prefix => name.startsWith(prefix));
}

function isMauiDebugAdapterInfrastructureEnvironmentVariable(name: string): boolean {
    return isAspireDebuggerInfrastructureEnvironmentVariable(name) ||
        mauiDebugAdapterInfrastructureEnvironmentVariables.has(name);
}

function isKnownMauiApplicationEnvironmentVariable(name: string): boolean {
    return name === 'ASPNETCORE_ENVIRONMENT' ||
        name === 'DOTNET_ENVIRONMENT' ||
        name === 'ASPIRE_DASHBOARD_AI_DISABLED' ||
        name === 'ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL' ||
        name === 'ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL' ||
        name === 'APPLICATIONINSIGHTS_CONFIGURATION_CONTENT' ||
        name === 'SSL_CERT_DIR' ||
        name === 'SSL_CERT_FILE' ||
        name.startsWith('APPLICATION_INSIGHTS_') ||
        name.startsWith('OTEL_');
}

function isInheritedHostEnvironmentVariable(variable: EnvVar): boolean {
    // The MAUI extension turns environmentVariables into mlaunch --setenv arguments.
    // Inherited VS Code/C# Dev Kit process variables often contain spaces, for example:
    //   CommonPropertyBagPath=/Users/me/Library/Application Support/csdevkit/db.json
    //   _=/Applications/Visual Studio Code - Insiders.app/Contents/MacOS/Code - Insiders
    // mlaunch receives those as split arguments and aborts with MT0018 before installing the app.
    return !isKnownMauiApplicationEnvironmentVariable(variable.name) &&
        process.env[variable.name] === variable.value;
}

function getMauiPlatform(launchConfig: ExecutableLaunchConfiguration): string | undefined {
    if (!isMauiLaunchConfiguration(launchConfig)) {
        return undefined;
    }

    if (launchConfig.platform) {
        return launchConfig.platform;
    }

    const targetFramework = launchConfig.target_framework?.toLowerCase();
    if (targetFramework?.includes('-maccatalyst')) {
        return 'maccatalyst';
    }
    if (targetFramework?.includes('-android')) {
        return 'android';
    }
    if (targetFramework?.includes('-ios')) {
        return 'ios';
    }
    if (targetFramework?.includes('-windows')) {
        return 'windows';
    }

    return undefined;
}

function getMauiTargetKind(launchConfig: MauiLaunchConfiguration, msbuildProperties: Map<string, string>, runtimeIdentifier: string | undefined): MauiTargetKind | undefined {
    const configuredTargetKind = launchConfig.target_kind?.toLowerCase();
    switch (configuredTargetKind) {
        case 'device':
        case 'emulator':
        case 'simulator':
            return configuredTargetKind;
    }

    const adbTarget = msbuildProperties.get('AdbTarget');
    if (adbTarget?.startsWith('-d')) {
        return 'device';
    }
    if (adbTarget?.startsWith('-e')) {
        return 'emulator';
    }

    const deviceName = msbuildProperties.get('_DeviceName');
    if (deviceName?.startsWith(':v2:udid=')) {
        return 'simulator';
    }
    if (deviceName) {
        return 'device';
    }
    if (runtimeIdentifier === 'ios-arm64') {
        return 'device';
    }
    if (launchConfig.platform?.toLowerCase() === 'ios') {
        return 'simulator';
    }

    return undefined;
}

function getExplicitDeviceFromMsBuildProperties(msbuildProperties: Map<string, string>): string | undefined {
    const adbTarget = msbuildProperties.get('AdbTarget');
    const adbSerialPrefix = '-s ';
    if (adbTarget?.startsWith(adbSerialPrefix)) {
        return adbTarget.slice(adbSerialPrefix.length).trim() || undefined;
    }

    const deviceName = msbuildProperties.get('_DeviceName');
    const simulatorPrefix = ':v2:udid=';
    if (deviceName?.startsWith(simulatorPrefix)) {
        return deviceName.slice(simulatorPrefix.length).trim() || undefined;
    }

    return deviceName?.trim() || undefined;
}

function applyAndroidDebugTargetProperties(debugConfiguration: AspireResourceExtendedDebugConfiguration, platform: string | undefined, msbuildProperties: Map<string, string>, device: string): void {
    if (platform !== 'android' || !msbuildProperties.has('AdbTarget')) {
        return;
    }

    msbuildProperties.set('AdbTarget', `-s ${device}`);
    setMsBuildProperties(debugConfiguration, msbuildProperties);
}

function removeAndroidDebugTargetProperties(debugConfiguration: AspireResourceExtendedDebugConfiguration, platform: string | undefined, msbuildProperties: Map<string, string>): void {
    if (platform !== 'android' || !msbuildProperties.has('AdbTarget')) {
        return;
    }

    msbuildProperties.delete('AdbTarget');
    setMsBuildProperties(debugConfiguration, msbuildProperties);
}

function getDefaultIosSimulatorRuntimeIdentifier(): string {
    return process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64';
}

function applyIosDebugTargetProperties(debugConfiguration: AspireResourceExtendedDebugConfiguration, targetKind: MauiTargetKind | undefined, device: string | undefined): void {
    if (!device || (targetKind !== 'simulator' && targetKind !== 'device')) {
        return;
    }

    const msbuildProperties = getMsBuildProperties(debugConfiguration);
    if (targetKind === 'simulator') {
        debugConfiguration.isEmulator = true;
        debugConfiguration.runtimeIdentifier ??= getDefaultIosSimulatorRuntimeIdentifier();
        msbuildProperties.set('_DeviceName', `:v2:udid=${device}`);
    } else {
        debugConfiguration.isEmulator = false;
        debugConfiguration.runtimeIdentifier ??= 'ios-arm64';
        msbuildProperties.set('_DeviceName', device);
    }

    debugConfiguration.debugTarget ??= device;
    msbuildProperties.set('RuntimeIdentifier', debugConfiguration.runtimeIdentifier);
    setMsBuildProperties(debugConfiguration, msbuildProperties);
}

async function resolveExplicitDevice(platform: string | undefined, targetKind: MauiTargetKind | undefined, configuredDevice: string | undefined): Promise<ResolvedMauiDeviceTarget | undefined> {
    if (!configuredDevice) {
        return undefined;
    }

    if (platform === 'android' && targetKind === 'emulator') {
        const normalizedConfiguredDevice = configuredDevice.toLowerCase();
        const devices = await mauiDeviceListProvider();
        const matchingDevice = devices
            .filter(device => hasPlatform(device, 'android') && device.isEmulator)
            .find(device =>
                device.identifier.toLowerCase() === normalizedConfiguredDevice ||
                device.emulatorId?.toLowerCase() === normalizedConfiguredDevice ||
                device.name?.toLowerCase() === normalizedConfiguredDevice);

        if (matchingDevice) {
            return createResolvedDeviceTarget(platform, targetKind, matchingDevice);
        }
    }

    return createResolvedDeviceTargetFromIdentifier(configuredDevice);
}

function getDefaultDesktopDevice(platform: string | undefined, targetKind: MauiTargetKind | undefined): string | undefined {
    if (targetKind !== 'device') {
        return undefined;
    }

    switch (platform) {
        case 'maccatalyst':
            return 'my-mac';
        case 'windows':
            return 'windowsmachine';
        default:
            return undefined;
    }
}

function setConfiguration(debugConfiguration: AspireResourceExtendedDebugConfiguration, configuration: string | undefined): void {
    const normalizedConfiguration = configuration?.trim();
    if (normalizedConfiguration) {
        debugConfiguration.configuration = normalizedConfiguration;
    }
}

function getMsBuildProperties(debugConfiguration: AspireResourceExtendedDebugConfiguration): Map<string, string> {
    const existingProperties = debugConfiguration.msbuildProperties;
    if (existingProperties instanceof Map) {
        return new Map(existingProperties);
    }

    if (existingProperties && typeof existingProperties === 'object') {
        return new Map(Object.entries(existingProperties).map(([key, value]) => [key, String(value)]));
    }

    return new Map<string, string>();
}

function setMsBuildProperties(debugConfiguration: AspireResourceExtendedDebugConfiguration, properties: Map<string, string>): void {
    if (properties.size > 0) {
        // The MAUI extension's dynamic build task reads this as a Map and calls
        // `.forEach` when appending `-p:` arguments. Keep the live debug config
        // as a Map; logging converts it to a plain object separately.
        debugConfiguration.msbuildProperties = properties;
    } else {
        delete debugConfiguration.msbuildProperties;
    }
}

function applyStructuredLaunchConfiguration(launchConfig: MauiLaunchConfiguration, debugConfiguration: AspireResourceExtendedDebugConfiguration): void {
    if (launchConfig.runtime_identifier) {
        debugConfiguration.runtimeIdentifier = launchConfig.runtime_identifier;
    }

    if (launchConfig.msbuild_properties) {
        const msbuildProperties = getMsBuildProperties(debugConfiguration);
        for (const [name, value] of Object.entries(launchConfig.msbuild_properties)) {
            msbuildProperties.set(name, value);
        }
        setMsBuildProperties(debugConfiguration, msbuildProperties);
    }
}

function hasPlatform(device: MauiDeviceInfo, platform: string): boolean {
    return device.platform === platform || device.platforms.includes(platform);
}

function getDeviceIdentity(device: MauiDeviceInfo): string {
    return device.name ? `${device.name} (${device.identifier})` : device.identifier;
}

async function listMauiDevices(): Promise<MauiDeviceInfo[]> {
    const mauiExtension = vscode.extensions.getExtension('ms-dotnettools.dotnet-maui');
    if (!mauiExtension) {
        throw new Error('The .NET MAUI VS Code extension is required to resolve MAUI device targets.');
    }

    const cliPath = path.join(mauiExtension.extensionPath, 'tools', 'maui-cli', 'maui.dll');
    const { stdout } = await execFileAsync('dotnet', [cliPath, 'device', 'list', '--json', '--ci'], { timeout: 30000 });
    const jsonStart = stdout.indexOf('[');
    const jsonEnd = stdout.lastIndexOf(']');
    if (jsonStart < 0 || jsonEnd < jsonStart) {
        throw new Error('The .NET MAUI device list command did not return JSON.');
    }

    const parsedDevices = JSON.parse(stdout.slice(jsonStart, jsonEnd + 1)) as unknown;
    if (!Array.isArray(parsedDevices)) {
        throw new Error('The .NET MAUI device list command returned an unexpected payload.');
    }

    return parsedDevices.map(toMauiDeviceInfo).filter((device): device is MauiDeviceInfo => device !== undefined);
}

function toMauiDeviceInfo(value: unknown): MauiDeviceInfo | undefined {
    if (!value || typeof value !== 'object') {
        return undefined;
    }

    const device = value as Record<string, unknown>;
    const identifier = getStringProperty(device, 'identifier') ?? getStringProperty(device, 'id');
    if (!identifier) {
        return undefined;
    }

    return {
        identifier,
        emulatorId: getStringProperty(device, 'emulator_id') ?? getStringProperty(device, 'emulatorId'),
        platform: getStringProperty(device, 'platform'),
        platforms: getStringArrayProperty(device, 'platforms'),
        isEmulator: getBooleanProperty(device, 'is_emulator') ?? getBooleanProperty(device, 'isEmulator') ?? false,
        isRunning: getBooleanProperty(device, 'is_running') ?? getBooleanProperty(device, 'isRunning') ?? false,
        name: getStringProperty(device, 'name')
    };
}

function getStringProperty(value: Record<string, unknown>, property: string): string | undefined {
    const propertyValue = value[property];
    return typeof propertyValue === 'string' && propertyValue.trim() ? propertyValue : undefined;
}

function getBooleanProperty(value: Record<string, unknown>, property: string): boolean | undefined {
    const propertyValue = value[property];
    return typeof propertyValue === 'boolean' ? propertyValue : undefined;
}

function getStringArrayProperty(value: Record<string, unknown>, property: string): string[] {
    const propertyValue = value[property];
    if (!Array.isArray(propertyValue)) {
        return [];
    }

    return propertyValue.filter((item): item is string => typeof item === 'string');
}

function createResolvedDeviceTarget(platform: string | undefined, targetKind: MauiTargetKind | undefined, device: MauiDeviceInfo): ResolvedMauiDeviceTarget {
    const isStoppedAndroidEmulator = platform === 'android' && targetKind === 'emulator' && device.isEmulator && !device.isRunning;
    return {
        device: device.identifier,
        useDebugConfigurationDevice: !isStoppedAndroidEmulator,
    };
}

function createResolvedDeviceTargetFromIdentifier(device: string): ResolvedMauiDeviceTarget {
    return {
        device,
        useDebugConfigurationDevice: true,
    };
}

async function resolveDefaultDevice(platform: string | undefined, targetKind: MauiTargetKind | undefined): Promise<ResolvedMauiDeviceTarget | undefined> {
    if (!platform || !targetKind || platform === 'maccatalyst' || platform === 'windows') {
        return undefined;
    }

    const devices = await mauiDeviceListProvider();
    let candidates = devices.filter(device => hasPlatform(device, platform));
    if (targetKind === 'device') {
        candidates = candidates.filter(device => !device.isEmulator);
    } else {
        candidates = candidates.filter(device => device.isEmulator);
        const runningCandidates = candidates.filter(device => device.isRunning);
        if (runningCandidates.length > 0) {
            candidates = runningCandidates;
        }
    }

    const targetDescription = `${platform} ${targetKind}`;
    if (candidates.length === 0) {
        throw new Error(`Unable to resolve a default ${targetDescription} target for MAUI debugging. Start a matching target or pass an explicit device/simulator id to the Aspire MAUI platform resource.`);
    }

    if (candidates.length === 1) {
        return createResolvedDeviceTarget(platform, targetKind, candidates[0]);
    }

    if (platform === 'ios' && targetKind === 'simulator') {
        return createResolvedDeviceTarget(platform, targetKind, candidates[0]);
    }

    throw new Error(`Unable to resolve a default ${targetDescription} target for MAUI debugging because multiple targets are available: ${candidates.map(getDeviceIdentity).join(', ')}. Pass an explicit device/simulator id to the Aspire MAUI platform resource.`);
}

function hasStructuredBuildMetadata(launchConfig: MauiLaunchConfiguration): boolean {
    return !!launchConfig.runtime_identifier ||
        (launchConfig.msbuild_properties !== undefined && Object.keys(launchConfig.msbuild_properties).length > 0);
}

function getMsBuildPropertyKey(properties: Map<string, string>, propertyName: string): string | undefined {
    const normalizedPropertyName = propertyName.toLowerCase();
    for (const key of properties.keys()) {
        if (key.toLowerCase() === normalizedPropertyName) {
            return key;
        }
    }

    return undefined;
}

function applyDotnetRunArgumentsToMauiConfiguration(args: string[] | undefined, debugConfiguration: AspireResourceExtendedDebugConfiguration, overwriteBuildProperties: boolean): void {
    if (!args?.length) {
        return;
    }

    const msbuildProperties = getMsBuildProperties(debugConfiguration);
    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg === '--') {
            break;
        }

        const propertyPrefix = arg.startsWith('-p:') ? '-p:' : arg.startsWith('/p:') ? '/p:' : undefined;
        if (propertyPrefix) {
            const property = arg.slice(propertyPrefix.length);
            const separator = property.indexOf('=');
            if (separator > 0) {
                const propertyName = property.slice(0, separator);
                const propertyValue = property.slice(separator + 1);
                const existingPropertyKey = getMsBuildPropertyKey(msbuildProperties, propertyName);
                const shouldApplyBuildProperty = overwriteBuildProperties || !existingPropertyKey;
                if (shouldApplyBuildProperty) {
                    msbuildProperties.set(propertyName, propertyValue);
                }
                switch (propertyName.toLowerCase()) {
                    case 'configuration':
                        setConfiguration(debugConfiguration, propertyValue);
                        break;
                    case 'runtimeidentifier':
                        if (shouldApplyBuildProperty && (overwriteBuildProperties || !debugConfiguration.runtimeIdentifier)) {
                            debugConfiguration.runtimeIdentifier = propertyValue;
                        }
                        break;
                }
            }
            continue;
        }

        if ((arg === '-c' || arg === '--configuration') && args[i + 1]) {
            setConfiguration(debugConfiguration, args[++i]);
            continue;
        }

        const configurationPrefix = arg.startsWith('-c:') ? '-c:' : arg.startsWith('-c=') ? '-c=' : arg.startsWith('--configuration=') ? '--configuration=' : undefined;
        if (configurationPrefix) {
            setConfiguration(debugConfiguration, arg.slice(configurationPrefix.length));
            continue;
        }

        if ((arg === '-r' || arg === '--runtime') && args[i + 1]) {
            const runtimeIdentifier = args[++i];
            if (overwriteBuildProperties || !debugConfiguration.runtimeIdentifier) {
                debugConfiguration.runtimeIdentifier = runtimeIdentifier;
            }
            continue;
        }

        const runtimePrefix = arg.startsWith('-r:') ? '-r:' : arg.startsWith('--runtime=') ? '--runtime=' : undefined;
        if (runtimePrefix) {
            const runtimeIdentifier = arg.slice(runtimePrefix.length);
            if (overwriteBuildProperties || !debugConfiguration.runtimeIdentifier) {
                debugConfiguration.runtimeIdentifier = runtimeIdentifier;
            }
        }
    }

    setMsBuildProperties(debugConfiguration, msbuildProperties);
}

function applyMacCatalystEnvironmentOverlay(env: EnvVar[], debugConfiguration: AspireResourceExtendedDebugConfiguration): void {
    const overlayEntries: EnvVar[] = [];
    for (const variable of env) {
        if (!variable.name) {
            continue;
        }

        overlayEntries.push(variable);
    }

    if (overlayEntries.length > 0) {
        registerRunStartWrapper(debugConfiguration.runId, async operation => await runWithMacCatalystEnvironmentOverlay(overlayEntries, operation));
    }
}

function registerMauiInfrastructureEnvironmentScrubber(runId: string): void {
    registerRunStartWrapper(runId, async operation => await runWithMauiInfrastructureEnvironmentScrubber(operation));
}

function registerMauiLaunchJsonConfigurationWrapper(runId: string, projectPath: string): void {
    registerRunStartWrapper(runId, async operation => {
        const configuration = vscode.workspace.getConfiguration(undefined, vscode.Uri.file(projectPath));
        const previousValue = configuration.get<boolean | undefined>(mauiLaunchJsonConfigurationsSetting);
        const inspected = configuration.inspect<boolean>(mauiLaunchJsonConfigurationsSetting);
        const scope = inspected?.workspaceFolderValue !== undefined
            ? { target: vscode.ConfigurationTarget.WorkspaceFolder, previousValue: inspected.workspaceFolderValue }
            : inspected?.workspaceValue !== undefined
                ? { target: vscode.ConfigurationTarget.Workspace, previousValue: inspected.workspaceValue }
                : { target: vscode.ConfigurationTarget.WorkspaceFolder, previousValue: undefined };

        if (previousValue !== true) {
            await configuration.update(mauiLaunchJsonConfigurationsSetting, true, scope.target);
        }

        try {
            await vscode.extensions.getExtension('ms-dotnettools.dotnet-maui')?.activate();
            const debugCommandAvailable = await waitForMauiCommand(mauiStartDebugSessionCommand, mauiCommandRegistrationTimeoutMs);
            if (!debugCommandAvailable) {
                throw new Error(`The .NET MAUI extension did not register '${mauiStartDebugSessionCommand}' within ${mauiCommandRegistrationTimeoutMs}ms.`);
            }

            return await operation();
        } finally {
            if (previousValue !== true) {
                await configuration.update(mauiLaunchJsonConfigurationsSetting, scope.previousValue, scope.target);
            }
        }
    });
}

function registerMauiActiveDebugTargetWrapper(runId: string, platform: string | undefined, device: string | undefined): void {
    if (!platform || !device) {
        return;
    }

    registerRunStartWrapper(runId, async operation => {
        await setMauiActiveDebugTarget(platform, device);

        return await operation();
    });
}

async function setMauiActiveDebugTarget(platform: string, device: string): Promise<void> {
    const mauiExtension = vscode.extensions.getExtension<MauiExtensionApi>('ms-dotnettools.dotnet-maui');
    const exports = await mauiExtension?.activate() ?? mauiExtension?.exports;
    const debugTargetsManager = exports?.maui?.debugTargetsManager;
    const setActiveDebugTarget = debugTargetsManager?.setActiveDebugTarget;

    if (!setActiveDebugTarget) {
        extensionLogOutputChannel.warn('The .NET MAUI extension did not expose debugTargetsManager.setActiveDebugTarget; continuing with the debug configuration target only.');
        return;
    }

    const selectedTarget = await setActiveDebugTarget.call(debugTargetsManager, device, platform);
    if (!selectedTarget) {
        throw new Error(`Unable to select MAUI ${platform} debug target '${device}'.`);
    }
}

async function runWithMauiInfrastructureEnvironmentScrubber<T>(operation: () => Promise<T>): Promise<T> {
    const keys = Object.keys(process.env).filter(isAspireDebuggerInfrastructureEnvironmentVariable);
    const originalValues = new Map<string, string | undefined>();

    try {
        addFilteredEnvironmentKeys(keys);
        for (const key of keys) {
            originalValues.set(key, process.env[key]);
            delete process.env[key];
        }

        return await operation();
    } finally {
        for (const [key, originalValue] of originalValues) {
            if (originalValue === undefined) {
                delete process.env[key];
            } else {
                process.env[key] = originalValue;
            }
        }
        removeFilteredEnvironmentKeys(keys);
    }
}

async function runWithMacCatalystEnvironmentOverlay<T>(overlayEntries: EnvVar[], operation: () => Promise<T>): Promise<T> {
    const originalValues = new Map<string, string | undefined>();
    const filteredKeys = [...new Set(overlayEntries.map(entry => entry.name))];
    try {
        addFilteredEnvironmentKeys(filteredKeys);
        for (const { name, value } of overlayEntries) {
            if (!originalValues.has(name)) {
                originalValues.set(name, process.env[name]);
            }

            process.env[name] = value;
        }

        return await operation();
    } finally {
        for (const [name, originalValue] of originalValues) {
            if (originalValue === undefined) {
                delete process.env[name];
            } else {
                process.env[name] = originalValue;
            }
        }
        removeFilteredEnvironmentKeys(filteredKeys);
    }
}

function requiresMacCatalystEnvironmentOverlay(variable: EnvVar): boolean {
    return variable.value.includes('=') || variable.value.includes('\n') || variable.value.includes('\r');
}

function hasEnvironmentLineBreak(variable: EnvVar): boolean {
    return variable.value.includes('\n') || variable.value.includes('\r');
}

function applyEnvironmentToMauiConfiguration(env: EnvVar[], debugConfiguration: AspireResourceExtendedDebugConfiguration, platform: string | undefined): void {
    const applicationEnvironment = env.filter(variable =>
        !isMauiDebugAdapterInfrastructureEnvironmentVariable(variable.name) &&
        !isInheritedHostEnvironmentVariable(variable));
    if (!applicationEnvironment.length) {
        return;
    }

    if (platform !== 'maccatalyst') {
        const invalidEnvironmentVariable = applicationEnvironment.find(variable => variable.name && hasEnvironmentLineBreak(variable));
        if (invalidEnvironmentVariable) {
            throw new Error(`MAUI debug environment variable '${invalidEnvironmentVariable.name}' contains a newline, which cannot be represented by the MAUI debug adapter environmentVariables format.`);
        }
    }

    const environmentVariablesToSerialize = platform === 'maccatalyst'
        ? applicationEnvironment.filter(variable => !requiresMacCatalystEnvironmentOverlay(variable))
        : applicationEnvironment;

    const environmentVariablesToOverlay = platform === 'maccatalyst'
        ? applicationEnvironment.filter(requiresMacCatalystEnvironmentOverlay)
        : [];

    applyMacCatalystEnvironmentOverlay(environmentVariablesToOverlay, debugConfiguration);

    const environmentVariables = environmentVariablesToSerialize
        .filter(variable => variable.name)
        .map(variable => `${variable.name}=${variable.value}`);
    if (!environmentVariables.length) {
        return;
    }

    if (typeof debugConfiguration.environmentVariables === 'string' && debugConfiguration.environmentVariables.trim()) {
        debugConfiguration.environmentVariables = `${debugConfiguration.environmentVariables}\n${environmentVariables.join('\n')}`;
    } else {
        debugConfiguration.environmentVariables = environmentVariables.join('\n');
    }
}

export const mauiDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'maui',
    debugAdapter: 'maui',
    extensionId: 'ms-dotnettools.dotnet-maui',
    getDisplayName,
    getSupportedFileTypes: () => ['.csproj'],
    getProjectFile: (launchConfig) => getProjectFile(launchConfig),
    createDebugSessionConfigurationCallback: async (launchConfig, args, env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isMauiLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not maui for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        debugConfiguration.type = 'maui';
        debugConfiguration.request = 'launch';
        debugConfiguration.project = launchConfig.project_path;
        debugConfiguration.configuration = 'Debug';
        debugConfiguration.noDebug = !launchOptions.debug;
        debugConfiguration.skipDebug = !launchOptions.debug;
        // The MAUI extension keys its Apple launch customization off sessionId;
        // without it the build can complete without reaching mlaunch/install.
        debugConfiguration.sessionId = launchOptions.runId;
        // The MAUI extension contributes this task dynamically; Aspire must not require
        // or generate a workspace tasks.json entry for it.
        debugConfiguration.preLaunchTask = mauiBuildTask;
        delete debugConfiguration.program;
        delete debugConfiguration.args;
        delete debugConfiguration.env;
        registerMauiLaunchJsonConfigurationWrapper(launchOptions.runId, launchConfig.project_path);
        registerMauiInfrastructureEnvironmentScrubber(launchOptions.runId);
        applyStructuredLaunchConfiguration(launchConfig, debugConfiguration);
        applyDotnetRunArgumentsToMauiConfiguration(args, debugConfiguration, !hasStructuredBuildMetadata(launchConfig));

        if (launchConfig.target_framework) {
            debugConfiguration.targetFramework = launchConfig.target_framework;
        }

        const platform = getMauiPlatform(launchConfig);
        if (platform) {
            debugConfiguration.platform = platform;
        }

        applyEnvironmentToMauiConfiguration(env, debugConfiguration, platform);

        const msbuildProperties = getMsBuildProperties(debugConfiguration);
        const targetKind = getMauiTargetKind(launchConfig, msbuildProperties, debugConfiguration.runtimeIdentifier);
        const configuredDevice = launchConfig.device ?? getExplicitDeviceFromMsBuildProperties(msbuildProperties);
        const defaultDesktopDevice = getDefaultDesktopDevice(platform, targetKind);
        const resolvedDevice = await resolveExplicitDevice(platform, targetKind, configuredDevice) ??
            (defaultDesktopDevice ? createResolvedDeviceTargetFromIdentifier(defaultDesktopDevice) : undefined) ??
            await resolveDefaultDevice(platform, targetKind);
        if (resolvedDevice) {
            if (resolvedDevice.useDebugConfigurationDevice) {
                debugConfiguration.device = resolvedDevice.device;
                applyAndroidDebugTargetProperties(debugConfiguration, platform, msbuildProperties, resolvedDevice.device);
            } else {
                removeAndroidDebugTargetProperties(debugConfiguration, platform, msbuildProperties);
            }
            if (platform === 'ios') {
                applyIosDebugTargetProperties(debugConfiguration, targetKind, resolvedDevice.device);
            }
            registerMauiActiveDebugTargetWrapper(launchOptions.runId, platform, resolvedDevice.device);
        }

        if (!debugConfiguration.cwd) {
            debugConfiguration.cwd = path.dirname(launchConfig.project_path);
        }
    }
};
