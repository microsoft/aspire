import * as assert from 'assert';
import * as fs from 'fs';
import * as net from 'net';
import * as path from 'path';
import { getTaskProcessEventCount, isSamePath, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForSelectedWorkspaceAppHost, waitForTaskProcessEvent } from './helpers/assertions';
import { createEmptyAppHostProject, executeE2eControlCommand, getGeneratedAppHostPath, removeGeneratedProject, restoreWorkspaceAppHostConfig, runE2eTeardown, stopAppHostIfRunning, waitForKnownProcessExit, writeFileWithRetry, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { getRepoRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

interface NodeFunctionsDebugProof {
    proof: string;
    resourceBreakpoint: {
        stoppedEvent: { sessionId: string; sessionType: string; reason: string };
        matchingStackFrame: { source?: { path?: string }; line: number };
    };
    debugSessions: {
        id: string;
        type: string;
        parentSessionId?: string;
        configuration: { request?: string; address?: string; port?: number };
    }[];
}

suite('Aspire Node Functions debugger E2E', function () {
    this.timeout(900000);
    const projectName = 'AspireE2E.NodeFunctionsDebugger';
    const resourceName = 'e2e-node-functions';
    let appHostPath: string | undefined;

    teardown(async () => {
        if (process.env.ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS !== 'true') {
            return;
        }

        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'clearBreakpoints' }),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => appHostPath ? stopAppHostIfRunning(appHostPath) : undefined,
            () => waitForNoDebugSessions(),
            () => appHostPath ? waitForNoRunningAppHost(120000, appHostPath) : undefined,
            () => removeGeneratedProject(projectName),
            () => restoreWorkspaceAppHostConfig(),
        ], 'Node Functions debugger E2E teardown failed.');
    });

    test('starts Core Tools, attaches pwa-node, hits an HTTP breakpoint and cleans up the worker', async function () {
        // Share the existing Functions shard's pinned Core Tools and debugger-extension prerequisites.
        if (process.env.ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS !== 'true') {
            this.skip();
        }

        const projectRoot = await createEmptyAppHostProject(projectName);
        const generatedAppHostPath = getGeneratedAppHostPath(projectName);
        appHostPath = path.join(projectRoot, `${projectName}.csproj`);
        const appHostSourcePath = path.join(projectRoot, 'Program.cs');
        const functionsDirectory = path.join(projectRoot, 'functions');
        fs.cpSync(path.join(getRepoRoot(), 'extension', 'src', 'test', 'fixtures', 'azure-functions-node'), functionsDirectory, { recursive: true });
        const sourcePath = path.join(functionsDirectory, 'HttpProof', 'index.js');
        const breakpointLine = findLine(sourcePath, "const message = 'Aspire Node Functions debugger E2E';");
        const hostingProject = path.relative(projectRoot, path.join(getRepoRoot(), 'src', 'Aspire.Hosting.Azure.Functions', 'Aspire.Hosting.Azure.Functions.csproj')).replace(/\\/g, '/');

        // Use the source integration: the published package used by the baseline fixture may predate
        // AddAzureFunctionsApp. It is a library reference, not another executable project resource.
        const original = fs.readFileSync(generatedAppHostPath, 'utf8');
        const sdkVersion = original.match(/^#:sdk Aspire\.AppHost\.Sdk@([^\r\n]+)$/m)?.[1];
        assert.ok(sdkVersion, 'Expected an Aspire SDK directive in the generated AppHost.');
        writeFileWithRetry(appHostPath, `<Project Sdk="Aspire.AppHost.Sdk/${sdkVersion}">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NoWarn>$(NoWarn);ASPIREAZUREFUNCTIONS001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${hostingProject}" IsAspireProjectResource="false" />
  </ItemGroup>
</Project>
`);
        const withoutDirectives = original.replace(/^#:.*\r?\n/gm, '');
        const appHostSource = withoutDirectives.replace('builder.Build().Run();', `builder.AddAzureFunctionsApp("${resourceName}", "functions", Aspire.Hosting.Azure.AzureFunctionsLanguage.JavaScript);

builder.Build().Run();`);
        assert.notStrictEqual(appHostSource, withoutDirectives, 'Expected the generated AppHost run statement.');
        writeFileWithRetry(appHostSourcePath, appHostSource);
        fs.rmSync(generatedAppHostPath);
        writeWorkspaceAppHostConfigForPath(appHostPath);

        await openAspireView();
        await waitForRepositoryIdle();
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForSelectedWorkspaceAppHost(appHostPath, 180000);
        const taskSequence = getTaskProcessEventCount();

        // This existing bridge installs real VS Code breakpoints before starting the AppHost,
        // drives HTTP traffic after endpoint activation, records DAP stack frames and stops debugging.
        const status = await executeE2eControlCommand({
            name: 'proveAppHostAndResourceDebugging',
            appHostPath,
            resourceName,
            appHostSourcePath,
            appHostBreakpointLine: findLine(appHostSourcePath, `builder.AddAzureFunctionsApp("${resourceName}"`),
            resourceSourcePath: sourcePath,
            resourceBreakpointLine: breakpointLine,
            resourceRequestPath: '/api/node-debug-proof',
            timeoutMs: 300000,
        }, { timeoutMs: 660000 });
        assert.strictEqual(status.status, 'applied');
        const proof = status.result as NodeFunctionsDebugProof;
        assert.strictEqual(proof.proof, 'aspire-apphost-and-resource-debug-breakpoints-hit');
        const hit = proof.resourceBreakpoint;
        assert.strictEqual(hit.stoppedEvent.reason, 'breakpoint');
        assert.strictEqual(hit.stoppedEvent.sessionType, 'pwa-node');
        assert.ok(hit.matchingStackFrame.source?.path);
        assert.ok(isSamePath(hit.matchingStackFrame.source.path, sourcePath));
        assert.strictEqual(hit.matchingStackFrame.line, breakpointLine + 1);
        let session = proof.debugSessions.find(candidate => candidate.id === hit.stoppedEvent.sessionId);
        assert.ok(session, 'Expected the breakpoint to belong to a recorded debug session.');
        // js-debug can represent an attached target as a child session. The inspector connection
        // belongs to its parent; require the hit's own ancestry rather than any unrelated session.
        while (session.configuration.port === undefined && session.parentSessionId) {
            const parent = proof.debugSessions.find(candidate => candidate.id === session!.parentSessionId);
            assert.ok(parent, 'Expected the attached target parent in the recorded debug sessions.');
            session = parent;
        }
        assert.strictEqual(session.type, 'pwa-node');
        assert.strictEqual(session.configuration.request, 'attach');
        assert.strictEqual(session.configuration.address, '127.0.0.1');
        const inspectorPort = session.configuration.port;
        assert.ok(typeof inspectorPort === 'number' && inspectorPort > 0 && inspectorPort <= 65535);

        const taskStarted = await waitForTaskProcessEvent(
            event => event.state === 'started' && event.taskName === 'func: functions'
                && event.taskSource === 'aspire' && event.taskDefinitionType === 'shell',
            'Node Functions Core Tools task to start', 30000, taskSequence);
        assert.ok(taskStarted.processId && taskStarted.processId > 0);
        await waitForTaskProcessEvent(
            event => event.state === 'ended' && event.executionId === taskStarted.executionId,
            'the same Core Tools task to end when debugging stops', 120000, taskStarted.sequence);
        await waitForKnownProcessExit(taskStarted.processId, 'Node Functions Core Tools task', 120000);
        await waitForInspectorExit(inspectorPort, 30000);
        await waitForNoDebugSessions();
        await waitForNoRunningAppHost(120000, appHostPath);
    });
});

function findLine(filePath: string, text: string): number {
    const line = fs.readFileSync(filePath, 'utf8').split(/\r?\n/).findIndex(value => value.includes(text));
    assert.ok(line >= 0, `Expected breakpoint marker ${text} in ${filePath}.`);
    return line;
}

async function waitForInspectorExit(port: number, timeoutMs: number): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        const listening = await new Promise<boolean>((resolve, reject) => {
            const socket = net.createConnection({ host: '127.0.0.1', port });
            socket.setTimeout(1000);
            socket.once('connect', () => {
                socket.destroy();
                resolve(true);
            });
            socket.once('error', error => {
                socket.destroy();
                if ((error as NodeJS.ErrnoException).code === 'ECONNREFUSED') {
                    resolve(false);
                } else {
                    reject(error);
                }
            });
            socket.once('timeout', () => {
                socket.destroy();
                reject(new Error('Timed out probing the Node inspector during cleanup.'));
            });
        });
        if (!listening) {
            return;
        }
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    assert.fail('Node inspector is still listening after debugging stopped.');
}
