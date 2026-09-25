import type { AspireExtendedDebugConfiguration } from '../dcp/types';

export const maxLaunchProfileLength = 256;

const identityChangingCharacters = /[\u0000-\u001F\u007F-\u009F]|\p{Cf}/u;

export function isValidLaunchProfile(value: unknown): value is string {
    return typeof value === 'string' &&
        value.trim().length > 0 &&
        value.length <= maxLaunchProfileLength &&
        !identityChangingCharacters.test(value);
}

/**
 * Replaces root launch-profile arguments while preserving AppHost arguments after `--`.
 *
 * Aspire accepts these root forms:
 *   --launch-profile Development
 *   --launch-profile=Development
 *   -lp Development
 *   -lp=Development
 *
 * The normalized equals form also preserves profile names that begin with `-`, which an
 * option parser would otherwise interpret as another option.
 */
export function ensureLaunchProfileCliArg(args: string[] | undefined, launchProfile: string): string[] {
    const existing = args ?? [];
    const separatorIndex = existing.indexOf('--');
    const rootArguments = separatorIndex === -1 ? existing : existing.slice(0, separatorIndex);
    const filteredRootArguments: string[] = [];

    for (let i = 0; i < rootArguments.length; i++) {
        const argument = rootArguments[i];
        if (argument === '--launch-profile' || argument === '-lp') {
            if (i + 1 < rootArguments.length && !rootArguments[i + 1].startsWith('-')) {
                i++;
            }
        }
        else if (argument.startsWith('--launch-profile=') || argument.startsWith('-lp=')) {
            continue;
        }
        else {
            filteredRootArguments.push(argument);
        }
    }

    const normalizedRootArguments = [...filteredRootArguments, `--launch-profile=${launchProfile}`];
    return separatorIndex === -1
        ? normalizedRootArguments
        : [...normalizedRootArguments, ...existing.slice(separatorIndex)];
}

export function removeRootLaunchProfileCliArg(args: string[] | undefined): string[] | undefined {
    if (args === undefined) {
        return undefined;
    }

    const separatorIndex = args.indexOf('--');
    const rootArguments = separatorIndex === -1 ? args : args.slice(0, separatorIndex);
    const filteredRootArguments: string[] = [];

    for (let i = 0; i < rootArguments.length; i++) {
        const argument = rootArguments[i];
        if (argument === '--launch-profile' || argument === '-lp') {
            if (i + 1 < rootArguments.length && !rootArguments[i + 1].startsWith('-')) {
                i++;
            }
        }
        else if (argument.startsWith('--launch-profile=') || argument.startsWith('-lp=')) {
            continue;
        }
        else {
            filteredRootArguments.push(argument);
        }
    }

    return separatorIndex === -1
        ? filteredRootArguments
        : [...filteredRootArguments, ...args.slice(separatorIndex)];
}

export function getAppHostLaunchProfileOptions(configuration: AspireExtendedDebugConfiguration | undefined, includeProjectSettings: boolean): {
    launchProfile: string | undefined;
    disableLaunchProfile: boolean | undefined;
} {
    const projectDebuggerSettings = includeProjectSettings
        ? configuration?.debuggers?.['project']
        : undefined;
    const appHostDebuggerSettings = configuration?.debuggers?.['apphost'];

    return {
        launchProfile: appHostDebuggerSettings?.launchProfile
            ?? projectDebuggerSettings?.launchProfile
            ?? configuration?.launchProfile,
        disableLaunchProfile: appHostDebuggerSettings?.disableLaunchProfile
            ?? projectDebuggerSettings?.disableLaunchProfile,
    };
}

export function getRootLaunchProfileCliArg(args: readonly string[]): string | undefined {
    const separatorIndex = args.indexOf('--');
    const rootArguments = separatorIndex === -1 ? args : args.slice(0, separatorIndex);

    for (let i = 0; i < rootArguments.length; i++) {
        const argument = rootArguments[i];
        if (argument.startsWith('--launch-profile=') || argument.startsWith('-lp=')) {
            return argument.slice(argument.indexOf('=') + 1);
        }

        if ((argument === '--launch-profile' || argument === '-lp') &&
            i + 1 < rootArguments.length &&
            !rootArguments[i + 1].startsWith('-')) {
            return rootArguments[i + 1];
        }
    }

    return undefined;
}
