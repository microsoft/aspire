import * as sinon from 'sinon';
import * as vscode from 'vscode';

export async function getAndActivateExtension() {
	const extension = vscode.extensions.getExtension('aspire-vscode') || vscode.extensions.all.find(e => e.id.endsWith('aspire-vscode'));
	if (!extension) {
		throw new Error('Extension not found');
	}

	await extension.activate();
	return extension;
}

/**
 * An in-memory `vscode.Memento` for tests that exercise globalState-backed suppression flags.
 *
 * Mirrors VS Code's semantics closely enough for that purpose: `update` with `undefined` removes the
 * key, and `get` falls back to the supplied default only when the key is absent.
 */
export function createTestMemento(): vscode.Memento {
	const values = new Map<string, unknown>();

	return {
		keys: () => [...values.keys()],
		get: <T>(key: string, defaultValue?: T) => values.has(key) ? values.get(key) as T : defaultValue as T,
		update: async (key: string, value: unknown) => {
			if (value === undefined) {
				values.delete(key);
				return;
			}

			values.set(key, value);
		},
	} as vscode.Memento;
}

/**
 * Creates the C# Dev Kit configuration shape used by Hot Reload tests.
 *
 * The setting contribution is extension-host state, not user configuration. Model it explicitly so
 * tests behave the same whether C# Dev Kit happens to be installed in the host or not. Supplied
 * `get` and `update` implementations are retained so each test still controls setting values and
 * writes independently.
 */
export function createHotReloadTestConfiguration(
	configuration: Partial<vscode.WorkspaceConfiguration> = {},
	options: { contributed?: boolean; defaultValue?: boolean } = {}
): vscode.WorkspaceConfiguration {
	return {
		...configuration,
		inspect: <T>(section: string) => {
			if (section !== 'hotReload' || options.contributed === false) {
				return undefined;
			}

			return {
				key: 'csharp.experimental.debug.hotReload',
				defaultValue: (options.defaultValue ?? false) as T
			};
		}
	} as vscode.WorkspaceConfiguration;
}

/**
 * Stands in for the .NET service the project debugger uses, so tests can drive the real project
 * launch path without invoking the dotnet CLI or building anything.
 *
 * Shared rather than duplicated per test file: both the .NET debugger tests and the Hot Reload
 * regression tests need to launch a project resource, and they must agree on what a successful
 * build and target-path lookup look like.
 */
export class TestDotNetService {
	private _getDotNetTargetPathStub: sinon.SinonStub;
	private _hasDevKit: boolean;

	public buildDotNetProjectStub: sinon.SinonStub;

	// `dotnet run-api` output returned for file-based (.cs) apps. Tests override this with a serialized
	// RunCommand payload; the default empty string mirrors the not-configured case.
	public runApiOutput: string = '';
	public runApiEnvironment: NodeJS.ProcessEnv | undefined;

	constructor(outputPath: string, rejectBuild: Error | null, hasDevKit: boolean) {
		this._getDotNetTargetPathStub = sinon.stub();
		this._getDotNetTargetPathStub.resolves(outputPath);

		this.buildDotNetProjectStub = sinon.stub();
		if (rejectBuild) {
			this.buildDotNetProjectStub.rejects(rejectBuild);
		} else {
			this.buildDotNetProjectStub.resolves();
		}

		this._hasDevKit = hasDevKit;
	}

	getDotNetTargetPath(projectFile: string): Promise<string> {
		return this._getDotNetTargetPathStub(projectFile);
	}

	buildDotNetProject(projectFile: string): Promise<void> {
		return this.buildDotNetProjectStub(projectFile);
	}

	getAndActivateDevKit(): Promise<boolean> {
		return Promise.resolve(this._hasDevKit);
	}

	getDotNetRunApiOutput(projectPath: string, environment?: NodeJS.ProcessEnv): Promise<string> {
		this.runApiEnvironment = environment;
		return Promise.resolve(this.runApiOutput);
	}
}
