import * as path from 'path';
import { AppHostDataRepository, ResourceJson } from './views/AppHostDataRepository';
import { AspireTerminalProvider } from './utils/AspireTerminalProvider';
import { spawnCliProcess } from './debugger/languages/cli';
import { AcquiredTestRunSession, TestRunSessionAcquireOptions } from './dcp/TestRunSessionManager';
import AspireDcpServer from './dcp/AspireDcpServer';

/**
 * Public API exported by the Aspire extension for consumption by other extensions (e.g. C# Dev Kit).
 */
export interface AspireExtensionApi {
	/**
	 * Fetches all currently running AppHosts with their resources.
	 * Performs a fresh CLI call independent of panel/polling state.
	 */
	getRunningAppHosts(): Promise<AppHostInfo[]>

	/**
	 * Stops a running resource by name.
	 * Resolves when the CLI command completes.
	 */
	stopResource(resourceName: string, appHostPath: string): Promise<void>

	/**
	 * Starts a stopped resource by name.
	 * Resolves when the CLI command completes.
	 */
	startResource(resourceName: string, appHostPath: string): Promise<void>

	/**
	 * Acquires DCP connection environment for a test process that starts an AppHost in-process.
	 */
	acquireTestRunSession(options: TestRunSessionAcquireOptions): AcquiredTestRunSession

	/**
	 * Releases a test-run DCP lease and stops resources started through it.
	 */
	releaseTestRunSession(id: string): Promise<void>
}

export interface AppHostInfo {
	/** Absolute path to the AppHost project or source file. */
	appHostPath: string
	/** Process ID of the running AppHost. */
	pid: number
	/** Dashboard URL, if available. */
	dashboardUrl: string | null
	/** Resources managed by this AppHost. */
	resources: ResourceInfo[]
}

export interface ResourceInfo {
	/** Internal resource name as defined in the AppHost. */
	name: string
	/** Display name for the resource. */
	displayName: string | null
	/** Resource type (e.g. "Project", "Container"). */
	resourceType: string
	/** Current state (e.g. "Running", "Stopped", "Building"). */
	state: string | null
	/** Project path (.csproj) if this is a project resource, extracted from properties["project.path"]. */
	projectPath: string | null
	/** Endpoint URLs exposed by the resource. */
	endpoints: { name: string | null; url: string }[]
	/** Absolute path to the AppHost that owns this resource. */
	appHostPath: string
}

/**
 * Creates the public API object backed by the given data repository.
 */
export function createAspireExtensionApi(dataRepository: AppHostDataRepository, terminalProvider: AspireTerminalProvider, dcpServer: {
	acquireTestRunSession: (options: TestRunSessionAcquireOptions) => AcquiredTestRunSession,
	releaseTestRunSession: (id: string) => Promise<void>
}): AspireExtensionApi {
	return {
		async getRunningAppHosts(): Promise<AppHostInfo[]> {
			const appHosts = await dataRepository.fetchAppHostsOnce();
			return appHosts.map(appHost => ({
				appHostPath: appHost.appHostPath,
				pid: appHost.appHostPid,
				dashboardUrl: appHost.dashboardUrl,
				resources: (appHost.resources ?? []).map(r => mapResource(r, appHost.appHostPath)),
			}));
		},

		async stopResource(resourceName: string, appHostPath: string): Promise<void> {
			return executeResourceCommand(terminalProvider, resourceName, appHostPath, 'stop');
		},

		async startResource(resourceName: string, appHostPath: string): Promise<void> {
			return executeResourceCommand(terminalProvider, resourceName, appHostPath, 'start');
		},

		acquireTestRunSession: dcpServer.acquireTestRunSession,
		releaseTestRunSession: dcpServer.releaseTestRunSession,
	};
}

function mapResource(resource: ResourceJson, appHostPath: string): ResourceInfo {
	return {
		name: resource.name,
		displayName: resource.displayName,
		resourceType: resource.resourceType,
		state: resource.state,
		projectPath: resource.properties?.['project.path'] ?? null,
		endpoints: (resource.urls ?? [])
			.filter(u => !u.isInternal)
			.map(u => ({ name: u.name, url: u.url })),
		appHostPath,
	};
}

async function executeResourceCommand(terminalProvider: AspireTerminalProvider, resourceName: string, appHostPath: string, commandName: 'start' | 'stop'): Promise<void> {
	const trimmedAppHostPath = appHostPath.trim();
	if (!trimmedAppHostPath || !path.isAbsolute(trimmedAppHostPath)) {
		throw new Error('appHostPath must be a non-empty absolute path');
	}

	const cliPath = await terminalProvider.getAspireCliExecutablePath();
	const args = ['resource', resourceName, commandName, '--apphost', trimmedAppHostPath];
	return new Promise<void>((resolve, reject) => {
		let stdout = '';
		let stderr = '';
		let settled = false;

		spawnCliProcess(terminalProvider, cliPath, args, {
			noExtensionVariables: true,
			stdoutCallback: (data) => { stdout += data; },
			stderrCallback: (data) => { stderr += data; },
			exitCallback: (code) => {
				if (settled) {
					return;
				}

				settled = true;
				if (code === 0) {
					resolve();
				} else {
					const output = (stderr || stdout).trim();
					reject(new Error(`aspire resource ${commandName} exited with code ${code}${output ? `: ${output}` : ''}`));
				}
			},
			errorCallback: (error) => {
				if (settled) {
					return;
				}

				settled = true;
				reject(error);
			},
		});
	});
}
