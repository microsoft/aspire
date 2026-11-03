import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { findResource, waitForRunningResourceWithUrl } from '../test-e2e/helpers/assertions';
import type { AspireExtensionE2EStateFile, AspireResourceState, AspireResourceUrlState } from '../types/extensionApi';

suite('E2E resource URL readiness', () => {
    const environmentNames = [
        'ASPIRE_EXTENSION_E2E_STATE_FILE',
        'ASPIRE_EXTENSION_E2E_PRIMARY_APPHOST',
        'ASPIRE_EXTENSION_E2E_RUN_ID',
    ] as const;
    const httpsUrl: AspireResourceUrlState = {
        name: 'https',
        displayName: null,
        url: 'https://localhost:12345',
        isInternal: false,
    };
    const runningResource: AspireResourceState = {
        name: 'e2e-functions-instance',
        displayName: 'e2e-functions',
        resourceType: 'Project',
        state: 'Running',
        projectPath: null,
        dashboardUrl: null,
        urls: [httpsUrl],
        commands: null,
    };
    let directory: string;
    let statePath: string;
    let previousEnvironment: Record<string, string | undefined>;
    let snapshot: AspireExtensionE2EStateFile;

    setup(() => {
        previousEnvironment = Object.fromEntries(environmentNames.map(name => [name, process.env[name]]));
        directory = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'aspire-e2e-readiness-'));
        statePath = path.join(directory, 'state.json');
        const appHostPath = path.join(directory, 'AppHost.csproj');
        process.env.ASPIRE_EXTENSION_E2E_STATE_FILE = statePath;
        process.env.ASPIRE_EXTENSION_E2E_PRIMARY_APPHOST = appHostPath;
        process.env.ASPIRE_EXTENSION_E2E_RUN_ID = 'resource-readiness';
        snapshot = {
            extensionHostSessionId: 'resource-readiness',
            updatedAt: '2026-01-01T00:00:00Z',
            runId: 'resource-readiness',
            state: {
                viewMode: 'workspace',
                isRepositoryLoading: false,
                isWorkspaceAppHostDiscoveryComplete: true,
                hasError: false,
                errorMessage: undefined,
                workspaceAppHost: undefined,
                workspaceAppHostName: 'AppHost',
                workspaceAppHostPath: appHostPath,
                workspaceAppHostCandidatePaths: [appHostPath],
                workspaceAppHostDescription: undefined,
                workspaceResources: [],
                appHosts: [],
                launchingPaths: [],
                stoppingPaths: [],
                debugSessions: [],
            },
            commandInvocations: [],
            terminalCommands: [],
            debugLaunches: [],
            debugConsoleOutputs: [],
            stoppingPathEvents: [],
            taskProcessEvents: [],
            browserDebugSessions: [],
        };
    });

    teardown(() => {
        for (const name of environmentNames) {
            const previousValue = previousEnvironment[name];
            if (previousValue === undefined) {
                delete process.env[name];
            }
            else {
                process.env[name] = previousValue;
            }
        }
        fs.rmSync(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    });

    const notReadyCases: { name: string; resources: AspireResourceState[] }[] = [
        { name: 'resource missing', resources: [] },
        { name: 'starting with HTTPS', resources: [{ ...runningResource, state: 'Starting' }] },
        { name: 'running with null URLs', resources: [{ ...runningResource, urls: null }] },
        { name: 'running with empty URLs', resources: [{ ...runningResource, urls: [] }] },
        {
            name: 'running with only HTTP',
            resources: [{ ...runningResource, urls: [{ ...httpsUrl, name: 'http', url: 'http://localhost:12345' }] }],
        },
        { name: 'stopped with HTTPS', resources: [{ ...runningResource, state: 'Exited' }] },
        {
            name: 'a different resource with HTTPS',
            resources: [
                { ...runningResource, urls: [] },
                { ...runningResource, name: 'other-resource', displayName: 'other-resource' },
            ],
        },
    ];

    for (const { name, resources } of notReadyCases) {
        test(`waits for a later ready snapshot when ${name}`, async () => {
            writeResources(resources);

            // The waiter reads its first snapshot synchronously before yielding to its polling delay.
            // Publishing next makes the ordering deterministic without racing a timer.
            const pending = waitForRunningResourceWithUrl('e2e-functions', 'https:', 5000);
            writeResources([runningResource]);

            const ready = await pending;
            assert.deepStrictEqual(findResource(ready.state, 'e2e-functions'), runningResource);
        });
    }

    test('accepts an already-ready resource by its instance name', async () => {
        writeResources([runningResource]);

        const ready = await waitForRunningResourceWithUrl(runningResource.name, 'https:', 5000);

        assert.deepStrictEqual(findResource(ready.state, runningResource.name), runningResource);
    });

    test('reports the requested endpoint and last state when HTTPS never appears', async () => {
        const resource = { ...runningResource, urls: [] };
        writeResources([resource]);

        await assert.rejects(
            waitForRunningResourceWithUrl('e2e-functions', 'https:', 1000),
            (error: Error) => {
                assert.ok(error.message.includes("resource 'e2e-functions' to be Running with a 'https:' URL"));
                const lastState = JSON.parse(error.message.split('\nLast state: ')[1]) as AspireExtensionE2EStateFile;
                assert.deepStrictEqual(findResource(lastState.state, 'e2e-functions'), resource);
                return true;
            });
    });

    function writeResources(resources: readonly AspireResourceState[]): void {
        snapshot.state.workspaceResources = resources;
        fs.writeFileSync(statePath, JSON.stringify(snapshot));
    }
});
