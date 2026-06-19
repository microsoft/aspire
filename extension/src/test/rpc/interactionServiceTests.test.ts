import * as assert from 'assert';
import * as fs from 'fs/promises';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import * as sinon from 'sinon';

import { IInteractionService, InteractionService } from '../../server/interactionService';
import { ICliRpcClient, RpcClient, ValidationResult } from '../../server/rpcClient';
import { extensionLogOutputChannel } from '../../utils/logging';
import AspireRpcServer, { RpcServerConnectionInfo } from '../../server/AspireRpcServer';
import { AspireDebugSession } from '../../debugger/AspireDebugSession';

suite('InteractionService endpoints', () => {
	let statusBarItem: vscode.StatusBarItem;
	let createStatusBarItemStub: sinon.SinonStub;

	setup(() => {
		statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left);
		createStatusBarItemStub = sinon.stub(vscode.window, 'createStatusBarItem').returns(statusBarItem);
	});

	teardown(() => {
		createStatusBarItemStub.restore();
		statusBarItem.dispose();
	});

	// promptForString
	test('promptForString calls validateInput and returns valid result', async () => {
		const testInfo = await createTestRpcServer();
		let validateInputCalled = false;
		const showInputBoxStub = sinon.stub(vscode.window, 'showInputBox').callsFake(async (options: any) => {
			if (options && typeof options.validateInput === 'function') {
				validateInputCalled = true;
				// Simulate valid input
				const validationResult = await options.validateInput('valid');
				assert.strictEqual(validationResult, null, 'Should return null for valid input');
			}
			return 'valid';
		});
		const rpcClient = testInfo.rpcClient;
		const result = await testInfo.interactionService.promptForString('Enter valid input:', null, false, rpcClient);
		assert.strictEqual(result, 'valid');
		assert.ok(validateInputCalled, 'validateInput should be called');
		showInputBoxStub.restore();
	});

	test('promptForString calls validateInput and returns invalid result', async () => {
		const testInfo = await createTestRpcServer();
		let validateInputCalled = false;
		const showInputBoxStub = sinon.stub(vscode.window, 'showInputBox').callsFake(async (options: any) => {
			if (options && typeof options.validateInput === 'function') {
				validateInputCalled = true;
				// Simulate invalid input
				const validationResult = await options.validateInput('invalid');
				assert.strictEqual(typeof validationResult, 'string', 'Should return error message for invalid input');
			}
			return 'invalid';
		});
		const rpcClient = testInfo.rpcClient;
		const result = await testInfo.interactionService.promptForString('Enter valid input:', null, false, rpcClient);
		assert.strictEqual(result, 'invalid');
		assert.ok(validateInputCalled, 'validateInput should be called');
		showInputBoxStub.restore();
	});

	test('promptForString returns empty string when user provides empty value', async () => {
		const testInfo = await createTestRpcServer();
		const showInputBoxStub = sinon.stub(vscode.window, 'showInputBox').resolves('');
		const rpcClient = testInfo.rpcClient;
		const result = await testInfo.interactionService.promptForString('Enter value:', null, false, rpcClient);
		assert.strictEqual(result, '', 'Should return empty string, not null');
		showInputBoxStub.restore();
	});

	test('promptForString returns null when user cancels', async () => {
		const testInfo = await createTestRpcServer();
		const showInputBoxStub = sinon.stub(vscode.window, 'showInputBox').resolves(undefined);
		const rpcClient = testInfo.rpcClient;
		const result = await testInfo.interactionService.promptForString('Enter value:', null, false, rpcClient);
		assert.strictEqual(result, null, 'Should return null when cancelled');
		showInputBoxStub.restore();
	});

	// promptForSecretString
	test('promptForSecretString sets password option to true', async () => {
		const testInfo = await createTestRpcServer();
		let passwordOptionSet = false;
		const showInputBoxStub = sinon.stub(vscode.window, 'showInputBox').callsFake(async (options: any) => {
			if (options && options.password === true) {
				passwordOptionSet = true;
			}
			return 'secret-value';
		});
		const rpcClient = testInfo.rpcClient;
		const result = await testInfo.interactionService.promptForSecretString('Enter password:', true, rpcClient);
		assert.strictEqual(result, 'secret-value');
		assert.ok(passwordOptionSet, 'password option should be set to true for secret prompts');
		showInputBoxStub.restore();
	});

	test('promptForSecretString returns empty string when user provides empty value', async () => {
		const testInfo = await createTestRpcServer();
		const showInputBoxStub = sinon.stub(vscode.window, 'showInputBox').resolves('');
		const rpcClient = testInfo.rpcClient;
		const result = await testInfo.interactionService.promptForSecretString('Enter password:', false, rpcClient);
		assert.strictEqual(result, '', 'Should return empty string, not null');
		showInputBoxStub.restore();
	});

	test('promptForSecretString returns null when user cancels', async () => {
		const testInfo = await createTestRpcServer();
		const showInputBoxStub = sinon.stub(vscode.window, 'showInputBox').resolves(undefined);
		const rpcClient = testInfo.rpcClient;
		const result = await testInfo.interactionService.promptForSecretString('Enter password:', false, rpcClient);
		assert.strictEqual(result, null, 'Should return null when cancelled');
		showInputBoxStub.restore();
	});

	// confirm
	test('confirm returns true when Yes is selected', async () => {
		const testInfo = await createTestRpcServer();
		const showQuickPickStub = sinon.stub(vscode.window, 'showQuickPick').resolves('Yes' as any);
		const result = await testInfo.interactionService.confirm('Are you sure?', true);
		assert.strictEqual(result, true);
		assert.ok(showQuickPickStub.calledOnce, 'showQuickPick should be called once');

		// Verify options passed to showQuickPick
		const callArgs = showQuickPickStub.getCall(0).args;
		assert.deepStrictEqual(callArgs[0], ['Yes', 'No'], 'should show Yes and No choices');
		assert.strictEqual(callArgs[1]?.canPickMany, false, 'canPickMany should be false');
		assert.strictEqual(callArgs[1]?.ignoreFocusOut, true, 'ignoreFocusOut should be true');

		showQuickPickStub.restore();
	});

	test('confirm returns false when No is selected', async () => {
		const testInfo = await createTestRpcServer();
		const showQuickPickStub = sinon.stub(vscode.window, 'showQuickPick').resolves('No' as any);
		const result = await testInfo.interactionService.confirm('Are you sure?', false);
		assert.strictEqual(result, false);
		assert.ok(showQuickPickStub.calledOnce, 'showQuickPick should be called once');
		showQuickPickStub.restore();
	});

	test('confirm returns null when cancelled', async () => {
		const testInfo = await createTestRpcServer();
		const showQuickPickStub = sinon.stub(vscode.window, 'showQuickPick').resolves(undefined);
		const result = await testInfo.interactionService.confirm('Are you sure?', true);
		assert.strictEqual(result, null);
		assert.ok(showQuickPickStub.calledOnce, 'showQuickPick should be called once');
		showQuickPickStub.restore();
	});

	test('displayError endpoint', async () => {
		const testInfo = await createTestRpcServer();
		const showErrorMessageSpy = sinon.spy(vscode.window, 'showErrorMessage');
		testInfo.interactionService.displayError('Test error message');
		assert.ok(showErrorMessageSpy.calledWith('Test error message'));
		showErrorMessageSpy.restore();
	});

	test('displayMessage endpoint', async () => {
		const testInfo = await createTestRpcServer();
		const showInformationMessageSpy = sinon.spy(vscode.window, 'showInformationMessage');
		testInfo.interactionService.displayMessage(":test_emoji:", 'Test info message');
		assert.ok(showInformationMessageSpy.calledWith('Test info message'));
		showInformationMessageSpy.restore();
	});

	test("displaySuccess endpoint", async () => {
		const testInfo = await createTestRpcServer();
		const showInformationMessageSpy = sinon.spy(vscode.window, 'showInformationMessage');
		testInfo.interactionService.displaySuccess('Test success message');
		assert.ok(showInformationMessageSpy.calledWith('Test success message'));
		showInformationMessageSpy.restore();
	});

	test("displaySuccess clears active progress notification", async () => {
		const testInfo = await createTestRpcServer();
		const showInformationMessageStub = sinon.stub(vscode.window, 'showInformationMessage').resolves();

		try {
			testInfo.interactionService.showStatus('Executing test command...');
			assert.strictEqual((testInfo.interactionService as any)._progressNotifier.isActive, true);

			testInfo.interactionService.displaySuccess('Test success message');

			assert.strictEqual((testInfo.interactionService as any)._progressNotifier.isActive, false);
		}
		finally {
			showInformationMessageStub.restore();
		}
	});

	test("showStatus ignores E2E delay environment unless the E2E bridge is enabled", async () => {
		const originalEnableBridge = process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE;
		const originalStateFile = process.env.ASPIRE_EXTENSION_E2E_STATE_FILE;
		const originalControlFile = process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE;
		const originalShowStatusDelayMs = process.env.ASPIRE_EXTENSION_E2E_SHOW_STATUS_DELAY_MS;
		const testInfo = await createTestRpcServer();
		const waitStub = sinon.stub(Atomics, 'wait').returns('timed-out');

		try {
			delete process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE;
			delete process.env.ASPIRE_EXTENSION_E2E_STATE_FILE;
			delete process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE;
			process.env.ASPIRE_EXTENSION_E2E_SHOW_STATUS_DELAY_MS = '10000';

			testInfo.interactionService.showStatus('Executing test command...');

			assert.strictEqual(waitStub.called, false);
		}
		finally {
			restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE', originalEnableBridge);
			restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_STATE_FILE', originalStateFile);
			restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_CONTROL_FILE', originalControlFile);
			restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_SHOW_STATUS_DELAY_MS', originalShowStatusDelayMs);
			waitStub.restore();
		}
	});

	test("RPC close clears active progress notification", async () => {
		let closeHandler: (() => void) | undefined;
		const messageConnection = {
			onClose: (handler: () => void) => {
				closeHandler = handler;
				return { dispose: () => { } };
			},
			sendRequest: sinon.stub()
		} as any;

		const rpcClient = new RpcClient({} as any, messageConnection, null, () => null);

		rpcClient.interactionService.showStatus('Scanning for running AppHosts...');
		assert.strictEqual((rpcClient.interactionService as any)._progressNotifier.isActive, true);

		closeHandler!();

		assert.strictEqual((rpcClient.interactionService as any)._progressNotifier.isActive, false);
	});

	test("displaySubtleMessage endpoint", async () => {
		const testInfo = await createTestRpcServer();
		const setStatusBarMessageSpy = sinon.spy(vscode.window, 'setStatusBarMessage');
		testInfo.interactionService.displaySubtleMessage('Test subtle message');
		assert.ok(setStatusBarMessageSpy.calledWith('Test subtle message'));
		setStatusBarMessageSpy.restore();
	});

	test("displayEmptyLine endpoint", async () => {
		const stub = sinon.stub(extensionLogOutputChannel, 'append');
		const testInfo = await createTestRpcServer();
		testInfo.interactionService.displayEmptyLine();
		assert.ok(stub.calledWith('\n'));
		stub.restore();
	});

	test("openEditor adds folder to existing workspace", async () => {
		const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'aspire-open-editor-'));
		const workspacePath = path.join(tempRoot, 'workspace');
		const projectPath = path.join(tempRoot, 'MyFirstApp');
		await fs.mkdir(workspacePath);
		await fs.mkdir(projectPath);

		const sandbox = sinon.createSandbox();

		try {
			const testInfo = await createTestRpcServer();
			const workspaceFolder = {
				uri: vscode.Uri.file(workspacePath),
				name: 'workspace',
				index: 0
			} as vscode.WorkspaceFolder;

			sandbox.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
			sandbox.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) =>
				uri.fsPath === workspaceFolder.uri.fsPath ? workspaceFolder : undefined);
			const updateWorkspaceFoldersStub = sandbox.stub(vscode.workspace, 'updateWorkspaceFolders').returns(true);
			const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves();

			await testInfo.interactionService.openEditor(projectPath);

			assert.strictEqual(updateWorkspaceFoldersStub.callCount, 1, 'Should update workspace folders once');
			const updateArgs = updateWorkspaceFoldersStub.getCall(0).args;
			assert.strictEqual(updateArgs[0], 1, 'Should add after existing workspace folders');
			assert.strictEqual(updateArgs[1], 0, 'Should not remove existing workspace folders');
			assertPathEqual(updateArgs[2].uri.fsPath, projectPath, 'Should add the new project folder');
			assert.strictEqual(executeCommandStub.callCount, 0, 'Should not replace the workspace with vscode.openFolder');
		}
		finally {
			sandbox.restore();
			await fs.rm(tempRoot, { recursive: true, force: true });
		}
	});

	test("openEditor opens folder when there is no workspace", async () => {
		const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'aspire-open-editor-'));
		const projectPath = path.join(tempRoot, 'MyFirstApp');
		await fs.mkdir(projectPath);

		const sandbox = sinon.createSandbox();

		try {
			const testInfo = await createTestRpcServer();

			sandbox.stub(vscode.workspace, 'workspaceFolders').value(undefined);
			sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
			const updateWorkspaceFoldersStub = sandbox.stub(vscode.workspace, 'updateWorkspaceFolders').returns(true);
			const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves();

			await testInfo.interactionService.openEditor(projectPath);

			assert.strictEqual(updateWorkspaceFoldersStub.callCount, 0, 'Should not update workspace folders when no workspace exists');
			assert.strictEqual(executeCommandStub.callCount, 1, 'Should open the new project folder');
			const executeArgs = executeCommandStub.getCall(0).args;
			assert.strictEqual(executeArgs[0], 'vscode.openFolder');
			assertPathEqual(executeArgs[1].fsPath, projectPath);
			assert.deepStrictEqual(executeArgs[2], { forceNewWindow: false });
		}
		finally {
			sandbox.restore();
			await fs.rm(tempRoot, { recursive: true, force: true });
		}
	});

	test("openEditor shows warning when folder cannot be added to existing workspace", async () => {
		const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'aspire-open-editor-'));
		const workspacePath = path.join(tempRoot, 'workspace');
		const projectPath = path.join(tempRoot, 'MyFirstApp');
		await fs.mkdir(workspacePath);
		await fs.mkdir(projectPath);

		const sandbox = sinon.createSandbox();

		try {
			const testInfo = await createTestRpcServer();
			const workspaceFolder = {
				uri: vscode.Uri.file(workspacePath),
				name: 'workspace',
				index: 0
			} as vscode.WorkspaceFolder;

			sandbox.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
			sandbox.stub(vscode.workspace, 'getWorkspaceFolder').callsFake((uri: vscode.Uri) =>
				uri.fsPath === workspaceFolder.uri.fsPath ? workspaceFolder : undefined);
			sandbox.stub(vscode.workspace, 'updateWorkspaceFolders').returns(false);
			const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves();
			const showWarningMessageStub = sandbox.stub(vscode.window, 'showWarningMessage').resolves(undefined);

			await testInfo.interactionService.openEditor(projectPath);

			assert.strictEqual(executeCommandStub.callCount, 0, 'Should not replace the workspace with vscode.openFolder');
			assert.strictEqual(showWarningMessageStub.callCount, 1, 'Should warn when the project folder was not added');
			assert.ok(showWarningMessageStub.getCall(0).args[0].includes(projectPath), 'Warning should include the project folder path');
		}
		finally {
			sandbox.restore();
			await fs.rm(tempRoot, { recursive: true, force: true });
		}
	});

	test("displayDashboardUrls writes URLs to output channel and shows info message when autoLaunch is notification", async () => {
		const sandbox = sinon.createSandbox();

		try {
			const stub = sandbox.stub(extensionLogOutputChannel, 'info');
			const showInformationMessageStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves();
			sandbox.stub(vscode.workspace, 'getConfiguration').returns({
				get: (key: string, defaultValue?: any) => key === 'enableAspireDashboardAutoLaunch' ? 'notification' : defaultValue
			} as any);
			const testInfo = await createTestRpcServer();

			const baseUrl = 'http://localhost/login?t=base-secret';
			const codespacesUrl = 'http://codespaces/login?t=codespaces-secret';

			await testInfo.interactionService.displayDashboardUrls({
				BaseUrlWithLoginToken: baseUrl,
				CodespacesUrlWithLoginToken: codespacesUrl
			});

			const outputLines = stub.getCalls().map(call => call.args[0]);

			await new Promise(resolve => setTimeout(resolve, 2000));

			assert.ok(outputLines.some(line => line.includes('http://localhost')), 'Output should contain sanitized base URL origin');
			assert.ok(outputLines.some(line => line.includes('http://codespaces')), 'Output should contain sanitized codespaces URL origin');
			assert.ok(outputLines.every(line => !line.includes('base-secret')), 'Output should not contain base URL login token');
			assert.ok(outputLines.every(line => !line.includes('codespaces-secret')), 'Output should not contain codespaces URL login token');
			assert.equal(showInformationMessageStub.callCount, 1, 'Should show info message when autoLaunch is notification');
		}
		finally {
			sandbox.restore();
		}
	});

	test("displayDashboardUrls writes URLs but does not show info message when autoLaunch is launch", async () => {
		const sandbox = sinon.createSandbox();

		try {
			const stub = sandbox.stub(extensionLogOutputChannel, 'info');
			const showInformationMessageStub = sandbox.stub(vscode.window, 'showInformationMessage').resolves();
			sandbox.stub(vscode.workspace, 'getConfiguration').returns({
				get: (key: string, defaultValue?: any) => key === 'enableAspireDashboardAutoLaunch' ? 'launch' : defaultValue
			} as any);
			const testInfo = await createTestRpcServer();

			const baseUrl = 'http://localhost/login?t=base-secret';
			const codespacesUrl = 'http://codespaces/login?t=codespaces-secret';

			await testInfo.interactionService.displayDashboardUrls({
				BaseUrlWithLoginToken: baseUrl,
				CodespacesUrlWithLoginToken: codespacesUrl
			});

			const outputLines = stub.getCalls().map(call => call.args[0]);

			assert.ok(outputLines.some(line => line.includes('http://localhost')), 'Output should contain sanitized base URL origin');
			assert.ok(outputLines.some(line => line.includes('http://codespaces')), 'Output should contain sanitized codespaces URL origin');
			assert.ok(outputLines.every(line => !line.includes('base-secret')), 'Output should not contain base URL login token');
			assert.ok(outputLines.every(line => !line.includes('codespaces-secret')), 'Output should not contain codespaces URL login token');
			assert.equal(showInformationMessageStub.callCount, 0, 'Should not show info message when autoLaunch is launch');
		}
		finally {
			sandbox.restore();
		}
	});

	test("displayLines endpoint", async () => {
		const sandbox = sinon.createSandbox();

		try {
			sandbox.stub(extensionLogOutputChannel, 'info');
			const sentMessages: { message: string; category: string }[] = [];
			const mockDebugSession = {
				sendMessage: (message: string, addNewLine: boolean, category: 'stdout' | 'stderr') => {
					sentMessages.push({ message, category });
				}
			} as unknown as AspireDebugSession;
			const testInfo = await createTestRpcServer(null, () => mockDebugSession);

			testInfo.interactionService.displayLines([
				{ Stream: 'stdout', Line: 'line1' },
				{ Stream: 'stderr', Line: 'line2' }
			]);

			assert.strictEqual(sentMessages.length, 2, 'Should send two messages to debug session');
			assert.strictEqual(sentMessages[0].message, 'line1');
			assert.strictEqual(sentMessages[0].category, 'stdout');
			assert.strictEqual(sentMessages[1].message, 'line2');
			assert.strictEqual(sentMessages[1].category, 'stderr');
		}
		finally {
			sandbox.restore();
		}
	});

	test("displayLines without debug session falls back to Aspire terminal", async () => {
		const sandbox = sinon.createSandbox();

		try {
			sandbox.stub(extensionLogOutputChannel, 'info');
			const sentTexts: string[] = [];
			const mockTerminal = {
				terminal: {
					sendText: (text: string, addNewLine: boolean) => {
						sentTexts.push(text);
					}
				},
				dispose: () => {}
			};
			const testInfo = await createTestRpcServer(null, () => null);
			// Inject a mock terminal provider via the InteractionService constructor
			(testInfo.interactionService as any)._getAspireTerminal = () => mockTerminal;

			testInfo.interactionService.displayLines([
				{ Stream: 'stdout', Line: 'line1' },
				{ Stream: 'stderr', Line: 'line2' }
			]);

			assert.strictEqual(sentTexts.length, 2, 'Should send two lines to Aspire terminal');
			assert.strictEqual(sentTexts[0], 'line1');
			assert.strictEqual(sentTexts[1], 'line2');
		}
		finally {
			sandbox.restore();
		}
	});
});

type RpcServerTestInfo = {
	rpcServerInfo: RpcServerConnectionInfo;
	rpcClient: ICliRpcClient;
	interactionService: IInteractionService;
};

function assertPathEqual(actual: string, expected: string, message?: string) {
	assert.strictEqual(normalizePathForComparison(actual), normalizePathForComparison(expected), message);
}

function normalizePathForComparison(value: string) {
	const normalized = path.normalize(value);

	return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
}

function restoreEnvironmentVariable(name: string, value: string | undefined): void {
	if (value === undefined) {
		delete process.env[name];
		return;
	}

	process.env[name] = value;
}

class TestCliRpcClient implements ICliRpcClient {
    debugSessionId: string | null;
    interactionService: IInteractionService;

    constructor(debugSessionId: string | null, getAspireDebugSession: () => AspireDebugSession | null) {
        this.debugSessionId = debugSessionId;
        this.interactionService = new InteractionService(getAspireDebugSession, this);
    }

	stopCli(): Promise<void> {
		return Promise.resolve();
	}

	getCliVersion(): Promise<string> {
		return Promise.resolve('1.0.0');
	}

	validatePromptInputString(input: string): Promise<ValidationResult | null> {
		if (input === "valid") {
			return Promise.resolve({ Message: `Valid input: ${input}`, Successful: true });
		}
		else if (input === "invalid") {
			return Promise.resolve({ Message: `Invalid input: ${input}`, Successful: false });
		}
		else {
			return Promise.resolve(null);
		}
	}

	getCliCapabilities(): Promise<string[]> {
		return Promise.resolve(['build-dotnet-using-cli']);
	}
}

async function createTestRpcServer(debugSessionId?: string | null, getAspireDebugSession?: () => AspireDebugSession | null): Promise<RpcServerTestInfo> {
    getAspireDebugSession ??= () => {
        return null;
    };

	const rpcClient = new TestCliRpcClient(debugSessionId ?? null, getAspireDebugSession);

	const rpcServer = await AspireRpcServer.create(() => rpcClient);

	if (!rpcServer) {
		throw new Error('Failed to set up RPC server');
	}

	return {
		rpcServerInfo: rpcServer.connectionInfo,
		rpcClient: rpcClient,
		interactionService: rpcClient.interactionService
	};
}
