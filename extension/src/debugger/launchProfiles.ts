import * as path from 'path';
import * as fs from 'fs';
import { DebugConfigurationArguments, ExecutableLaunchConfiguration, EnvVar, ProjectLaunchConfiguration } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';
import { isFileBasedApp } from './languages/dotnet';
import { getNodeValue, Node, parse, ParseError, parseTree } from 'jsonc-parser';
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

export function hasSdkCompatibleLaunchProfileProperties(profile: unknown): profile is LaunchProfile {
    if (!profile || typeof profile !== 'object' || Array.isArray(profile)) {
        return false;
    }

    const value = profile as Record<string, unknown>;
    if (typeof value.commandName !== 'string') {
        return false;
    }

    for (const property of ['commandLineArgs']) {
        if (value[property] !== undefined && value[property] !== null && typeof value[property] !== 'string') {
            return false;
        }
    }

    for (const property of ['dotnetRunMessages']) {
        if (value[property] !== undefined && typeof value[property] !== 'boolean') {
            return false;
        }
    }

    if (value.environmentVariables !== undefined && value.environmentVariables !== null) {
        if (typeof value.environmentVariables !== 'object' ||
            Array.isArray(value.environmentVariables) ||
            Object.values(value.environmentVariables).some(environmentValue => typeof environmentValue !== 'string')) {
            return false;
        }
    }

    // The SDK deserializes each provider with a different model and ignores properties from
    // other providers, so validate only the fields consumed by the selected parser.
    // https://github.com/dotnet/sdk/tree/main/src/Microsoft.DotNet.ProjectTools/LaunchSettings
    switch (value.commandName) {
        case LaunchProfileCommandName.project:
            for (const property of ['applicationUrl', 'launchUrl']) {
                if (value[property] !== undefined && value[property] !== null && typeof value[property] !== 'string') {
                    return false;
                }
            }

            return value.launchBrowser === undefined || typeof value.launchBrowser === 'boolean';
        case LaunchProfileCommandName.executable:
            for (const property of ['executablePath', 'workingDirectory']) {
                if (value[property] !== undefined && value[property] !== null && typeof value[property] !== 'string') {
                    return false;
                }
            }

            return true;
        default:
            return true;
    }
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

export function expandSdkEnvironmentVariables(
    value: string,
    environment: NodeJS.ProcessEnv = process.env
): string {
    // Match Environment.ExpandEnvironmentVariables, which the SDK launch-profile parser uses:
    // https://github.com/dotnet/sdk/blob/main/src/Microsoft.DotNet.ProjectTools/LaunchSettings/ExecutableLaunchProfileParser.cs
    let result = '';
    let currentIndex = 0;

    while (currentIndex < value.length) {
        const variableStart = value.indexOf('%', currentIndex);
        if (variableStart < 0) {
            result += value.slice(currentIndex);
            break;
        }

        const variableEnd = value.indexOf('%', variableStart + 1);
        if (variableEnd < 0) {
            result += value.slice(currentIndex);
            break;
        }

        const variableName = value.slice(variableStart + 1, variableEnd);
        const variableValue = getSdkEnvironmentVariable(environment, variableName);
        if (variableValue !== undefined) {
            result += value.slice(currentIndex, variableStart) + variableValue;
            currentIndex = variableEnd + 1;
        } else if (process.platform === 'win32') {
            // Windows delegates to ExpandEnvironmentStringsW, which leaves an unresolved span
            // intact and continues after its closing delimiter.
            // https://learn.microsoft.com/windows/win32/api/processenv/nf-processenv-expandenvironmentstringsw
            result += value.slice(currentIndex, variableEnd + 1);
            currentIndex = variableEnd + 1;
        } else {
            // The Unix runtime reconsiders the closing '%' as the start of another variable, so
            // adjacent references can share a delimiter.
            // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Environment.UnixOrBrowser.cs
            result += value.slice(currentIndex, variableEnd);
            currentIndex = variableEnd;
        }
    }

    return result;
}

function getSdkEnvironmentVariable(environment: NodeJS.ProcessEnv, name: string): string | undefined {
    if (process.platform !== 'win32') {
        return environment[name];
    }

    // Windows environment names are case-insensitive, including when the supplied environment is
    // a plain snapshot rather than Node's special process.env object.
    const normalizedName = name.toLowerCase();
    const matchingName = Object.keys(environment).find(candidate => candidate.toLowerCase() === normalizedName);
    return matchingName ? environment[matchingName] : undefined;
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
    // Relative profile paths are resolved from the launch-settings file, not the project file.
    sourceDirectory?: string;
    // The profile names in launchSettings.json *source order*. JavaScript objects enumerate
    // integer-like keys (e.g. "10", "2") in ascending numeric order rather than insertion order, so
    // `Object.keys(profiles)` cannot be trusted to reflect the order profiles appear in the file. The
    // .NET SDK selects the default launch profile using `JsonElement.EnumerateObject()`, which walks
    // the file in source order, so we must preserve that order here to pick the same default profile.
    // Populated by readLaunchSettings; may be absent for LaunchSettings constructed by other means.
    profileOrder?: readonly string[];
    profileEntries?: readonly LaunchProfileSourceEntry[];
}

export interface LaunchProfileSourceEntry {
    name: string;
    profile: LaunchProfile;
    hasInvalidProperties: boolean;
}

export interface LaunchProfileResult {
    profile: LaunchProfile | null;
    profileName: string | null;
    hasInvalidProperties?: boolean;
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
    // JSON permits duplicate properties in practice, and both JSON.parse and the SDK use the last
    // top-level "profiles" value. Preserve the order from that same object.
    const profilesNode = extractProfilesNode(content);
    if (!profilesNode?.children) {
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

function extractProfilesNode(content: string): Node | undefined {
    const root = parseTree(content, [], { allowTrailingComma: true });
    const profileProperties = root?.children?.filter(propertyNode =>
        propertyNode.type === 'property' &&
        propertyNode.children?.[0]?.value === 'profiles');

    const profilesNode = profileProperties?.[profileProperties.length - 1]?.children?.[1];
    return profilesNode?.type === 'object' ? profilesNode : undefined;
}

function extractLaunchProfileSourceEntries(content: string): LaunchProfileSourceEntry[] | undefined {
    const profilesNode = extractProfilesNode(content);
    if (!profilesNode?.children) {
        return undefined;
    }

    const entries: LaunchProfileSourceEntry[] = [];
    for (const profileProperty of profilesNode.children) {
        const profileName = profileProperty.children?.[0]?.value;
        const profileNode = profileProperty.children?.[1];
        if (typeof profileName !== 'string' || !profileNode) {
            continue;
        }

        const profile = getNodeValue(profileNode) as LaunchProfile;
        if (profileNode.type !== 'object' || !profileNode.children) {
            entries.push({ name: profileName, profile, hasInvalidProperties: false });
            continue;
        }

        const commandNameNode = profileNode.children
            .filter(propertyNode => propertyNode.children?.[0]?.value === 'commandName')
            .at(-1)?.children?.[1];
        if (commandNameNode?.type !== 'string') {
            entries.push({ name: profileName, profile, hasInvalidProperties: false });
            continue;
        }

        const stringProperties = new Set(['commandLineArgs']);
        const booleanProperties = new Set(['dotnetRunMessages']);
        if (commandNameNode.value === LaunchProfileCommandName.project) {
            stringProperties.add('applicationUrl').add('launchUrl');
            booleanProperties.add('launchBrowser');
        } else if (commandNameNode.value === LaunchProfileCommandName.executable) {
            stringProperties.add('executablePath').add('workingDirectory');
        } else {
            entries.push({ name: profileName, profile, hasInvalidProperties: false });
            continue;
        }

        const hasInvalidProperty = profileNode.children.some(propertyNode => {
            const propertyName = propertyNode.children?.[0]?.value;
            const propertyValue = propertyNode.children?.[1];
            if (typeof propertyName !== 'string' || !propertyValue) {
                return false;
            }

            if (stringProperties.has(propertyName)) {
                return propertyValue.type !== 'string' && propertyValue.type !== 'null';
            }

            if (booleanProperties.has(propertyName)) {
                return propertyValue.type !== 'boolean';
            }

            if (propertyName !== 'environmentVariables') {
                return false;
            }

            if (propertyValue.type === 'null') {
                return false;
            }

            return propertyValue.type !== 'object' ||
                propertyValue.children?.some(environmentProperty =>
                    environmentProperty.children?.[1]?.type !== 'string') === true;
        });

        entries.push({ name: profileName, profile, hasInvalidProperties: hasInvalidProperty });
    }

    return entries;
}

function parseJsonContent<T>(content: string): { value: T; normalizedContent: string } {
    const normalizedContent = content.charCodeAt(0) === 0xFEFF ? content.slice(1) : content;
    const errors: ParseError[] = [];
    const value = parse(normalizedContent, errors, { allowTrailingComma: true }) as T;
    if (errors.length > 0) {
        throw new SyntaxError(`Invalid JSON at offset ${errors[0].offset}.`);
    }

    return { value, normalizedContent };
}

function equalsOrdinalIgnoreCase(left: string, right: string): boolean {
    if (left.length !== right.length) {
        return false;
    }

    const toOrdinalUpperCase = (value: string) => Array.from(value, character => {
        // .NET ordinal casing deliberately excludes the Turkish dotless I and long S mappings.
        // JavaScript also performs multi-character uppercase expansions that ordinal casing omits.
        if (character === '\u0131' || character === '\u017F') {
            return character;
        }

        const upper = character.toUpperCase();
        return upper.length === character.length ? upper : character;
    }).join('');

    return toOrdinalUpperCase(left) === toOrdinalUpperCase(right);
}

/**
 * Reads and parses the launchSettings.json file for a given project
 */
export async function readLaunchSettings(projectPath: string): Promise<LaunchSettings | null> {
    try {
        let launchSettingsPath: string;
        const isFileBasedProject = isFileBasedApp(projectPath);

        if (isFileBasedProject) {
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
            const projectName = path.basename(projectPath, path.extname(projectPath));
            const launchSettingsDirectory = path.extname(projectPath).toLowerCase() === '.vbproj'
                ? 'My Project'
                : 'Properties';
            const propertiesLaunchSettingsPath = path.join(projectDir, launchSettingsDirectory, 'launchSettings.json');
            const runJsonPath = path.join(projectDir, `${projectName}.run.json`);

            if (fs.existsSync(propertiesLaunchSettingsPath)) {
                if (fs.existsSync(runJsonPath)) {
                    extensionLogOutputChannel.warn(`Both '${propertiesLaunchSettingsPath}' and '${runJsonPath}' exist; using '${propertiesLaunchSettingsPath}' to match 'dotnet run'. '${runJsonPath}' is ignored.`);
                }

                launchSettingsPath = propertiesLaunchSettingsPath;
            } else {
                launchSettingsPath = runJsonPath;
            }
        }

        if (fs.existsSync(launchSettingsPath)) {
            const { value: launchSettings, normalizedContent } = parseJsonContent<LaunchSettings>(
                fs.readFileSync(launchSettingsPath, 'utf8'));
            // Capture the profile order from the file so the default-profile selection matches the SDK.
            launchSettings.profileEntries = extractLaunchProfileSourceEntries(normalizedContent);
            launchSettings.profileOrder = launchSettings.profileEntries?.map(entry => entry.name);
            launchSettings.sourceDirectory = path.dirname(launchSettingsPath);

            extensionLogOutputChannel.debug(`Successfully read launch settings from: ${launchSettingsPath}`);
            return launchSettings;
        }

        extensionLogOutputChannel.debug(`Launch settings file not found at: ${launchSettingsPath}`);

        if (!isFileBasedProject) {
            return null;
        }

        // File-based apps created by older CLI versions stored profiles in aspire.config.json.
        const aspireConfigPath = path.join(path.dirname(projectPath), aspireConfigFileName);
        if (fs.existsSync(aspireConfigPath)) {
            const { value: aspireConfig, normalizedContent } = parseJsonContent<Record<string, unknown>>(
                fs.readFileSync(aspireConfigPath, 'utf8'));

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
                return {
                    profiles,
                    profileOrder: extractProfileOrder(normalizedContent),
                    sourceDirectory: path.dirname(aspireConfigPath)
                };
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
        const profileEntries = launchSettings.profileEntries ??
            (launchSettings.profileOrder ?? Object.keys(launchSettings.profiles))
                .map(name => ({ name, profile: launchSettings.profiles[name], hasInvalidProperties: false }));
        const matchingProfileEntries = profileEntries
            .filter(candidate => equalsOrdinalIgnoreCase(candidate.name, profileName));
        if (matchingProfileEntries.length === 1) {
            const matchingProfile = matchingProfileEntries[0];
            extensionLogOutputChannel.debug(`Using explicit launch profile: ${profileName}`);
            return {
                profile: matchingProfile.profile,
                profileName,
                hasInvalidProperties: matchingProfile.hasInvalidProperties
            };
        }

        extensionLogOutputChannel.debug(`Explicit launch profile '${profileName}' not found uniquely in launch settings`);
        return { profile: null, profileName: null };
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
    // profileEntries (populated by readLaunchSettings) preserves both source order and duplicate
    // profile values; fall back to profileOrder/Object.keys for programmatically constructed settings.
    const profileEntries = launchSettings.profileEntries ??
        (launchSettings.profileOrder ?? Object.keys(launchSettings.profiles))
            .map(name => ({ name, profile: launchSettings.profiles[name], hasInvalidProperties: false }));

    for (const entry of profileEntries) {
        const { name, profile, hasInvalidProperties } = entry;
        // Match the SDK's exact, case-sensitive provider lookup: a profile whose commandName differs only
        // in casing (e.g. "executable") is not a supported provider, so `dotnet run-api` would skip it too.
        if (profile?.commandName && defaultLaunchProfileCommandNames.has(profile.commandName)) {
            return {
                profile,
                profileName: name,
                hasInvalidProperties
            };
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
 * Determines the final debugger arguments according to launch profile rules.
 * Launch-profile-authored text stays a string, while forwarded run-session tokens stay tokenized.
 * If run session args are present (including empty array), they completely replace launch profile args
 * If run session args are absent/null, launch profile args are used if available
 */
export function determineArguments(
    baseProfileArgs: string | undefined,
    runSessionArgs: string[] | undefined | null
): DebugConfigurationArguments | undefined {
    // If run session args are explicitly provided (including empty array), use them
    if (runSessionArgs !== undefined && runSessionArgs !== null) {
        extensionLogOutputChannel.debug(`Using run session arguments (count: ${runSessionArgs.length})`);
        return [...runSessionArgs];
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
    baseProfile: LaunchProfile | null,
    launchSettingsDirectory?: string
): string {
    if (baseProfile?.workingDirectory !== undefined && baseProfile.workingDirectory !== null) {
        const workingDirectory = launchSettingsDirectory !== undefined
            ? expandSdkEnvironmentVariables(baseProfile.workingDirectory)
            : expandEnvironmentVariables(baseProfile.workingDirectory);
        // The SDK resolves a relative workingDirectory from the launch-settings file. This matters
        // for Properties/launchSettings.json because its directory differs from the project directory.
        const isRooted = path.isAbsolute(workingDirectory) ||
            (process.platform === 'win32' && /^[a-zA-Z]:/.test(workingDirectory));
        if (isRooted) {
            // Preserve existing behavior for callers that construct profiles without source metadata.
            const workingDir = launchSettingsDirectory !== undefined ? path.resolve(workingDirectory) : workingDirectory;
            extensionLogOutputChannel.debug(`Using absolute working directory from launch profile: ${workingDir}`);
            return workingDir;
        } else {
            const baseDirectory = launchSettingsDirectory ?? path.dirname(projectPath);
            const workingDir = path.resolve(baseDirectory, workingDirectory);
            extensionLogOutputChannel.debug(`Using relative working directory from launch profile: ${workingDir}`);
            return workingDir;
        }
    }

    // Default to project directory
    const projectDir = path.dirname(projectPath);
    extensionLogOutputChannel.debug(`Using default working directory (project directory): ${projectDir}`);
    return projectDir;
}
