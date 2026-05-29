import * as vscode from 'vscode'
import { AppHostDataRepository, ResourceJson } from './views/AppHostDataRepository'
import { AspireTerminalProvider } from './utils/AspireTerminalProvider'
import { spawnCliProcess } from './debugger/languages/cli'

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
	 * Event fired when AppHost or resource state changes.
	 */
	onDidChangeAppHosts: vscode.Event<void>
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
export function createAspireExtensionApi(dataRepository: AppHostDataRepository, terminalProvider: AspireTerminalProvider): AspireExtensionApi {
	return {
		async getRunningAppHosts(): Promise<AppHostInfo[]> {
			const appHosts = await dataRepository.fetchAppHostsOnce()
			return appHosts.map(appHost => ({
				appHostPath: appHost.appHostPath,
				pid: appHost.appHostPid,
				dashboardUrl: appHost.dashboardUrl,
				resources: (appHost.resources ?? []).map(r => mapResource(r, appHost.appHostPath)),
			}))
		},

		async stopResource(resourceName: string, appHostPath: string): Promise<void> {
			const cliPath = await terminalProvider.getAspireCliExecutablePath()
			const args = ['resource', resourceName, 'stop']
			if (appHostPath) {
				args.push('--apphost', appHostPath)
			}
			return new Promise<void>((resolve, reject) => {
				spawnCliProcess(terminalProvider, cliPath, args, {
					exitCallback: (code) => {
						if (code === 0) {
							resolve()
						} else {
							reject(new Error(`aspire resource stop exited with code ${code}`))
						}
					},
					errorCallback: (error) => reject(error),
				})
			})
		},

		async startResource(resourceName: string, appHostPath: string): Promise<void> {
			const cliPath = await terminalProvider.getAspireCliExecutablePath()
			const args = ['resource', resourceName, 'start']
			if (appHostPath) {
				args.push('--apphost', appHostPath)
			}
			return new Promise<void>((resolve, reject) => {
				spawnCliProcess(terminalProvider, cliPath, args, {
					exitCallback: (code) => {
						if (code === 0) {
							resolve()
						} else {
							reject(new Error(`aspire resource start exited with code ${code}`))
						}
					},
					errorCallback: (error) => reject(error),
				})
			})
		},

		onDidChangeAppHosts: dataRepository.onDidChangeData,
	}
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
	}
}
