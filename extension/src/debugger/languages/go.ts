import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isGoLaunchConfiguration } from "../../dcp/types";
import { attachDebuggerConfigurationName, attachDebuggerUnavailable, goDisplayName, goLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { ResourceAttachConfigurationError, type ResourceAttachProvider, type ResourceDebugResourceSnapshot } from '../resourceDebugContracts';
import {
    getProcessCommandProgram,
    launchedChildProcessResolver,
    type LaunchedChildProcess,
    type LaunchedChildProcessIdentity,
} from '../launchedChildProcessDiscovery';

const executablePidPropertyName = 'executable.pid';
const executablePathPropertyName = 'executable.path';
const resourceLaunchConfigurationTypePropertyName = 'resource.launchConfigurationType';
const goBuildExecutablePattern = /(?:^|[\\/])go-build[^\\/\s]*(?:[\\/][^\\/\s]+)*[\\/]exe[\\/][^\\/\s]+(?:\.exe)?$/i;
const cachedGoRunExecutablePattern = /(?:^|[\\/])[0-9a-f]{2}[\\/][0-9a-f]{16,}-d[\\/][^\\/\s]+(?:\.exe)?$/i;

interface GoAttachDebuggerResourceInfo {
    readonly parentPid: number;
    readonly resourceLabel: string;
}

interface GoApplicationProcessResolver {
    resolveApplicationPid(goProcessId: number, cancellationToken?: vscode.CancellationToken): Promise<number>;
}

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    if (isGoLaunchConfiguration(launchConfig)) {
        return launchConfig.program || launchConfig.working_directory || '';
    }

    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

export const goDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'go',
    debugAdapter: 'go',
    extensionId: 'golang.go',
    getDisplayName: (launchConfiguration: ExecutableLaunchConfiguration) => {
        if (isGoLaunchConfiguration(launchConfiguration)) {
            const displayPath = launchConfiguration.program || launchConfiguration.working_directory || '';
            return displayPath ? goDisplayName(vscode.workspace.asRelativePath(displayPath)) : goLabel;
        }

        return goLabel;
    },
    getSupportedFileTypes: () => ['.go'],
    getProjectFile: (launchConfig) => getProjectFile(launchConfig),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isGoLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not go for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        debugConfiguration.type = 'go';
        debugConfiguration.request = 'launch';
        debugConfiguration.mode = 'debug';
        debugConfiguration.debugAdapter = 'dlv-dap';
        debugConfiguration.noDebug = !launchOptions.debug;

        const program = launchConfig.program || launchConfig.working_directory;
        if (program) {
            debugConfiguration.program = program;
        }

        if (launchConfig.working_directory) {
            debugConfiguration.cwd = launchConfig.working_directory;
        }

        if (launchConfig.build_flags) {
            debugConfiguration.buildFlags = launchConfig.build_flags;
        }

        debugConfiguration.args = args ?? [];
    }
};

export function createGoResourceAttachProvider(processResolver: GoApplicationProcessResolver): ResourceAttachProvider {
    return {
        id: 'go',
        requiredDebuggerExtensions: [{
            id: 'golang.go',
            label: goLabel,
        }],
        canRecognizeResource: resource => canRecognizeGoAttachDebuggerResource(resource),
        canAttachToResource: resource => getGoAttachDebuggerResourceInfo(resource) !== undefined,
        createDebugConfiguration: async (resource, cancellationToken) =>
            await createGoAttachDebugConfiguration(resource, processResolver, cancellationToken),
    };
}

export const goResourceAttachProvider: ResourceAttachProvider =
    createGoResourceAttachProvider({
        resolveApplicationPid: async (goProcessId, cancellationToken) =>
            await launchedChildProcessResolver.resolveProcessId(
                goProcessId,
                createGoRunProcessIdentity(),
                cancellationToken),
    });

export function createGoRunProcessIdentity(): LaunchedChildProcessIdentity {
    return {
        isLauncher: process => isGoToolProcess(process),
        isCandidate: process => isGoBuildApplication(process),
    };
}

function canRecognizeGoAttachDebuggerResource(resource: ResourceDebugResourceSnapshot): boolean {
    return getLaunchConfigurationType(resource) === 'go' && isGoExecutable(resource);
}

function getGoAttachDebuggerResourceInfo(resource: ResourceDebugResourceSnapshot): GoAttachDebuggerResourceInfo | undefined {
    if (resource.state !== 'Running' || !canRecognizeGoAttachDebuggerResource(resource)) {
        return undefined;
    }

    const parentPid = getProcessId(resource);
    if (parentPid === undefined) {
        return undefined;
    }

    return {
        parentPid,
        resourceLabel: resource.displayName ?? resource.name,
    };
}

async function createGoAttachDebugConfiguration(
    resource: ResourceDebugResourceSnapshot,
    processResolver: GoApplicationProcessResolver,
    cancellationToken?: vscode.CancellationToken,
): Promise<vscode.DebugConfiguration> {
    const attachInfo = getGoAttachDebuggerResourceInfo(resource);
    if (!attachInfo) {
        throw new ResourceAttachConfigurationError('resourceNotAttachable', attachDebuggerUnavailable);
    }

    let applicationPid: number;
    try {
        applicationPid = await processResolver.resolveApplicationPid(attachInfo.parentPid, cancellationToken);
    }
    catch (error) {
        if (error instanceof vscode.CancellationError || cancellationToken?.isCancellationRequested) {
            throw new vscode.CancellationError();
        }

        throw new ResourceAttachConfigurationError('resourceNotAttachable', attachDebuggerUnavailable);
    }

    if (!Number.isInteger(applicationPid) || applicationPid <= 0) {
        throw new ResourceAttachConfigurationError('resourceNotAttachable', attachDebuggerUnavailable);
    }

    return {
        type: 'go',
        request: 'attach',
        mode: 'local',
        debugAdapter: 'dlv-dap',
        name: attachDebuggerConfigurationName(attachInfo.resourceLabel),
        processId: applicationPid,
    };
}

function getLaunchConfigurationType(resource: ResourceDebugResourceSnapshot): string | undefined {
    const value = resource.properties?.[resourceLaunchConfigurationTypePropertyName];
    return typeof value === 'string' ? value : undefined;
}

function isGoExecutable(resource: ResourceDebugResourceSnapshot): boolean {
    const executablePath = resource.properties?.[executablePathPropertyName];
    if (typeof executablePath !== 'string') {
        return false;
    }

    const executableName = executablePath.split(/[\\/]/).pop()?.toLowerCase();
    return executableName === 'go' || executableName === 'go.exe';
}

function getProcessId(resource: ResourceDebugResourceSnapshot): number | undefined {
    const value = resource.properties?.[executablePidPropertyName];
    if (typeof value === 'number' && Number.isInteger(value) && value > 0) {
        return value;
    }

    if (typeof value !== 'string') {
        return undefined;
    }

    const processId = Number(value);
    return Number.isInteger(processId) && processId > 0 ? processId : undefined;
}

function isGoBuildApplication(process: LaunchedChildProcess): boolean {
    return isGoRunApplicationPath(process.executable) ||
        isGoRunApplicationPath(getProcessCommandProgram(process.command));
}

function isGoToolProcess(process: LaunchedChildProcess): boolean {
    const programs = [getProcessCommandProgram(process.command), process.executable];
    return programs.some(program => {
        const executableName = program?.split(/[\\/]/).pop()?.toLowerCase();
        return executableName === 'go' || executableName === 'go.exe';
    }) ||
        /(?:^|[\\/\s])go(?:\.exe)?\s+run(?:\s|$)/i.test(process.command);
}

function isGoRunApplicationPath(path: string | undefined): boolean {
    return path !== undefined &&
        (goBuildExecutablePattern.test(path) || cachedGoRunExecutablePattern.test(path));
}
