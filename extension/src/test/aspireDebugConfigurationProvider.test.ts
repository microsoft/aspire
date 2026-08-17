/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugConfigurationProvider, type ExternalLaunchReservation } from '../debugger/AspireDebugConfigurationProvider';
import { appHostLaunchReservationIdConfigKey, appHostLaunchTokenConfigKey, appHostSelectionOriginConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { isAspireDebugConfigurationExtensionOwned, markAspireDebugConfigurationAsExtensionOwned, stripAspireDebugConfigurationProviderInternalProperties } from '../debugger/AspireDebugConfigurationProviderInternal';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';
import * as cliPathModule from '../utils/cliPath';
import { AppHostDiscoveryService } from '../utils/appHostDiscovery';

/** Captures the AppHost paths the provider claims for `launch.json`/F5 launches. */
class RecordingLaunchReservation implements ExternalLaunchReservation {
    readonly reserved: string[] = [];
    readonly directoryScoped: string[] = [];
    readonly replacements: { previousAppHostPath: string; previousReservationId: string; appHostPath: string }[] = [];
    /** When set, the claim is refused as if a lifecycle-owned launch already held it. */
    claimedByLifecycle = false;

    tryReserveExternalLaunch(appHostPath: string, isDirectoryScope = false): string | false {
        this.reserved.push(appHostPath);
        if (isDirectoryScope) {
            this.directoryScoped.push(appHostPath);
        }
        return this.claimedByLifecycle ? false : `reservation-${this.reserved.length}`;
    }

    replaceExternalLaunchReservation(previousAppHostPath: string, previousReservationId: string, appHostPath: string, isDirectoryScope = false): string | false {
        this.replacements.push({ previousAppHostPath, previousReservationId, appHostPath });
        return this.tryReserveExternalLaunch(appHostPath, isDirectoryScope);
    }
}

suite('AspireDebugConfigurationProvider', () => {
    let tempDir: string;
    let sandbox: sinon.SinonSandbox;
    let launchReservation: RecordingLaunchReservation;

    setup(() => {
        sandbox = sinon.createSandbox();
        launchReservation = new RecordingLaunchReservation();
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-debug-configuration-provider-'));
    });

    teardown(() => {
        sandbox.restore();
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    test('resolves launch config SDK-style AppHost Program.cs to containing project file', async () => {
        const appHostDirectory = path.join(tempDir, 'AppHost');
        fs.mkdirSync(appHostDirectory);

        const programPath = path.join(appHostDirectory, 'Program.cs');
        const projectPath = path.join(appHostDirectory, 'AppHost.csproj');
        fs.writeFileSync(programPath, 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();');
        fs.writeFileSync(projectPath, '<Project Sdk="Microsoft.NET.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(projectPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.program, projectPath);
        assert.strictEqual(config?.[appHostSelectionOriginConfigKey], 'explicit-launch-configuration');
    });

    test('leaves launch config single-file apphost.cs unchanged', async () => {
        const appHostPath = path.join(tempDir, 'apphost.cs');
        fs.writeFileSync(appHostPath, '#:sdk Aspire.AppHost.Sdk\nvar builder = DistributedApplication.CreateBuilder(args);');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath
        });

        assert.strictEqual(config?.program, appHostPath);
    });

    test('leaves launch config TypeScript apphost.ts unchanged', async () => {
        const appHostPath = path.join(tempDir, 'apphost.ts');
        fs.writeFileSync(appHostPath, 'import { createBuilder } from "./.aspire/modules/aspire";');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath, appHostPath, 'typescript/nodejs'), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath
        });

        assert.strictEqual(config?.program, appHostPath);
    });

    test('reserves the resolved AppHost so an agent cannot start a second one beside a launch.json run', async () => {
        // `launch.json`/F5 never reaches `AppHostLaunchService.launch`, so this hook is the
        // only point the two launch paths share before the debug session exists. Without
        // the reservation the AppHost lifecycle tool sees nothing in flight and starts a
        // duplicate.
        const appHostDirectory = path.join(tempDir, 'AppHost');
        fs.mkdirSync(appHostDirectory);
        const programPath = path.join(appHostDirectory, 'Program.cs');
        const projectPath = path.join(appHostDirectory, 'AppHost.csproj');
        fs.writeFileSync(programPath, 'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();');
        fs.writeFileSync(projectPath, '<Project Sdk="Microsoft.NET.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(projectPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.program, projectPath);
        // The reservation must name the resolved target, which is what the tool addresses
        // and what the terminate handler later clears.
        assert.deepStrictEqual(launchReservation.reserved, [projectPath]);
        assert.strictEqual(config?.[appHostLaunchReservationIdConfigKey], 'reservation-1');
    });

    test('does not reserve a launch for an Aspire command that is not a run', async () => {
        // `publish`/`deploy`/`do` are not AppHost lifetimes, so reserving them would make
        // the tool report an AppHost as starting when nothing is being started.
        const appHostPath = path.join(tempDir, 'AppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Publish AppHost',
            type: 'aspire',
            request: 'launch',
            command: 'publish',
            program: appHostPath
        });

        assert.deepStrictEqual(launchReservation.reserved, []);
    });

    test('claims the concrete AppHost when the workspace-folder launch config leaves program as the directory', async () => {
        // The default `${workspaceFolder}` configuration deliberately resolves to the folder,
        // so claiming `config.program` would claim a directory. A directory is not the same
        // identity as the AppHost inside it, which would let the lifecycle tool start a
        // duplicate during the F5 startup window.
        const workspaceRoot = path.join(tempDir, 'workspace');
        const appHostDirectory = path.join(workspaceRoot, 'AppHost');
        fs.mkdirSync(appHostDirectory, { recursive: true });
        const projectPath = path.join(appHostDirectory, 'AppHost.csproj');
        fs.writeFileSync(projectPath, '<Project Sdk="Aspire.AppHost.Sdk" />');

        const folder: vscode.WorkspaceFolder = { uri: vscode.Uri.file(workspaceRoot), name: 'workspace', index: 0 };
        // `Uri.file` lowercases the drive letter on Windows, so the folder path has to come
        // from the folder itself for the configuration to look like the `${workspaceFolder}`
        // one VS Code substitutes.
        const folderPath = folder.uri.fsPath;
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(projectPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folderPath
        });

        assert.strictEqual(config?.program, folderPath);
        assert.deepStrictEqual(launchReservation.reserved, [projectPath]);
        assert.deepStrictEqual(launchReservation.directoryScoped, []);
    });

    test('reserves the workspace directory while F5 defers AppHost selection to the CLI', async () => {
        const folder = createWorkspaceFolder(path.join(tempDir, 'workspace'));
        const provider = new AspireDebugConfigurationProvider(
            createAppHostDiscoveryService(folder.uri.fsPath, null),
            launchReservation);

        const firstPass = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folder.uri.fsPath,
        });
        const secondPass = firstPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, firstPass)
            : undefined;

        assert.strictEqual(secondPass?.program, folder.uri.fsPath);
        assert.strictEqual(secondPass?.[appHostLaunchReservationIdConfigKey], 'reservation-1');
        assert.deepStrictEqual(launchReservation.reserved, [folder.uri.fsPath]);
        assert.deepStrictEqual(launchReservation.directoryScoped, [folder.uri.fsPath]);
        assert.deepStrictEqual(launchReservation.replacements, []);
    });

    test('replaces a workspace directory reservation when discovery later resolves a concrete AppHost', async () => {
        const folder = createWorkspaceFolder(path.join(tempDir, 'workspace'));
        const projectPath = path.join(folder.uri.fsPath, 'AppHost', 'AppHost.csproj');
        let candidatePath: string | undefined;
        const discoveryService = {
            resolveDebugTarget: async (filePath: string) => filePath,
            tryFindWorkspaceDefaultCandidate: async () => candidatePath
                ? { path: candidatePath, language: 'csharp', status: 'buildable' }
                : undefined,
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireDebugConfigurationProvider(discoveryService, launchReservation);
        const config: vscode.DebugConfiguration = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folder.uri.fsPath,
        };

        const firstPass = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, config);
        candidatePath = projectPath;
        const secondPass = firstPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, firstPass)
            : undefined;

        assert.strictEqual(secondPass?.[appHostLaunchReservationIdConfigKey], 'reservation-2');
        assert.deepStrictEqual(launchReservation.reserved, [folder.uri.fsPath, projectPath]);
        assert.deepStrictEqual(launchReservation.directoryScoped, [folder.uri.fsPath]);
        assert.deepStrictEqual(launchReservation.replacements, [{
            previousAppHostPath: folder.uri.fsPath,
            previousReservationId: 'reservation-1',
            appHostPath: projectPath,
        }]);
    });

    test('cancels a launch.json run when a lifecycle-owned launch already claimed the AppHost', async () => {
        // The lifecycle caller has already passed its own check by this point and cannot be
        // called back, so letting this session proceed would start a second AppHost for the
        // same project.
        const appHostPath = path.join(tempDir, 'AppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        launchReservation.claimedByLifecycle = true;
        const message = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath
        });

        assert.strictEqual(config, undefined);
        assert.strictEqual(message.calledOnce, true);
    });

    test('does not trust a launch.json launchedByExtension property as lifecycle-owned', async () => {
        const appHostPath = path.join(tempDir, 'AppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        launchReservation.claimedByLifecycle = true;
        const message = sandbox.stub(vscode.window, 'showInformationMessage').resolves(undefined);

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath,
            launchedByExtension: true
        });

        assert.strictEqual(config, undefined);
        assert.deepStrictEqual(launchReservation.reserved, [appHostPath]);
        assert.strictEqual(message.calledOnce, true);
    });

    test('does not trust a launch.json reservation ID', async () => {
        const appHostPath = path.join(tempDir, 'AppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Publish AppHost',
            type: 'aspire',
            request: 'launch',
            command: 'publish',
            program: appHostPath,
            [appHostLaunchReservationIdConfigKey]: 'forged-reservation',
        });

        assert.strictEqual(config?.[appHostLaunchReservationIdConfigKey], undefined);
        assert.deepStrictEqual(launchReservation.reserved, []);
    });

    test('reuses one reservation across repeated resolver passes for an external launch', async () => {
        const appHostPath = path.join(tempDir, 'AppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        const config: vscode.DebugConfiguration = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath,
        };

        const firstPass = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, config);
        const secondPass = firstPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, firstPass)
            : undefined;

        assert.deepStrictEqual(launchReservation.reserved, [appHostPath]);
        assert.strictEqual(firstPass?.[appHostLaunchReservationIdConfigKey], 'reservation-1');
        assert.strictEqual(secondPass?.[appHostLaunchReservationIdConfigKey], 'reservation-1');
    });

    test('reuses one reservation when repeated resolver passes use equivalent AppHost paths', async () => {
        const appHostDirectory = path.join(tempDir, 'AppHost');
        const projectPath = path.join(appHostDirectory, 'AppHost.csproj');
        const sourcePath = path.join(appHostDirectory, 'Program.cs');
        fs.mkdirSync(appHostDirectory);
        fs.writeFileSync(projectPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        fs.writeFileSync(sourcePath, 'var builder = DistributedApplication.CreateBuilder(args);');

        const folder = createWorkspaceFolder(tempDir);
        let selectedAppHostPath = projectPath;
        const discoveryService = {
            resolveDebugTarget: async (filePath: string) => filePath,
            tryFindWorkspaceDefaultCandidate: async () => ({
                path: selectedAppHostPath,
                language: 'csharp',
                status: 'buildable',
            }),
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireDebugConfigurationProvider(discoveryService, launchReservation);
        const config: vscode.DebugConfiguration = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folder.uri.fsPath,
        };

        const firstPass = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, config);
        selectedAppHostPath = sourcePath;
        const secondPass = firstPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, firstPass)
            : undefined;

        assert.deepStrictEqual(launchReservation.reserved, [projectPath]);
        assert.deepStrictEqual(launchReservation.replacements, []);
        assert.strictEqual(secondPass?.[appHostLaunchReservationIdConfigKey], 'reservation-1');
    });

    test('replaces an equivalent-path reservation after its identity later becomes ambiguous', async () => {
        const appHostDirectory = path.join(tempDir, 'AppHost');
        const projectPath = path.join(appHostDirectory, 'AppHost.csproj');
        const sourcePath = path.join(appHostDirectory, 'Program.cs');
        const replacementDirectory = path.join(tempDir, 'Replacement');
        const replacementPath = path.join(replacementDirectory, 'Replacement.csproj');
        fs.mkdirSync(appHostDirectory);
        fs.mkdirSync(replacementDirectory);
        fs.writeFileSync(projectPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        fs.writeFileSync(sourcePath, 'var builder = DistributedApplication.CreateBuilder(args);');
        fs.writeFileSync(replacementPath, '<Project Sdk="Aspire.AppHost.Sdk" />');

        const folder = createWorkspaceFolder(tempDir);
        let selectedAppHostPath = projectPath;
        const discoveryService = {
            resolveDebugTarget: async (filePath: string) => filePath,
            tryFindWorkspaceDefaultCandidate: async () => ({
                path: selectedAppHostPath,
                language: 'csharp',
                status: 'buildable',
            }),
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireDebugConfigurationProvider(discoveryService, launchReservation);
        const config: vscode.DebugConfiguration = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folder.uri.fsPath,
        };

        const firstPass = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, config);
        selectedAppHostPath = sourcePath;
        const secondPass = firstPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, firstPass)
            : undefined;
        fs.writeFileSync(path.join(appHostDirectory, 'Sibling.csproj'), '<Project Sdk="Aspire.AppHost.Sdk" />');
        selectedAppHostPath = replacementPath;
        const thirdPass = secondPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, secondPass)
            : undefined;

        assert.deepStrictEqual(launchReservation.reserved, [projectPath, replacementPath]);
        assert.deepStrictEqual(launchReservation.replacements, [{
            previousAppHostPath: projectPath,
            previousReservationId: 'reservation-1',
            appHostPath: replacementPath,
        }]);
        assert.strictEqual(thirdPass?.[appHostLaunchReservationIdConfigKey], 'reservation-2');
    });

    test('replaces an external reservation when repeated default discovery resolves a different AppHost', async () => {
        const workspaceRoot = path.join(tempDir, 'workspace');
        const firstAppHostPath = path.join(workspaceRoot, 'First', 'First.csproj');
        const secondAppHostPath = path.join(workspaceRoot, 'Second', 'Second.csproj');
        fs.mkdirSync(path.dirname(firstAppHostPath), { recursive: true });
        fs.mkdirSync(path.dirname(secondAppHostPath), { recursive: true });
        fs.writeFileSync(firstAppHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        fs.writeFileSync(secondAppHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');

        const folder = createWorkspaceFolder(workspaceRoot);
        let selectedAppHostPath = firstAppHostPath;
        const discoveryService = {
            resolveDebugTarget: async (filePath: string) => filePath,
            tryFindWorkspaceDefaultCandidate: async () => ({
                path: selectedAppHostPath,
                language: 'csharp',
                status: 'buildable',
            }),
        } as unknown as AppHostDiscoveryService;
        const provider = new AspireDebugConfigurationProvider(discoveryService, launchReservation);
        const config: vscode.DebugConfiguration = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folder.uri.fsPath,
        };

        const firstPass = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, config);
        const firstReservationId = firstPass?.[appHostLaunchReservationIdConfigKey];
        selectedAppHostPath = secondAppHostPath;
        const secondPass = firstPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, firstPass)
            : undefined;

        assert.deepStrictEqual(launchReservation.reserved, [firstAppHostPath, secondAppHostPath]);
        assert.deepStrictEqual(launchReservation.replacements, [{
            previousAppHostPath: firstAppHostPath,
            previousReservationId: 'reservation-1',
            appHostPath: secondAppHostPath,
        }]);
        assert.strictEqual(firstReservationId, 'reservation-1');
        assert.strictEqual(secondPass?.[appHostLaunchReservationIdConfigKey], 'reservation-2');
    });

    test('does not claim an AppHostLaunchService launch as an external one', async () => {
        // `launchCore` reserves its own slot and then calls `startDebugging`, which reaches
        // this resolver. Treating it as external would make the launch refuse itself against
        // the claim it just took.
        const appHostPath = path.join(tempDir, 'AppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        launchReservation.claimedByLifecycle = true;

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        const debugConfiguration: vscode.DebugConfiguration = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath,
            [appHostLaunchReservationIdConfigKey]: 'service-reservation',
        };
        markAspireDebugConfigurationAsExtensionOwned(debugConfiguration);

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, debugConfiguration);

        assert.strictEqual(config?.program, appHostPath);
        assert.deepStrictEqual(launchReservation.reserved, []);
        // The marker is internal and must not reach the debug adapter.
        assert.strictEqual('launchedByExtension' in (config ?? {}), false);
        assert.strictEqual(isAspireDebugConfigurationExtensionOwned(config ?? {}), true);
        assert.strictEqual(config?.[appHostLaunchReservationIdConfigKey], 'service-reservation');
    });

    test('does not claim repeated resolver passes for an AppHostLaunchService launch as external', async () => {
        // VS Code can hand the same configuration object through the substituted resolver
        // more than once before the debug adapter starts. The first pass strips the internal
        // marker, so the provider has to remember that this object already belonged to
        // `AppHostLaunchService` instead of treating the second pass as launch.json/F5.
        const appHostPath = path.join(tempDir, 'AppHost.csproj');
        fs.writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        launchReservation.claimedByLifecycle = true;

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);
        const config: vscode.DebugConfiguration = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath
        };
        markAspireDebugConfigurationAsExtensionOwned(config);

        const firstPass = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, config);
        const secondPass = firstPass
            ? await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, firstPass)
            : undefined;

        assert.strictEqual(secondPass?.program, appHostPath);
        assert.deepStrictEqual(launchReservation.reserved, []);
        assert.strictEqual('launchedByExtension' in (secondPass ?? {}), false);
        assert.strictEqual(isAspireDebugConfigurationExtensionOwned(secondPass ?? {}), true);

        stripAspireDebugConfigurationProviderInternalProperties(secondPass ?? {});
        assert.strictEqual(isAspireDebugConfigurationExtensionOwned(secondPass ?? {}), false);
        assert.deepStrictEqual(Object.keys(secondPass ?? {}).filter(key => key.startsWith('__aspireAppHostLaunchServiceConfiguration_')), []);
    });

    test('preserves AppHost launch token through substituted variable resolution', async () => {
        const appHostPath = path.join(tempDir, 'apphost.ts');
        fs.writeFileSync(appHostPath, 'import { createBuilder } from "./.aspire/modules/aspire";');

        const provider = new AspireDebugConfigurationProvider(
            createAppHostDiscoveryService(appHostPath, appHostPath, 'typescript/nodejs'),
            launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: appHostPath,
            [appHostLaunchTokenConfigKey]: 42,
        });

        assert.strictEqual(config?.[appHostLaunchTokenConfigKey], 42);
    });

    test('leaves launch config non-AppHost C# source file unchanged', async () => {
        const appDirectory = path.join(tempDir, 'App');
        fs.mkdirSync(appDirectory);

        const programPath = path.join(appDirectory, 'Program.cs');
        fs.writeFileSync(programPath, 'Console.WriteLine("Hello");');
        fs.writeFileSync(path.join(appDirectory, 'App.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />');

        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(programPath), launchReservation);
        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.program, programPath);
    });

    test('leaves workspace folder launch target unchanged and records AppHost telemetry target', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const appHostPath = path.join(tempDir, 'NestedAppHost', 'apphost.ts');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(appHostPath), launchReservation);

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: folder.uri.fsPath
        });

        assert.strictEqual(config?.program, folder.uri.fsPath);
        assert.strictEqual(config?.__aspireAppHostTelemetryTargetPath, appHostPath);
        assert.strictEqual(config?.[appHostSelectionOriginConfigKey], 'default-discovery');
    });

    test('treats macOS launch target differing from workspace folder only by casing as explicit', async () => {
        sandbox.stub(process, 'platform').value('darwin');
        const workspacePath = path.join(tempDir, 'workspace');
        const programPath = path.join(tempDir, 'Workspace');
        const folder = createWorkspaceFolder(workspacePath);
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(programPath), launchReservation);

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(folder, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.[appHostSelectionOriginConfigKey], 'explicit-launch-configuration');
    });

    test('provides dynamic launch config when active file resolves to AppHost candidate', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const projectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(projectPath), launchReservation);
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, projectPath);
        assert.strictEqual(configs[0][appHostSelectionOriginConfigKey], 'default-discovery');
    });

    test('omits selection origin from launch configurations written to launch.json', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const projectPath = path.join(tempDir, 'AppHost', 'AppHost.csproj');
        const provider = new AspireDebugConfigurationProvider(
            createAppHostDiscoveryService(projectPath),
            launchReservation,
            vscode.DebugConfigurationProviderTriggerKind.Initial);
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, projectPath);
        assert.ok(!(appHostSelectionOriginConfigKey in configs[0]));
    });

    test('provides default dynamic launch config when active file is not an AppHost candidate', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'Web', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(programPath, null), launchReservation);
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
    });

    test('provides default dynamic launch config when discovery fails', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createFailingAppHostDiscoveryService(), launchReservation);
        setActiveEditor(programPath, folder);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
    });

    test('provides default dynamic launch config when there is no active editor', async () => {
        const folder = createWorkspaceFolder(tempDir);
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService(folder.uri.fsPath, null), launchReservation);
        sandbox.stub(vscode.window, 'activeTextEditor').value(undefined);

        const configs = await provider.provideDebugConfigurations(folder);

        assert.strictEqual(configs.length, 1);
        assert.strictEqual(configs[0].program, folder.uri.fsPath);
    });

    test('leaves launch config program unchanged when debug target resolution fails', async () => {
        const programPath = path.join(tempDir, 'AppHost', 'Program.cs');
        const provider = new AspireDebugConfigurationProvider(createFailingAppHostDiscoveryService(), launchReservation);

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: programPath
        });

        assert.strictEqual(config?.program, programPath);
    });

    test('resolveDebugConfiguration keeps skip flag through repeated resolver calls after launch service already checked CLI', async () => {
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService('/repo/AppHost.csproj'), launchReservation);
        const resolveCliPathStub = sandbox.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
        const showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);

        const initialConfig = {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: '/repo/AppHost.csproj',
            skipCliAvailabilityCheck: true,
        } as AspireExtendedDebugConfiguration;

        const firstConfig = await provider.resolveDebugConfiguration(undefined, initialConfig) as AspireExtendedDebugConfiguration | undefined;
        const config = firstConfig
            ? await provider.resolveDebugConfiguration(undefined, firstConfig) as AspireExtendedDebugConfiguration | undefined
            : undefined;

        assert.ok(config);
        assert.strictEqual(config.program, '/repo/AppHost.csproj');
        assert.strictEqual(config.skipCliAvailabilityCheck, true);
        assert.strictEqual(resolveCliPathStub.called, false);
        assert.strictEqual(showErrorMessageStub.called, false);
    });

    test('resolveDebugConfigurationWithSubstitutedVariables removes internal skip flag before launch', async () => {
        const provider = new AspireDebugConfigurationProvider(createAppHostDiscoveryService('/repo/AppHost.csproj'), launchReservation);

        const config = await provider.resolveDebugConfigurationWithSubstitutedVariables(undefined, {
            name: 'Debug AppHost',
            type: 'aspire',
            request: 'launch',
            program: '/repo/AppHost.csproj',
            skipCliAvailabilityCheck: true,
        } as AspireExtendedDebugConfiguration) as AspireExtendedDebugConfiguration | undefined;

        assert.ok(config);
        assert.strictEqual(config.skipCliAvailabilityCheck, undefined);
    });

    function setActiveEditor(filePath: string, folder: vscode.WorkspaceFolder): void {
        sandbox.stub(vscode.window, 'activeTextEditor').value({
            document: {
                uri: vscode.Uri.file(filePath),
            },
        });
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(folder);
    }
});

function createWorkspaceFolder(folderPath: string): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(folderPath),
        name: 'workspace',
        index: 0,
    };
}

function createAppHostDiscoveryService(resolvedPath: string, candidatePath: string | null = resolvedPath, language = 'csharp'): AppHostDiscoveryService {
    const createCandidate = () => candidatePath ? {
        path: candidatePath,
        language: language,
        status: 'buildable',
    } : undefined;

    return {
        resolveDebugTarget: async (filePath: string, folder?: vscode.WorkspaceFolder) => folder && path.resolve(filePath) === path.resolve(folder.uri.fsPath) ? filePath : resolvedPath,
        tryFindWorkspaceDefaultCandidate: async (filePath: string, folder?: vscode.WorkspaceFolder) => folder && path.resolve(filePath) === path.resolve(folder.uri.fsPath) ? createCandidate() : undefined,
        tryFindCandidateForEditorFile: async () => createCandidate(),
    } as unknown as AppHostDiscoveryService;
}

function createFailingAppHostDiscoveryService(): AppHostDiscoveryService {
    return {
        resolveDebugTarget: async () => {
            throw new Error('discovery failed');
        },
        tryFindCandidateForEditorFile: async () => {
            throw new Error('discovery failed');
        },
    } as unknown as AppHostDiscoveryService;
}
