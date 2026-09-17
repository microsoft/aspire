import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';

import { AspireExtensionContext } from '../AspireExtensionContext';
import { registerTreeViewCommands } from '../activation/registerTreeViewCommands';
import { AppHostDataRepository, ViewMode } from '../data/AppHostDataRepository';
import { AppHostLaunchService } from '../services/AppHostLaunchService';
import { executeE2eControlCommand } from '../testing/e2eStateFileBridge';
import { pipelineInteractionCapability } from '../types/configInfo';
import { AspireExtensionE2EControlCommand } from '../types/extensionApi';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliPathModule from '../utils/cliPath';
import * as configInfoProvider from '../utils/configInfoProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import * as workspaceModule from '../utils/workspace';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';

import { createWorkspaceFolder } from './testHelpers';

function createLaunchService(): AppHostLaunchService {
    return new AppHostLaunchService({
        getCapabilityStatus: async () => 'supported',
    });
}

suite('E2E state file bridge', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => {
        sandbox.restore();
    });

    test('routes every AppHost action through the exact secondary tree element', async () => {
        const primaryPath = '/repo/primary/AppHost/AppHost.csproj';
        const secondaryPath = '/repo/secondary/AppHost/AppHost.csproj';
        const secondaryFolder = createWorkspaceFolder('secondary', '/repo/secondary');
        const secondaryTarget = workspaceFolderCliPathTarget(secondaryFolder);
        const cliPath = '/repo/secondary/tools/aspire';
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').callsFake(uri =>
            uri.path.startsWith(`${secondaryFolder.uri.path}/`) ? secondaryFolder : undefined);
        const repository = createRepository([primaryPath, secondaryPath], primaryPath);
        const terminalProvider = {
            resolveAspireCliPath: sandbox.stub().resolves({
                cliPath,
                available: true,
                source: 'configured',
            }),
        } as unknown as AspireTerminalProvider;
        const launchService = createLaunchService();
        const launchStub = sandbox.stub(launchService, 'launch').resolves();
        // The tree resolves the CLI itself through the canonical resolver, so pin it here rather
        // than letting a CLI installed on the test machine decide what the actions forward.
        sandbox.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath, available: true, source: 'configured' });
        sandbox.stub(workspaceModule, 'checkCliAvailableOrRedirect').callsFake(
            async (_operation, _target, options) => ({
                cliPath: options?.pinnedCliPath ?? cliPath,
                available: true,
            }));
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getCapabilityStatus').resolves('supported');
        sandbox.stub(configInfoProvider.ConfigInfoProvider.prototype, 'getConfigInfo').resolves({
            localSettingsPath: '/repo/secondary/aspire.config.json',
            globalSettingsPath: '/repo/global-aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [
                pipelineInteractionCapability,
            ],
        });
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, launchService);
        const registeredCommands = captureRegisteredTreeCommands(sandbox, provider, repository);
        sandbox.stub(vscode.commands, 'executeCommand').callsFake(async (commandId: string, ...args: unknown[]) => {
            const command = registeredCommands.get(commandId);
            if (!command) {
                throw new Error(`Command '${commandId}' was not registered.`);
            }

            return await command(...args);
        });
        const markStarted = sandbox.spy();

        const commands: AspireExtensionE2EControlCommand[] = [
            { name: 'deployAppHostAction', appHostPath: secondaryPath },
            { name: 'publishAppHostAction', appHostPath: secondaryPath },
            { name: 'runPipelineStepAppHostAction', appHostPath: secondaryPath },
            { name: 'debugPipelineStepAppHostAction', appHostPath: secondaryPath },
        ];
        for (const command of commands) {
            await dispatchControlCommand(command, repository, launchService, provider, terminalProvider, markStarted);
        }

        assert.deepStrictEqual(launchStub.getCalls().map(call => call.args), [
            [secondaryPath, 'deploy', false, undefined, secondaryTarget, cliPath],
            [secondaryPath, 'publish', false, undefined, secondaryTarget, cliPath],
            [secondaryPath, 'do', true, undefined, secondaryTarget, cliPath],
            [secondaryPath, 'do', false, undefined, secondaryTarget, cliPath],
        ]);
        assert.strictEqual(markStarted.callCount, 4);
        provider.dispose();
    });

    test('fails explicitly when the requested AppHost tree element cannot be found', async () => {
        const requestedPath = '/repo/missing/AppHost/AppHost.csproj';
        const repository = createRepository(['/repo/primary/AppHost/AppHost.csproj']);
        const terminalProvider = {} as AspireTerminalProvider;
        const launchService = createLaunchService();
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, launchService);
        const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);

        await assert.rejects(
            dispatchControlCommand(
                { name: 'deployAppHostAction', appHostPath: requestedPath },
                repository,
                launchService,
                provider,
                terminalProvider),
            error => error instanceof Error
                && error.message.includes('deployAppHostAction')
                && error.message.includes(requestedPath));

        assert.strictEqual(executeCommandStub.called, false);
        provider.dispose();
    });

    test('preserves existing command and legacy publish dispatch behavior', async () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        const repository = createRepository([appHostPath]);
        const terminalProvider = {} as AspireTerminalProvider;
        const launchService = createLaunchService();
        const launchStub = sandbox.stub(launchService, 'launch').resolves();
        const provider = new AspireAppHostTreeProvider(repository, terminalProvider, launchService);
        const executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves('refreshed');
        const markStarted = sandbox.spy();

        const refreshResult = await dispatchControlCommand(
            { name: 'refreshAppHosts' },
            repository,
            launchService,
            provider,
            terminalProvider,
            markStarted);
        await dispatchControlCommand(
            { name: 'publishAppHost', appHostPath },
            repository,
            launchService,
            provider,
            terminalProvider,
            markStarted);

        assert.strictEqual(refreshResult, 'refreshed');
        assert.deepStrictEqual(executeCommandStub.getCalls().map(call => call.args), [
            ['aspire-vscode.refreshAppHosts'],
        ]);
        assert.deepStrictEqual(launchStub.firstCall.args, [appHostPath, 'publish', true]);
        assert.strictEqual(markStarted.callCount, 2);
        provider.dispose();
    });
});

function createRepository(candidatePaths: readonly string[], selectedPath?: string): AppHostDataRepository {
    const onDidChangeData: vscode.Event<void> = () => ({ dispose: () => { } });
    return {
        viewMode: 'workspace' as ViewMode,
        appHosts: [],
        workspaceResources: [],
        workspaceAppHostPath: selectedPath,
        workspaceAppHostCandidatePaths: candidatePaths,
        workspaceAppHostName: undefined,
        workspaceAppHostDescription: undefined,
        onDidChangeData,
    } as unknown as AppHostDataRepository;
}

function captureRegisteredTreeCommands(
    sandbox: sinon.SinonSandbox,
    provider: AspireAppHostTreeProvider,
    repository: AppHostDataRepository,
): ReadonlyMap<string, (...args: unknown[]) => Promise<unknown>> {
    const commands = new Map<string, (...args: unknown[]) => Promise<unknown>>();
    sandbox.stub(vscode.commands, 'registerCommand').callsFake((commandId, callback) => {
        commands.set(commandId, callback as (...args: unknown[]) => Promise<unknown>);
        return { dispose: () => { } };
    });
    registerTreeViewCommands(provider, repository);

    return commands;
}

async function dispatchControlCommand(
    command: AspireExtensionE2EControlCommand,
    repository: AppHostDataRepository,
    launchService: AppHostLaunchService,
    provider: AspireAppHostTreeProvider,
    terminalProvider: AspireTerminalProvider,
    markStarted: () => void = () => { },
): Promise<unknown> {
    return await executeE2eControlCommand(
        {} as vscode.ExtensionContext,
        {} as AspireExtensionContext,
        repository,
        launchService,
        provider,
        terminalProvider,
        { hasSnapshot: false },
        {},
        new Map(),
        command,
        markStarted);
}
