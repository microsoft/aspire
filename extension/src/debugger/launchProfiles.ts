import * as path from 'path';
import * as fs from 'fs';
import { ExecutableLaunchConfiguration, EnvVar, ProjectLaunchConfiguration } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';
import { isFileBasedApp } from './languages/dotnet';
import { stripComments, parseTree, findNodeAtLocation } from 'jsonc-parser';
import { aspireConfigFileName, AspireConfigProfile } from '../utils/cliTypes';

/*
 * Represents a launchSettings.json profile.
 * Only a property that is available both in the C# vscode debugger (https://code.visualstudio.com/docs/csharp/debugger-settings)
 * *and* in the launchSettings.json is available here.
*/
export interface LaunchProfile {
    commandName: string;
    executablePath?: string;
    workingDirectory?: string;
    // args in debug configuration
    commandLineArgs?: string;
    // Both these properties must be set to launch the browser. See
    // https://code.visualstudio.com/docs/csharp/debugger-settings#_starting-a-web-browser
    launchBrowser?: boolean;
    applicationUrl?: string;
    // env in debug configuration
    environmentVariables?: { [key: string]: string };
    // checkForDevCert in debug configuration
    useSSL?: boolean;
    // The URL to launch in the browser. May be absolute (e.g. "https://my.localhost");
    // when relative, it is resolved against the first applicationUrl entry.
    launchUrl?: string;
}

/**
 * Expands environment variable references in a string.
 * Supports $(VAR) and %VAR% syntax used by launch profiles.
 */
export function expandEnvironmentVariables(value: string): string {
    // Expand $(VAR) syntax (used by VS and MSBuild-style launch profiles)
    let result = value.replace(/\$\(([^)]+)\)/g, (_, varName) => process.env[varName] ?? '');
    // Expand %VAR% syntax (Windows)
    result = result.replace(/%([^%]+)%/g, (_, varName) => process.env[varName] ?? '');
    return result;
}

/**
 * Well-known launch profile command names, using the exact casing the .NET SDK uses.
 *
 * The SDK's provider table (`LaunchSettings.s_providers`) is an ordinal, case-sensitive dictionary keyed
 * by these exact strings, so command-name comparisons must match that casing rather than lowercasing.
 * A profile such as `commandName: "executable"` is therefore NOT a supported provider and `dotnet run` /
 * `dotnet run-api` skips it. See
 * https://github.com/dotnet/sdk/blob/main/src/Microsoft.DotNet.ProjectTools/LaunchSettings/LaunchSettings.cs
 */
export const LaunchProfileCommandName = {
    project: 'Project',
    executable: 'Executable',
} as const;

// Command names that `dotnet run` / `dotnet run-api` recognize when picking the *default* launch profile.
// The SDK selects the first profile whose commandName maps to a supported provider, and its provider table
// currently contains both 'Project' and 'Executable'. Keep this in sync with the SDK's provider table:
// https://github.com/dotnet/sdk/blob/main/src/Microsoft.DotNet.ProjectTools/LaunchSettings/LaunchSettings.cs
const defaultLaunchProfileCommandNames: ReadonlySet<string> = new Set([
    LaunchProfileCommandName.project,
    LaunchProfileCommandName.executable,
]);

export interface LaunchSettings {
    profiles: { [key: string]: LaunchProfile };
    // The profile names in launchSettings.json *source order*. JavaScript objects enumerate
    // integer-like keys (e.g. "10", "2") in ascending numeric order rather than insertion order, so
    // `Object.keys(profiles)` cannot be trusted to reflect the order profiles appear in the file. The
    // .NET SDK selects the default launch profile using `JsonElement.EnumerateObject()`, which walks
    // the file in source order, so we must preserve that order here to pick the same default profile.
    // Populated by readLaunchSettings; may be absent for LaunchSettings constructed by other means.
    profileOrder?: readonly string[];
}

export interface LaunchProfileResult {
    profile: LaunchProfile | null;
    profileName: string | null;
}

/**
 * Extracts the profile names from launchSettings.json (or aspire.config.json) content in *source
 * order* using a JSONC syntax tree, rather than relying on parsed-object key enumeration.
 *
 * This is necessary because integer-like profile names (e.g. "10", "2") are reordered by JavaScript
 * object key enumeration (numeric keys first, in ascending order), which does not match the file
 * order the .NET SDK uses when selecting the default launch profile. Walking the parse tree preserves
 * the exact order the properties appear in the file.
 *
 * Returns undefined when the content has no `profiles` object so callers can fall back to key order.
 */
function extractProfileOrder(content: string): string[] | undefined {
    const root = parseTree(content);
    if (!root) {
        return undefined;
    }

    const profilesNode = findNodeAtLocation(root, ['profiles']);
    if (profilesNode?.type !== 'object' || !profilesNode.children) {
        return undefined;
    }

    const order: string[] = [];
    for (const propertyNode of profilesNode.children) {
        // Each object member is a 'property' node whose first child is the key (a string node).
        const keyNode = propertyNode.children?.[0];
        if (typeof keyNode?.value === 'string') {
            order.push(keyNode.value);
        }
    }

    return order;
}

/**
 * Reads and parses the launchSettings.json file for a given project
 */
export async function readLaunchSettings(projectPath: string): Promise<LaunchSettings | null> {
    try {
        let launchSettingsPath: string;

        if (isFileBasedApp(projectPath)) {
            // Mirror the .NET SDK's launch-settings discovery for `dotnet run` / `dotnet run-api`
            // (LaunchSettings.TryFindLaunchSettingsFile): for a file-based app the SDK looks next to the
            // entry-point `.cs` file and prefers `Properties/launchSettings.json`, only falling back to
            // `<app>.run.json` when the former is absent. If both exist, `<app>.run.json` is ignored.
            const dir = path.dirname(projectPath);
            const propertiesLaunchSettingsPath = path.join(dir, 'Properties', 'launchSettings.json');
            const fileNameWithoutExt = path.basename(projectPath, path.extname(projectPath));
            const runJsonPath = path.join(dir, `${fileNameWithoutExt}.run.json`);

            if (fs.existsSync(propertiesLaunchSettingsPath)) {
                if (fs.existsSync(runJsonPath)) {
                    extensionLogOutputChannel.warn(`Both '${propertiesLaunchSettingsPath}' and '${runJsonPath}' exist; using '${propertiesLaunchSettingsPath}' to match 'dotnet run'. '${runJsonPath}' is ignored.`);
                }

                launchSettingsPath = propertiesLaunchSettingsPath;
            } else {
                launchSettingsPath = runJsonPath;
            }
        } else {
            const projectDir = path.dirname(projectPath);
            launchSettingsPath = path.join(projectDir, 'Properties', 'launchSettings.json');
        }

        if (fs.existsSync(launchSettingsPath)) {
            let content = fs.readFileSync(launchSettingsPath, 'utf8');
            // We need to strip comments from the JSON file before parsing
            content = stripComments(content);
            const launchSettings = JSON.parse(content) as LaunchSettings;
            // Capture the profile order from the file so the default-profile selection matches the SDK.
            launchSettings.profileOrder = extractProfileOrder(content);

            extensionLogOutputChannel.debug(`Successfully read launch settings from: ${launchSettingsPath}`);
            return launchSettings;
        }

        extensionLogOutputChannel.debug(`Launch settings file not found at: ${launchSettingsPath}`);

        // Fall back to aspire.config.json profiles
        const aspireConfigPath = path.join(path.dirname(projectPath), aspireConfigFileName);
        if (fs.existsSync(aspireConfigPath)) {
            let content = fs.readFileSync(aspireConfigPath, 'utf8');
            content = stripComments(content);
            const aspireConfig = JSON.parse(content);

            if (aspireConfig?.profiles && typeof aspireConfig.profiles === 'object') {
                // Convert aspire.config.json profiles to LaunchSettings format
                const profiles: { [key: string]: LaunchProfile } = {};
                for (const [name, profile] of Object.entries(aspireConfig.profiles)) {
                    const p = profile as AspireConfigProfile;
                    profiles[name] = {
                        commandName: 'Project',
                        applicationUrl: p.applicationUrl,
                        environmentVariables: p.environmentVariables,
                    };
                }

                extensionLogOutputChannel.debug(`Successfully read launch profiles from: ${aspireConfigPath}`);
                return { profiles, profileOrder: extractProfileOrder(content) };
            }
        }

        return null;
    } catch (error) {
        extensionLogOutputChannel.error(`Failed to read launch settings for project ${projectPath}: ${error}`);
        return null;
    }
}

/**
 * Determines the base launch profile according to the Aspire launch profile rules
 */
export function determineBaseLaunchProfile(
    launchConfig: ProjectLaunchConfiguration,
    launchSettings: LaunchSettings | null
): LaunchProfileResult {
    // If disable_launch_profile property is set to true in project launch configuration, there is no base profile, regardless of the value of launch_profile property.
    if (launchConfig.disable_launch_profile === true) {
        extensionLogOutputChannel.debug('Launch profile disabled via disable_launch_profile=true');
        return { profile: null, profileName: null };
    }

    if (!launchSettings || !launchSettings.profiles) {
        extensionLogOutputChannel.debug('No launch settings or profiles available');
        return { profile: null, profileName: null };
    }

    // If launch_profile property is set, check if that profile exists
    if (launchConfig.launch_profile) {
        const profileName = launchConfig.launch_profile;
        const profile = launchSettings.profiles[profileName];

        if (profile) {
            extensionLogOutputChannel.debug(`Using explicit launch profile: ${profileName}`);
            return { profile, profileName };
        } else {
            extensionLogOutputChannel.debug(`Explicit launch profile '${profileName}' not found in launch settings`);
            return { profile: null, profileName: null };
        }
    }

    // If launch_profile is absent, fall back to the profile that `dotnet run` applies by default.
    const defaultProfile = determineDefaultLaunchProfile(launchSettings);
    if (defaultProfile.profile) {
        extensionLogOutputChannel.debug(`Using default launch profile: ${defaultProfile.profileName}`);
        return defaultProfile;
    }

    // TODO: If launch_profile is absent, check for a ServiceDefaults project in the workspace
    // and look for a launch profile with that ServiceDefaults project name in the current project's launch settings
    extensionLogOutputChannel.debug('No base launch profile determined');
    return { profile: null, profileName: null };
}

/**
 * Determines the launch profile that `dotnet run` / `dotnet run-api` applies by default: the first
 * profile whose commandName maps to a supported provider (currently 'Project' or 'Executable').
 *
 * This is NOT necessarily the first 'Project' profile. The SDK picks the first *supported* profile, so an
 * 'Executable' profile that appears earlier wins over a later 'Project' profile. See
 * {@link defaultLaunchProfileCommandNames}.
 *
 * `dotnet run-api` always applies this default profile because the extension invokes it without
 * selecting a profile, so run-api applies it regardless of which profile the extension itself resolves
 * via {@link determineBaseLaunchProfile} (or of `disable_launch_profile`). Callers use it to recognize
 * environment values that run-api copied from that default profile.
 */
export function determineDefaultLaunchProfile(launchSettings: LaunchSettings | null): LaunchProfileResult {
    if (!launchSettings?.profiles) {
        return { profile: null, profileName: null };
    }

    // Enumerate profiles in file source order to match the SDK's `JsonElement.EnumerateObject()`.
    // profileOrder (populated by readLaunchSettings) preserves that order even for integer-like
    // profile names; fall back to Object.keys for LaunchSettings constructed without it.
    const profileNames = launchSettings.profileOrder ?? Object.keys(launchSettings.profiles);

    for (const name of profileNames) {
        const profile = launchSettings.profiles[name];
        // Match the SDK's exact, case-sensitive provider lookup: a profile whose commandName differs only
        // in casing (e.g. "executable") is not a supported provider, so `dotnet run-api` would skip it too.
        if (profile?.commandName && defaultLaunchProfileCommandNames.has(profile.commandName)) {
            return { profile, profileName: name };
        }
    }

    return { profile: null, profileName: null };
}

/**
 * Merges environment variables from launch profile with run session environment variables
 * Run session variables take precedence over launch profile variables
 */
export function mergeEnvironmentVariables(
    launchProfileEnv: { [key: string]: string } | undefined,
    debugConfigEnv : { [key: string]: string } | undefined,
    runSessionEnv: EnvVar[],
    runApiEnv?: { [key: string]: string }
): [string, string][] {
    const merged: { [key: string]: string } = {};

    // Start with base profile environment variables
    if (launchProfileEnv) {
        Object.assign(merged, launchProfileEnv);
    }

    // Override with debug configuration environment variables
    if (debugConfigEnv) {
        Object.assign(merged, debugConfigEnv);
    }

    // Override with run API environment variables
    if (runApiEnv) {
        Object.assign(merged, runApiEnv);
    }

    // Override with run session environment variables (these take precedence)
    for (const envVar of runSessionEnv) {
        merged[envVar.name] = envVar.value;
    }

    return Object.entries(merged);
}

/**
 * Determines the final arguments array according to launch profile rules
 * If run session args are present (including empty array), they completely replace launch profile args
 * If run session args are absent/null, launch profile args are used if available
 */
export function determineArguments(
    baseProfileArgs: string | undefined,
    runSessionArgs: string[] | undefined | null
): string | undefined {
    // If run session args are explicitly provided (including empty array), use them
    if (runSessionArgs !== undefined && runSessionArgs !== null) {
        extensionLogOutputChannel.debug(`Using run session arguments: ${JSON.stringify(runSessionArgs)}`);
        return runSessionArgs.join(' ');
    }

    // If run session args are absent/null, use launch profile args if available
    if (baseProfileArgs) {
        extensionLogOutputChannel.debug(`Using launch profile arguments: ${baseProfileArgs}`);
        return baseProfileArgs;
    }

    extensionLogOutputChannel.debug('No arguments determined');
    return undefined;
}

/**
 * Determines the working directory for project execution
 * Uses launch profile WorkingDirectory if specified, otherwise uses project directory
 */
export function determineWorkingDirectory(
    projectPath: string,
    baseProfile: LaunchProfile | null
): string {
    if (baseProfile?.workingDirectory) {
        const workingDirectory = expandEnvironmentVariables(baseProfile.workingDirectory);
        // If working directory is relative, resolve it relative to project directory
        if (path.isAbsolute(workingDirectory)) {
            extensionLogOutputChannel.debug(`Using absolute working directory from launch profile: ${workingDirectory}`);
            return workingDirectory;
        } else {
            const projectDir = path.dirname(projectPath);
            const workingDir = path.resolve(projectDir, workingDirectory);
            extensionLogOutputChannel.debug(`Using relative working directory from launch profile: ${workingDir}`);
            return workingDir;
        }
    }

    // Default to project directory
    const projectDir = path.dirname(projectPath);
    extensionLogOutputChannel.debug(`Using default working directory (project directory): ${projectDir}`);
    return projectDir;
}

interface ServerReadyAction {
    action: "openExternally";
    pattern: "\\bNow listening on:\\s+https?://\\S+";
    uriFormat: string;
}

export function determineServerReadyAction(launchBrowser?: boolean, applicationUrl?: string, launchUrl?: string): ServerReadyAction | undefined {
    if (!launchBrowser || !applicationUrl) {
        return undefined;
    }

    let uriFormat = applicationUrl.includes(';') ? applicationUrl.split(';')[0] : applicationUrl;

    if (launchUrl) {
        uriFormat = resolveLaunchUrl(launchUrl, uriFormat);
    }

    return {
        action: "openExternally",
        pattern: "\\bNow listening on:\\s+https?://\\S+",
        uriFormat: uriFormat
    };
}

function resolveLaunchUrl(launchUrl: string, applicationUrl: string): string {
    const absoluteLaunchUrl = tryCreateUrl(launchUrl);
    if (absoluteLaunchUrl) {
        return getHttpUrlOrFallback(absoluteLaunchUrl, applicationUrl, launchUrl);
    }

    const resolvedLaunchUrl = tryCreateUrl(launchUrl, applicationUrl);
    if (resolvedLaunchUrl) {
        return getHttpUrlOrFallback(resolvedLaunchUrl, applicationUrl, launchUrl);
    }

    extensionLogOutputChannel.warn(`Failed to resolve launchUrl '${launchUrl}' against applicationUrl '${applicationUrl}'. Falling back to applicationUrl.`);
    return applicationUrl;
}

function tryCreateUrl(url: string, base?: string): URL | undefined {
    try {
        return base ? new URL(url, base) : new URL(url);
    } catch {
        return undefined;
    }
}

function getHttpUrlOrFallback(url: URL, fallback: string, launchUrl: string): string {
    if (url.protocol === 'http:' || url.protocol === 'https:') {
        if (url.hostname === '*') {
            extensionLogOutputChannel.warn(`Ignoring launchUrl '${launchUrl}' because it resolves to wildcard host '*'. Falling back to applicationUrl.`);
            return fallback;
        }

        return url.href;
    }

    extensionLogOutputChannel.warn(`Ignoring launchUrl '${launchUrl}' because it resolves to unsupported scheme '${url.protocol}'. Falling back to applicationUrl.`);
    return fallback;
}
