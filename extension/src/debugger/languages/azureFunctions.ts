import * as vscode from 'vscode';
import * as path from 'path';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isAzureFunctionsLaunchConfiguration, AzureFunctionsLaunchConfiguration } from '../../dcp/types';
import { invalidLaunchConfiguration } from '../../loc/strings';
import { extensionLogOutputChannel } from '../../utils/logging';
import { ResourceDebuggerExtension } from '../debuggerExtensions';
import { registerRunCleanup } from '../runCleanupRegistry';

const AF_EXTENSION_ID = 'ms-azuretools.vscode-azurefunctions';

/**
 * Result from the Azure Functions extension's startFuncProcess API.
 * processId is a string — it's the PID of the dotnet worker process
 * (found via pickChildProcess which searches for a child matching /(dotnet|func)/).
 */
interface StartFuncProcessResult {
    processId: string;
    success: boolean;
    error?: string;
}

/**
 * The Azure Functions extension API (v1.10.0).
 * Obtained via the @microsoft/vscode-azext-utils API provider pattern:
 *   ext.exports.getApi('~1.10.0') → AzureFunctionsApi
 */
interface AzureFunctionsApi {
    apiVersion: string;
    startFuncProcess(buildPath: string, args: string[], env: Record<string, string>): Promise<StartFuncProcessResult>;
}

interface AzureFunctionsApiProvider {
    getApi(apiVersion: string): AzureFunctionsApi;
}

/** Tracks worker PIDs by runId for cleanup. */
const workerPidsByRunId = new Map<string, number>();

/** Tracks the VS Code Task executions (func host start) by runId for cleanup. */
const taskExecutionsByRunId = new Map<string, vscode.TaskExecution>();

/** Kill the func host task and worker process for the given runId, if any. */
function killFuncProcess(runId: string): void {
    // Terminate the VS Code Task running "func host start"
    const taskExecution = taskExecutionsByRunId.get(runId);
    if (taskExecution) {
        extensionLogOutputChannel.info(`Terminating func host task for runId ${runId}`);
        taskExecution.terminate();
        taskExecutionsByRunId.delete(runId);
    }

    // Also kill the worker PID directly in case task termination doesn't propagate
    const pid = workerPidsByRunId.get(runId);
    if (pid !== undefined) {
        extensionLogOutputChannel.info(`Killing func worker process for runId ${runId} (pid: ${pid})`);
        try {
            process.kill(pid);
        } catch {
            // Process may already be dead
        }
        workerPidsByRunId.delete(runId);
    }
}

async function getAzureFunctionsApi(): Promise<AzureFunctionsApi> {
    const ext = vscode.extensions.getExtension(AF_EXTENSION_ID);
    if (!ext) {
        throw new Error(`Azure Functions extension (${AF_EXTENSION_ID}) is not installed`);
    }
    if (!ext.isActive) {
        await ext.activate();
    }

    // The AF extension uses the @microsoft/vscode-azext-utils API provider
    // pattern. ext.exports has a getApi(version) method that returns the actual API.
    const provider = ext.exports as AzureFunctionsApiProvider;
    if (typeof provider?.getApi !== 'function') {
        throw new Error('Azure Functions extension does not expose the expected getApi provider');
    }

    return provider.getApi('~1.10.0');
}

export const azureFunctionsDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'azure-functions',
    debugAdapter: 'coreclr',
    extensionId: 'ms-dotnettools.csharp',
    getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => {
        if (isAzureFunctionsLaunchConfiguration(launchConfig) && launchConfig.project_path) {
            return `Azure Functions: ${path.basename(launchConfig.project_path)}`;
        }
        return 'Azure Functions';
    },
    getSupportedFileTypes: () => ['.cs', '.csproj'],
    getProjectFile: (launchConfig) => {
        if (isAzureFunctionsLaunchConfiguration(launchConfig)) {
            return launchConfig.project_path;
        }
        throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
    },
    createDebugSessionConfigurationCallback: async (launchConfig, args, env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isAzureFunctionsLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not azure-functions for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        // Register cleanup for this run up-front so that killFuncProcess is called
        // via the generic cleanupRun path regardless of how the session ends.
        registerRunCleanup(debugConfiguration.runId, () => killFuncProcess(debugConfiguration.runId));

        const projectPath = launchConfig.project_path;
        // project_path from the C# side is the .csproj file path (resolved by
        // AzureFunctionsProjectMetadata.ResolveProjectPath). The AF extension
        // API expects the project *directory* as buildPath.
        const projectDir = path.dirname(projectPath);

        extensionLogOutputChannel.info(`Starting Azure Functions project via extension API: ${projectPath} (buildPath: ${projectDir})`);

        // Only pass DCP-specific env vars to the AF extension. The VS Code Task
        // it creates already inherits the VS Code process environment, so we
        // don't need to merge process.env — that would just duplicate values.
        const dcpEnv = Object.fromEntries(
            (env ?? []).filter(e => e.value !== undefined).map(e => [e.name, e.value])
        );

        // Start func host via the Azure Functions extension API.
        // The API creates a VS Code Task running "func host start", polls
        // /admin/host/status until ready, then finds the dotnet worker child
        // process and returns its PID. We let func handle the build itself
        // so it outputs to its expected bin/output/ location.
        //
        // The AF extension API has no stopFuncProcess method, so we track the
        // VS Code Task it creates by diffing taskExecutions before/after the call.
        const api = await getAzureFunctionsApi();
        extensionLogOutputChannel.info(`Got Azure Functions API (version ${api.apiVersion}), calling startFuncProcess`);

        const executionsBefore = new Set(vscode.tasks.taskExecutions);
        const result = await api.startFuncProcess(projectDir, args ?? [], dcpEnv);

        // Find the new task execution that was created by startFuncProcess.
        // Filter by task name containing "func" to reduce the chance of capturing
        // an unrelated task started concurrently by another extension or user.
        const newExecutions = [...vscode.tasks.taskExecutions].filter(exec => !executionsBefore.has(exec));
        const funcExecution = newExecutions.find(exec => exec.task.name.toLowerCase().includes('func'));

        if (funcExecution) {
            extensionLogOutputChannel.info(`Captured func host task for runId ${debugConfiguration.runId}: ${funcExecution.task.name}`);
            taskExecutionsByRunId.set(debugConfiguration.runId, funcExecution);
        }
        if (newExecutions.length > 1) {
            extensionLogOutputChannel.warn(`Multiple new task executions detected after startFuncProcess (${newExecutions.length}); captured: ${funcExecution?.task.name}`);
        }

        if (!result.success) {
            throw new Error(`Azure Functions extension failed to start func host: ${result.error ?? 'unknown error'}`);
        }

        const workerPid = result.processId;
        extensionLogOutputChannel.info(`Azure Functions worker process started (PID: ${workerPid}), attaching debugger`);

        // Track the worker PID for cleanup
        const workerPidNumber = parseInt(workerPid, 10);
        workerPidsByRunId.set(debugConfiguration.runId, workerPidNumber);

        // Configure coreclr attach to the worker process
        debugConfiguration.type = 'coreclr';
        debugConfiguration.request = 'attach';
        debugConfiguration.processId = String(workerPidNumber);

        // Remove launch-mode properties that don't apply to attach
        delete debugConfiguration.program;
        delete debugConfiguration.args;
        delete debugConfiguration.cwd;
        delete debugConfiguration.console;
        delete debugConfiguration.env;
    }
};
