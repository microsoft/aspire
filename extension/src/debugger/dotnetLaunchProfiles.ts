import * as path from 'path';
import { EnvVar, ProjectLaunchConfiguration } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';
import { LaunchProfile, LaunchSettings } from './dotnetLaunchSettings';

/**
 * Expands environment variable references in a string.
 * Supports $(VAR) and %VAR% syntax used by launch profiles.
 */
export function expandEnvironmentVariables(value: string): string {
    let result = value.replace(/\$\(([^)]+)\)/g, (_, varName) => process.env[varName] ?? '');
    result = result.replace(/%([^%]+)%/g, (_, varName) => process.env[varName] ?? '');
    return result;
}

/**
 * Well-known launch profile command names (lowercased for case-insensitive comparison).
 */
export const LaunchProfileCommandName = {
    project: 'project',
    executable: 'executable',
} as const;

export interface LaunchProfileResult {
    profile: LaunchProfile | null;
    profileName: string | null;
}

/**
 * Determines the base launch profile according to the Aspire launch profile rules.
 */
export function determineBaseLaunchProfile(
    launchConfig: ProjectLaunchConfiguration,
    launchSettings: LaunchSettings | null
): LaunchProfileResult {
    const debugMessage =
        `[launchProfile] determineBaseLaunchProfile:
  disable_launch_profile=${!!launchConfig.disable_launch_profile}
  launch_profile='${launchConfig.launch_profile ?? ''}'
  hasLaunchSettings=${!!launchSettings}
  profileCount=${launchSettings?.profiles ? Object.keys(launchSettings.profiles).length : 0}`;
    extensionLogOutputChannel.debug(debugMessage);

    if (launchConfig.disable_launch_profile === true) {
        extensionLogOutputChannel.debug('Launch profile disabled via disable_launch_profile=true');
        return { profile: null, profileName: null };
    }

    if (!launchSettings || !launchSettings.profiles) {
        extensionLogOutputChannel.debug('No launch settings or profiles available');
        return { profile: null, profileName: null };
    }

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

    for (const [name, profile] of Object.entries(launchSettings.profiles)) {
        if (profile.commandName?.toLowerCase() === LaunchProfileCommandName.project) {
            extensionLogOutputChannel.debug(`Using default launch profile: ${name}`);
            return { profile, profileName: name };
        }
    }

    extensionLogOutputChannel.debug('No base launch profile determined');
    return { profile: null, profileName: null };
}

/**
 * Merges environment variables from launch profile with run session environment variables.
 * Run session variables take precedence over launch profile variables.
 */
export function mergeEnvironmentVariables(
    launchProfileEnv: { [key: string]: string } | undefined,
    debugConfigEnv : { [key: string]: string } | undefined,
    runSessionEnv: EnvVar[],
    runApiEnv?: { [key: string]: string }
): [string, string][] {
    const merged: { [key: string]: string } = {};

    if (launchProfileEnv) {
        Object.assign(merged, launchProfileEnv);
    }

    if (debugConfigEnv) {
        Object.assign(merged, debugConfigEnv);
    }

    if (runApiEnv) {
        Object.assign(merged, runApiEnv);
    }

    for (const envVar of runSessionEnv) {
        merged[envVar.name] = envVar.value;
    }

    return Object.entries(merged);
}

/**
 * Determines the final arguments array according to launch profile rules.
 * If run session args are present (including empty array), they completely replace launch profile args.
 * If run session args are absent/null, launch profile args are used if available.
 */
export function determineArguments(
    baseProfileArgs: string | undefined,
    runSessionArgs: string[] | undefined | null
): string | undefined {
    if (runSessionArgs !== undefined && runSessionArgs !== null) {
        extensionLogOutputChannel.debug(`Using run session arguments: ${JSON.stringify(runSessionArgs)}`);
        return runSessionArgs.join(' ');
    }

    if (baseProfileArgs) {
        extensionLogOutputChannel.debug(`Using launch profile arguments: ${baseProfileArgs}`);
        return baseProfileArgs;
    }

    extensionLogOutputChannel.debug('No arguments determined');
    return undefined;
}

/**
 * Determines the working directory for project execution.
 * Uses launch profile WorkingDirectory if specified, otherwise uses project directory.
 */
export function determineWorkingDirectory(
    projectPath: string,
    baseProfile: LaunchProfile | null
): string {
    const normalizeToPosixPath = (value: string): string => path.posix.normalize(value.replace(/\\/g, '/'));

    if (baseProfile?.workingDirectory) {
        const workingDirectory = expandEnvironmentVariables(baseProfile.workingDirectory);
        const isAbsoluteWorkingDirectory = path.isAbsolute(workingDirectory) || path.win32.isAbsolute(workingDirectory);
        const isWindowsProjectPath = path.win32.isAbsolute(projectPath);

        if (isAbsoluteWorkingDirectory) {
            const normalizedWorkingDirectory = normalizeToPosixPath(workingDirectory);
            extensionLogOutputChannel.debug(`Using absolute working directory from launch profile: ${normalizedWorkingDirectory}`);
            return normalizedWorkingDirectory;
        } else {
            const projectDir = isWindowsProjectPath ? path.win32.dirname(projectPath) : path.dirname(projectPath);
            const resolvedWorkingDirectory = isWindowsProjectPath
                ? path.win32.resolve(projectDir, workingDirectory)
                : path.resolve(projectDir, workingDirectory);
            const normalizedWorkingDirectory = normalizeToPosixPath(resolvedWorkingDirectory);
            extensionLogOutputChannel.debug(`Using relative working directory from launch profile: ${normalizedWorkingDirectory}`);
            return normalizedWorkingDirectory;
        }
    }

    const projectDir = path.win32.isAbsolute(projectPath) ? path.win32.dirname(projectPath) : path.dirname(projectPath);
    const normalizedProjectDir = normalizeToPosixPath(projectDir);
    extensionLogOutputChannel.debug(`Using default working directory (project directory): ${normalizedProjectDir}`);
    return normalizedProjectDir;
}
