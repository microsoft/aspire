import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import type { AppHostDisplayInfo, ResourceJson } from '../data/AppHostDataRepository';
import { createProjectResourceAttachProvider, projectDebuggerExtension, projectResourceAttachProvider } from '../debugger/languages/dotnet';
import { createGoResourceAttachProvider } from '../debugger/languages/go';
import { ResourceAttachProviderRegistry } from '../debugger/resourceAttachProviders';
import { ResourceDebugAppHostIdentityComparer, ResourceDebugAppHostRepository, ResourceDebugService, ResourceDebugServiceDependencies } from '../debugger/resourceDebugService';
import { ResourceDebugSessionEvents, ResourceDebugSessionRegistry, ResourceDebugSessionRegistryOptions } from '../debugger/resourceDebugSessionRegistry';
import { ResourceAttachConfigurationError, type ResourceAttachProvider, type ResourceDebugAppHostTarget, type ResourceDebugRequest, type ResourceDebugResourceSnapshot, type ResourceDebugResult } from '../debugger/resourceDebugContracts';
import { extensionLogOutputChannel } from '../utils/logging';

const target: ResourceDebugAppHostTarget = {
    absolutePath: '/repo/AppHost.csproj',
    displayPath: 'AppHost.csproj',
};
const resolvedTarget: ResourceDebugAppHostTarget = {
    ...target,
    appHostPid: 42,
};

function createResource(overrides: Partial<ResourceJson> = {}): ResourceJson {
    return {
        name: 'api',
        displayName: 'API',
        resourceType: 'Project',
        state: 'Running',
        stateStyle: null,
        healthStatus: null,
        healthReports: null,
        exitCode: null,
        dashboardUrl: null,
        urls: null,
        commands: null,
        properties: {
            'project.path': '/repo/api/Api.csproj',
            'executable.path': 'dotnet',
        },
        ...overrides,
    };
}

function createGoResource(overrides: Partial<ResourceJson> = {}): ResourceJson {
    return createResource({
        resourceType: 'Executable',
        properties: {
            'resource.launchConfigurationType': 'go',
            'executable.path': 'go',
            'executable.pid': '1234',
        },
        ...overrides,
    });
}

function createAppHost(overrides: Partial<AppHostDisplayInfo> = {}): AppHostDisplayInfo {
    return {
        appHostPath: target.absolutePath,
        appHostPid: 42,
        cliPid: null,
        dashboardUrl: null,
        resources: [createResource()],
        ...overrides,
    };
}

function createRequest(overrides: Partial<ResourceDebugRequest> = {}): ResourceDebugRequest {
    return {
        source: 'tree',
        strategy: 'attach',
        appHost: target,
        resourceName: 'api',
        ...overrides,
    };
}

interface RecordedResourceDebugTelemetryEvent {
    readonly name: string;
    readonly properties: Record<string, string>;
    readonly measurements: Record<string, number> | undefined;
}

class TestResourceDebugTelemetry {
    public readonly events: RecordedResourceDebugTelemetryEvent[] = [];
    public currentTime = 0;

    now(): number {
        return this.currentTime;
    }

    recordStart(properties: Record<string, string>): void {
        this._record('aspire/vscode/resourcedebug/start', properties);
    }

    recordResult(properties: Record<string, string>, measurements: Record<string, number>): void {
        this._record('aspire/vscode/resourcedebug/result', properties, measurements);
    }

    recordSessionEnd(properties: Record<string, string>, measurements: Record<string, number>): void {
        this._record('aspire/vscode/resourcedebug/session/end', properties, measurements);
    }

    private _record(name: string, properties: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }
}

class TestResourceDebugClock {
    private readonly _timestamps: (number | Error)[];

    constructor(...timestamps: (number | Error)[]) {
        this._timestamps = timestamps;
    }

    now(): number {
        const timestamp = this._timestamps.shift();
        if (timestamp instanceof Error) {
            throw timestamp;
        }

        return timestamp ?? 0;
    }
}

function createProvider(overrides: Partial<ResourceAttachProvider> = {}): ResourceAttachProvider {
    return {
        id: 'dotnet',
        requiredDebuggerExtensions: [{
            id: 'ms-dotnettools.csharp',
            label: 'C#',
        }],
        canRecognizeResource: () => true,
        canAttachToResource: () => true,
        createDebugConfiguration: async () => ({
            type: 'coreclr',
            request: 'attach',
            name: 'Attach debugger: API',
        }),
        ...overrides,
    };
}

class TestDebugSessionEvents implements ResourceDebugSessionEvents {
    private _startListener: ((session: vscode.DebugSession) => void) | undefined;
    private _terminateListener: ((session: vscode.DebugSession) => void) | undefined;
    public startedConfiguration: vscode.DebugConfiguration | undefined;

    onDidStartDebugSession(listener: (session: vscode.DebugSession) => void): vscode.Disposable {
        this._startListener = listener;
        return new vscode.Disposable(() => {
            this._startListener = undefined;
        });
    }

    onDidTerminateDebugSession(listener: (session: vscode.DebugSession) => void): vscode.Disposable {
        this._terminateListener = listener;
        return new vscode.Disposable(() => {
            this._terminateListener = undefined;
        });
    }

    start(configuration: vscode.DebugConfiguration): void {
        this.startedConfiguration = configuration;
        this._startListener?.({
            id: 'resource-attach-session',
            configuration,
        } as vscode.DebugSession);
    }

    terminate(configuration: vscode.DebugConfiguration): void {
        this._terminateListener?.({
            id: 'resource-attach-session',
            configuration,
        } as vscode.DebugSession);
    }
}

function createService(options: {
    appHosts?: readonly AppHostDisplayInfo[];
    provider?: ResourceAttachProvider;
    providers?: readonly ResourceAttachProvider[];
    isExtensionInstalled?: (extensionId: string) => boolean;
    startDebugging?: (folder: vscode.WorkspaceFolder | undefined, configuration: vscode.DebugConfiguration) => Thenable<boolean>;
    compareAppHostIdentity?: ResourceDebugAppHostIdentityComparer;
    telemetry?: TestResourceDebugTelemetry;
    clock?: { now(): number };
    pendingStartTimeoutMs?: number;
    isProcessAlreadyDebugged?: (processId: number) => boolean;
} = {}): {
    service: ResourceDebugService;
    repository: ResourceDebugAppHostRepository;
    sessions: ResourceDebugSessionRegistry;
    events: TestDebugSessionEvents;
    telemetry: TestResourceDebugTelemetry;
} {
    const repository: ResourceDebugAppHostRepository = {
        fetchRunningAppHostsOnce: async () => options.appHosts ?? [createAppHost()],
        fetchAppHostResourcesOnce: async appHostPath =>
            (options.appHosts ?? [createAppHost()]).find(appHost => appHost.appHostPath === appHostPath)?.resources ?? [],
    };
    const events = new TestDebugSessionEvents();
    const telemetry = options.telemetry ?? new TestResourceDebugTelemetry();
    const clock = options.clock ?? telemetry;
    const sessions = new ResourceDebugSessionRegistry(events, {
        pendingStartTimeoutMs: options.pendingStartTimeoutMs,
        telemetry,
        clock,
    } as unknown as ResourceDebugSessionRegistryOptions);
    const providers = new ResourceAttachProviderRegistry(
        options.providers ?? [options.provider ?? createProvider()],
        options.isExtensionInstalled ?? (() => true));
    const service = new ResourceDebugService({
        appHostRepository: repository,
        attachProviders: providers,
        sessionRegistry: sessions,
        startDebugging: options.startDebugging ?? (async () => true),
        compareAppHostIdentity: options.compareAppHostIdentity,
        telemetry,
        clock,
        isProcessAlreadyDebugged: options.isProcessAlreadyDebugged,
    } as unknown as ResourceDebugServiceDependencies);

    return { service, repository, sessions, events, telemetry };
}

suite('Resource debug service', () => {
    teardown(() => sinon.restore());

    test('keeps ResourceDebuggerExtension launch-only', () => {
        assert.deepStrictEqual(
            Object.keys(projectDebuggerExtension).sort(),
            [
                'createDebugSessionConfigurationCallback',
                'debugAdapter',
                'extensionId',
                'getDisplayName',
                'getProjectFile',
                'getSupportedFileTypes',
                'resourceType',
            ]);
    });

    test('registers .NET attach behavior independently from the launch provider', () => {
        const providers = new ResourceAttachProviderRegistry([projectResourceAttachProvider], () => true);

        assert.strictEqual(providers.getRecognizedProviderForResource(createResource({
            properties: {
                'project.path': '/repo/api/Api.csproj',
                'executable.path': 'dotnet',
                'executable.pid': '42',
            },
        }))?.id, 'dotnet');
    });

    test('uses the first recognized provider for readiness and configuration', async () => {
        const firstProvider = createProvider({
            canAttachToResource: sinon.stub().returns(false),
            createDebugConfiguration: sinon.stub().rejects(new Error('first provider should not configure')),
        });
        const secondProvider = createProvider({
            canAttachToResource: sinon.stub().returns(true),
            createDebugConfiguration: sinon.stub().resolves({
                type: 'coreclr',
                request: 'attach',
                name: 'Attach debugger: second provider',
            }),
        });
        const startDebugging = sinon.stub().resolves(true);
        const { service, sessions } = createService({
            providers: [firstProvider, secondProvider],
            startDebugging,
        });

        try {
            assert.strictEqual(service.canAttachToResource(createResource()), false);
            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'unsupportedResource' });
            assert.strictEqual((firstProvider.canAttachToResource as sinon.SinonStub).callCount, 2);
            assert.strictEqual((firstProvider.createDebugConfiguration as sinon.SinonStub).callCount, 0);
            assert.strictEqual((secondProvider.canAttachToResource as sinon.SinonStub).callCount, 0);
            assert.strictEqual((secondProvider.createDebugConfiguration as sinon.SinonStub).callCount, 0);
            assert.strictEqual(startDebugging.callCount, 0);
        }
        finally {
            sessions.dispose();
        }
    });

    test('uses a fresh AppHost snapshot instead of a tree resource', async () => {
        let fetchCount = 0;
        let configuredResource: ResourceDebugResourceSnapshot | undefined;
        const repository: ResourceDebugAppHostRepository = {
            fetchRunningAppHostsOnce: async () => {
                return [createAppHost({ resources: null })];
            },
            fetchAppHostResourcesOnce: async () => {
                fetchCount++;
                return [createResource({
                    properties: {
                        'project.path': '/repo/api/Api.csproj',
                        'executable.path': `dotnet-${fetchCount}`,
                    },
                })];
            },
        };
        const provider = createProvider({
            createDebugConfiguration: async resource => {
                configuredResource = resource;
                return { type: 'coreclr', request: 'attach', name: 'Attach debugger: API' };
            },
        });
        const events = new TestDebugSessionEvents();
        const sessions = new ResourceDebugSessionRegistry(events);
        const service = new ResourceDebugService({
            appHostRepository: repository,
            attachProviders: new ResourceAttachProviderRegistry([provider], () => true),
            sessionRegistry: sessions,
            startDebugging: async () => true,
        });

        const result = await service.debug(createRequest());

        assert.deepStrictEqual(result, { outcome: 'started', providerId: 'dotnet' });
        assert.strictEqual(fetchCount, 1);
        assert.strictEqual(configuredResource?.properties?.['executable.path'], 'dotnet-1');
        sessions.dispose();
    });

    test('resolves the running AppHost before fetching only its resource snapshot', async () => {
        const cancellation = new vscode.CancellationTokenSource();
        const fetchedPaths: string[] = [];
        const receivedTokens: Array<vscode.CancellationToken | undefined> = [];
        const repository: ResourceDebugAppHostRepository = {
            fetchRunningAppHostsOnce: async token => {
                assert.strictEqual(token, cancellation.token);
                return [
                    createAppHost({ appHostPath: '/repo/other/AppHost.csproj', resources: null }),
                    createAppHost({ appHostPath: '/repo/resolved/AppHost.csproj', resources: null }),
                ];
            },
            fetchAppHostResourcesOnce: async (appHostPath, token) => {
                fetchedPaths.push(appHostPath);
                receivedTokens.push(token);
                return [createResource()];
            },
        };
        const events = new TestDebugSessionEvents();
        const sessions = new ResourceDebugSessionRegistry(events);
        const service = new ResourceDebugService({
            appHostRepository: repository,
            attachProviders: new ResourceAttachProviderRegistry([createProvider()], () => true),
            sessionRegistry: sessions,
            startDebugging: async () => true,
            compareAppHostIdentity: (requestedPath, appHostPath) =>
                requestedPath === '/repo/alias/AppHost.csproj' && appHostPath === '/repo/resolved/AppHost.csproj'
                    ? 'same'
                    : 'different',
        });

        try {
            const result = await service.debug(createRequest({
                appHost: { absolutePath: '/repo/alias/AppHost.csproj', displayPath: 'alias/AppHost.csproj' },
                cancellationToken: cancellation.token,
            }));

            assert.deepStrictEqual(result, { outcome: 'started', providerId: 'dotnet' });
            assert.deepStrictEqual(fetchedPaths, ['/repo/resolved/AppHost.csproj']);
            assert.deepStrictEqual(receivedTokens, [cancellation.token]);
        }
        finally {
            cancellation.dispose();
            sessions.dispose();
        }
    });

    test('returns a snapshot failure when the selected AppHost cannot be described', async () => {
        const logError = sinon.stub(extensionLogOutputChannel, 'error');
        const { service, sessions, repository } = createService();
        repository.fetchAppHostResourcesOnce = async () => {
            throw new Error('process 1234 at /repo/private/AppHost.csproj');
        };

        try {
            const result = await service.debug(createRequest());

            assert.deepStrictEqual(result, { outcome: 'error', errorKind: 'resourceSnapshotFailed' });
            assert.doesNotMatch(JSON.stringify(result), /1234|\/repo|AppHost\.csproj/);
            assert.ok(logError.calledOnce);
        }
        finally {
            sessions.dispose();
        }
    });

    test('resolves duplicate resource names only within the requested AppHost', async () => {
        let configuredResource: ResourceDebugResourceSnapshot | undefined;
        const { service, sessions } = createService({
            appHosts: [
                createAppHost({
                    appHostPath: '/repo/first/AppHost.csproj',
                    resources: [createResource({ displayName: 'First API' })],
                }),
                createAppHost({
                    appHostPath: target.absolutePath,
                    resources: [createResource({ displayName: 'Second API' })],
                }),
            ],
            provider: createProvider({
                createDebugConfiguration: async resource => {
                    configuredResource = resource;
                    return { type: 'coreclr', request: 'attach', name: 'Attach debugger: Second API' };
                },
            }),
        });

        const result = await service.debug(createRequest());

        assert.deepStrictEqual(result, { outcome: 'started', providerId: 'dotnet' });
        assert.strictEqual(configuredResource?.displayName, 'Second API');
        sessions.dispose();
    });

    test('keeps a typed configuration failure when the provider rejects an unattached resource', async () => {
        const logError = sinon.stub(extensionLogOutputChannel, 'error');
        const { service, sessions } = createService({
            provider: createProvider({
                createDebugConfiguration: async () => {
                    throw new ResourceAttachConfigurationError('resourceNotAttachable', 'process 1234 at /repo/private/Api.dll');
                },
            }),
        });

        try {
            const result = await service.debug(createRequest());

            assert.deepStrictEqual(result, { outcome: 'error', errorKind: 'configurationFailed' });
            assert.doesNotMatch(JSON.stringify(result), /1234|\/repo|Api\.dll/);
            assert.ok(logError.calledOnce);
        }
        finally {
            sessions.dispose();
        }
    });

    test('fails closed when the AppHost identity is ambiguous', async () => {
        const { service, sessions } = createService({
            compareAppHostIdentity: () => 'ambiguous',
        });

        assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'appHostNotFound' });
        sessions.dispose();
    });

    test('fails closed when one matching AppHost identity is ambiguous', async () => {
        const { service, sessions } = createService({
            appHosts: [
                createAppHost(),
                createAppHost({ appHostPath: '/repo/ambiguous/AppHost.csproj' }),
            ],
            compareAppHostIdentity: (_requestedPath, appHostPath) =>
                appHostPath === target.absolutePath ? 'same' : 'ambiguous',
        });

        assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'appHostNotFound' });
        sessions.dispose();
    });

    test('resolves the selected AppHost process when one path has overlapping snapshots', async () => {
        const appHosts = [
            createAppHost({ appHostPid: 1111 }),
            createAppHost({ appHostPid: 2222 }),
        ];
        const { service, sessions } = createService({ appHosts });

        assert.deepStrictEqual(await service.debug(createRequest({
            appHost: {
                ...target,
                appHostPid: 2222,
            },
        })), { outcome: 'started', providerId: 'dotnet' });
        sessions.dispose();
    });

    test('rejects an AppHost process that no longer matches the selected tree item', async () => {
        const { service, sessions } = createService({
            appHosts: [createAppHost({ appHostPid: 2222 })],
        });

        assert.deepStrictEqual(await service.debug(createRequest({
            appHost: {
                ...target,
                appHostPid: 1111,
            },
        })), { outcome: 'appHostNotFound' });
        sessions.dispose();
    });

    test('fails closed when a resource is stale or duplicated', async () => {
        const missing = createService({
            appHosts: [createAppHost({ resources: [] })],
        });
        const duplicated = createService({
            appHosts: [createAppHost({ resources: [createResource(), createResource()] })],
        });

        assert.deepStrictEqual(await missing.service.debug(createRequest()), { outcome: 'resourceNotFound' });
        assert.deepStrictEqual(await duplicated.service.debug(createRequest()), { outcome: 'resourceNotFound' });
        missing.sessions.dispose();
        duplicated.sessions.dispose();
    });

    test('reports a missing debugger extension without exposing resource details', async () => {
        const { service, sessions } = createService({
            isExtensionInstalled: () => false,
        });

        assert.deepStrictEqual(await service.debug(createRequest()), {
            outcome: 'debuggerExtensionMissing',
            debuggerExtensions: [{ id: 'ms-dotnettools.csharp', label: 'C#' }],
        });
        sessions.dispose();
    });

    test('returns alreadyDebugging when Aspire already owns the reported resource process', async () => {
        const startDebugging = sinon.stub().resolves(true);
        const { service, sessions } = createService({
            appHosts: [createAppHost({
                resources: [createResource({
                    properties: {
                        'project.path': '/repo/api/Api.csproj',
                        'executable.path': 'dotnet',
                        'executable.pid': 4242,
                    } as unknown as ResourceJson['properties'],
                })],
            })],
            isProcessAlreadyDebugged: processId => processId === 4242,
            startDebugging,
        });

        assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'alreadyDebugging' });
        assert.strictEqual(startDebugging.called, false);
        sessions.dispose();
    });

    test('reports the missing Go debugger extension using only its requirement metadata', async () => {
        const resolver = {
            resolveApplicationPid: sinon.stub().rejects(new Error('/private/go-build123/b001/exe/api 4567')),
        };
        const { service, sessions } = createService({
            appHosts: [createAppHost({ resources: [createGoResource()] })],
            provider: createGoResourceAttachProvider(resolver),
            isExtensionInstalled: () => false,
        });

        try {
            assert.strictEqual(service.canAttachToResource(createGoResource()), true);
            const result = await service.debug(createRequest());

            assert.deepStrictEqual(result, {
                outcome: 'debuggerExtensionMissing',
                debuggerExtensions: [{ id: 'golang.go', label: 'Go' }],
            });
            assert.doesNotMatch(JSON.stringify(result), /1234|4567|go-build|\/private/);
            assert.strictEqual(resolver.resolveApplicationPid.called, false);
        }
        finally {
            sessions.dispose();
        }
    });

    test('normalizes Go process discovery failures without exposing process details', async () => {
        const resolver = {
            resolveApplicationPid: async () => {
                throw new Error('/private/go-build123/b001/exe/api --port 8080 4567');
            },
        };
        const { service, sessions } = createService({
            appHosts: [createAppHost({ resources: [createGoResource()] })],
            provider: createGoResourceAttachProvider(resolver),
        });

        try {
            const result = await service.debug(createRequest());

            assert.deepStrictEqual(result, { outcome: 'error', errorKind: 'configurationFailed' });
            assert.doesNotMatch(JSON.stringify(result), /1234|4567|go-build|\/private|8080/);
        }
        finally {
            sessions.dispose();
        }
    });

    test('checks attach eligibility before reporting a missing debugger extension', async () => {
        const { service, sessions } = createService({
            provider: createProvider({ canAttachToResource: () => false }),
            isExtensionInstalled: () => false,
        });

        assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'unsupportedResource' });
        sessions.dispose();
    });

    test('returns typed outcomes for unsupported and stopped resources', async () => {
        const unsupported = createService({
            provider: createProvider({ canAttachToResource: () => false }),
        });
        const stopped = createService({
            appHosts: [createAppHost({ resources: [createResource({ state: 'Finished' })] })],
            provider: createProvider({ canAttachToResource: () => false }),
        });

        assert.deepStrictEqual(await unsupported.service.debug(createRequest()), { outcome: 'unsupportedResource' });
        assert.deepStrictEqual(await stopped.service.debug(createRequest()), { outcome: 'resourceNotRunning' });
        unsupported.sessions.dispose();
        stopped.sessions.dispose();
    });

    test('recognizes stopped .NET resources before checking attach readiness', async () => {
        const stopped = createService({
            appHosts: [createAppHost({ resources: [createResource({ state: 'Finished' })] })],
            provider: projectResourceAttachProvider,
        });

        assert.deepStrictEqual(await stopped.service.debug(createRequest()), { outcome: 'resourceNotRunning' });
        stopped.sessions.dispose();
    });

    test('normalizes provider eligibility errors without exposing their details', async () => {
        const { service, sessions } = createService({
            provider: createProvider({
                canAttachToResource: () => {
                    throw new Error('process 1234 at /repo/private/Api.dll');
                },
            }),
        });

        const result = await service.debug(createRequest());

        assert.deepStrictEqual(result, { outcome: 'error', errorKind: 'providerResolutionFailed' });
        assert.doesNotMatch(JSON.stringify(result), /1234|\/repo|Api\.dll/);
        sessions.dispose();
    });

    test('serializes concurrent requests and returns alreadyDebugging for the duplicate', async () => {
        let completeStart: ((value: boolean) => void) | undefined;
        let markStartCalled: (() => void) | undefined;
        const startRequest = new Promise<boolean>(resolve => {
            completeStart = resolve;
        });
        const startCalled = new Promise<void>(resolve => {
            markStartCalled = resolve;
        });
        const startDebugging = sinon.stub().callsFake(() => {
            markStartCalled!();
            return startRequest;
        });
        const { service, repository, sessions } = createService({ startDebugging });
        let fetchCount = 0;
        repository.fetchRunningAppHostsOnce = async () => {
            fetchCount++;
            return [createAppHost()];
        };

        const first = service.debug(createRequest());
        const second = service.debug(createRequest());
        await startCalled;
        assert.strictEqual(startDebugging.callCount, 1);
        assert.strictEqual(fetchCount, 2);

        completeStart!(true);

        assert.deepStrictEqual(await first, { outcome: 'started', providerId: 'dotnet' });
        assert.deepStrictEqual(await second, { outcome: 'alreadyDebugging' });
        sessions.dispose();
    });

    test('cancels a request while it waits for the resource lock', async () => {
        let completeStart: ((value: boolean) => void) | undefined;
        let signalStart: (() => void) | undefined;
        let signalSecondIdentityFetch: (() => void) | undefined;
        const startRequest = new Promise<boolean>(resolve => {
            completeStart = resolve;
        });
        const startCalled = new Promise<void>(resolve => {
            signalStart = resolve;
        });
        const secondIdentityFetch = new Promise<void>(resolve => {
            signalSecondIdentityFetch = resolve;
        });
        const startDebugging = sinon.stub().callsFake(() => {
            signalStart!();
            return startRequest;
        });
        const { service, repository, sessions } = createService({ startDebugging });
        let identityFetchCount = 0;
        let resourceSnapshotCount = 0;
        repository.fetchRunningAppHostsOnce = async () => {
            identityFetchCount++;
            if (identityFetchCount === 2) {
                signalSecondIdentityFetch!();
            }
            return [createAppHost({ resources: null })];
        };
        repository.fetchAppHostResourcesOnce = async () => {
            resourceSnapshotCount++;
            return [createResource()];
        };
        const cancellation = new vscode.CancellationTokenSource();

        try {
            const first = service.debug(createRequest());
            await startCalled;

            const second = service.debug(createRequest({ cancellationToken: cancellation.token }));
            await secondIdentityFetch;
            cancellation.cancel();

            assert.deepStrictEqual(await second, { outcome: 'cancelled' });
            assert.strictEqual(resourceSnapshotCount, 1);

            completeStart!(true);
            assert.deepStrictEqual(await first, { outcome: 'started', providerId: 'dotnet' });
        }
        finally {
            cancellation.dispose();
            sessions.dispose();
        }
    });

    test('keeps a later request blocked when a canceled waiter has already completed', async () => {
        const sessions = new ResourceDebugSessionRegistry();
        let releaseFirst: (() => void) | undefined;
        let firstEntered: (() => void) | undefined;
        let firstCompleted = false;
        const firstCanComplete = new Promise<void>(resolve => {
            releaseFirst = resolve;
        });
        const firstHasEntered = new Promise<void>(resolve => {
            firstEntered = resolve;
        });
        const cancellation = new vscode.CancellationTokenSource();
        let laterWaiterStarted = false;

        try {
            const first = sessions.runSerialized(
                target,
                'api',
                undefined,
                async () => {
                    firstEntered!();
                    await firstCanComplete;
                    firstCompleted = true;
                    return 'first';
                },
                () => 'cancelled');
            await firstHasEntered;

            const canceledWaiter = sessions.runSerialized(
                target,
                'api',
                cancellation.token,
                async () => 'second',
                () => 'cancelled');

            cancellation.cancel();

            assert.strictEqual(await canceledWaiter, 'cancelled');
            assert.strictEqual(firstCompleted, false);

            const laterWaiter = sessions.runSerialized(
                target,
                'api',
                undefined,
                async () => {
                    laterWaiterStarted = true;
                    return 'third';
                },
                () => 'cancelled');
            await new Promise<void>(resolve => setImmediate(resolve));
            assert.strictEqual(laterWaiterStarted, false);

            releaseFirst!();
            assert.strictEqual(await first, 'first');
            assert.strictEqual(await laterWaiter, 'third');
        }
        finally {
            cancellation.dispose();
            sessions.dispose();
        }
    });

    test('passes the request cancellation token to providers that support cancellation', async () => {
        const cancellation = new vscode.CancellationTokenSource();
        let receivedToken: vscode.CancellationToken | undefined;
        const { service, sessions } = createService({
            provider: createProvider({
                createDebugConfiguration: async (_resource, token) => {
                    receivedToken = token;
                    return { type: 'coreclr', request: 'attach', name: 'Attach debugger: API' };
                },
            }),
        });

        try {
            assert.deepStrictEqual(await service.debug(createRequest({ cancellationToken: cancellation.token })), {
                outcome: 'started',
                providerId: 'dotnet',
            });
            assert.strictEqual(receivedToken, cancellation.token);
        }
        finally {
            cancellation.dispose();
            sessions.dispose();
        }
    });

    test('returns cancelled when .NET target discovery observes request cancellation', async () => {
        let receivedToken: vscode.CancellationToken | undefined;
        let signalTargetDiscoveryStarted: (() => void) | undefined;
        const targetDiscoveryStarted = new Promise<void>(resolve => {
            signalTargetDiscoveryStarted = resolve;
        });
        const provider = createProjectResourceAttachProvider(() => ({
            getAndActivateDevKit: async () => false,
            buildDotNetProject: async () => { },
            getDotNetAttachTargetInfo: async (
                _projectFile: string,
                _configuration: string | undefined,
                cancellationToken: vscode.CancellationToken | undefined) => {
                receivedToken = cancellationToken;
                signalTargetDiscoveryStarted!();
                return await new Promise<never>((_resolve, reject) => {
                    cancellationToken?.onCancellationRequested(() => reject(new vscode.CancellationError()));
                });
            },
            getDotNetTargetPath: async () => '',
            getDotNetRunApiOutput: async () => '',
        } as never));
        const cancellation = new vscode.CancellationTokenSource();
        const startDebugging = sinon.stub().resolves(true);
        const { service, sessions } = createService({
            appHosts: [createAppHost({
                resources: [createResource({
                    properties: {
                        'project.path': '/repo/api/Api.csproj',
                        'executable.path': 'dotnet',
                        'executable.pid': '42',
                    },
                })],
            })],
            provider,
            startDebugging,
        });

        try {
            const operation = service.debug(createRequest({ cancellationToken: cancellation.token }));
            await targetDiscoveryStarted;
            cancellation.cancel();

            const result = await Promise.race([
                operation,
                new Promise<'timedOut'>(resolve => setTimeout(() => resolve('timedOut'), 100)),
            ]);
            assert.deepStrictEqual(result, { outcome: 'cancelled' });
            assert.strictEqual(receivedToken, cancellation.token);
            assert.strictEqual(startDebugging.callCount, 0);
        }
        finally {
            cancellation.dispose();
            sessions.dispose();
        }
    });

    test('returns alreadyDebugging while an independent attach session is active', async () => {
        const { service, sessions } = createService();

        assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
        assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'alreadyDebugging' });
        sessions.dispose();
    });

    test('logs marker-loss expiry before allowing a recovery attach attempt', async () => {
        const clock = sinon.useFakeTimers();
        const logWarning = sinon.stub(extensionLogOutputChannel, 'warn');
        const events = new TestDebugSessionEvents();
        const sessions = new ResourceDebugSessionRegistry(events, { pendingStartTimeoutMs: 100 });
        const service = new ResourceDebugService({
            appHostRepository: {
                fetchRunningAppHostsOnce: async () => [createAppHost({ resources: null })],
                fetchAppHostResourcesOnce: async () => [createResource()],
            },
            attachProviders: new ResourceAttachProviderRegistry([createProvider()], () => true),
            sessionRegistry: sessions,
            startDebugging: async () => true,
        });

        try {
            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });

            // A third-party debug configuration provider can resolve the session without preserving
            // private properties from the launch configuration.
            events.start({ type: 'coreclr', request: 'attach', name: 'Attach debugger: API' });
            await clock.tickAsync(100);

            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.ok(logWarning.calledOnceWithExactly(
                'Resource debugger session tracking expired before its debug session reported the private marker. A later attach may start another session.'));
        }
        finally {
            sessions.dispose();
            clock.restore();
        }
    });

    test('keeps an accepted start active when a correlated independent session starts', async () => {
        const clock = sinon.useFakeTimers();
        const events = new TestDebugSessionEvents();
        const sessions = new ResourceDebugSessionRegistry(events, { pendingStartTimeoutMs: 100 });
        let startedConfiguration: vscode.DebugConfiguration | undefined;
        const service = new ResourceDebugService({
            appHostRepository: {
                fetchRunningAppHostsOnce: async () => [createAppHost({ resources: null })],
                fetchAppHostResourcesOnce: async () => [createResource()],
            },
            attachProviders: new ResourceAttachProviderRegistry([createProvider()], () => true),
            sessionRegistry: sessions,
            startDebugging: async (_folder, configuration) => {
                startedConfiguration = configuration;
                events.start(configuration);
                return true;
            },
        });

        try {
            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.ok(startedConfiguration);
            await clock.tickAsync(100);

            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'alreadyDebugging' });
        }
        finally {
            sessions.dispose();
            clock.restore();
        }
    });

    test('does not reactivate an attempt terminated before start acceptance', () => {
        const events = new TestDebugSessionEvents();
        const sessions = new ResourceDebugSessionRegistry(events);
        const attempt = sessions.createAttempt(target, 'api', {
            type: 'coreclr',
            request: 'attach',
            name: 'Attach debugger: API',
        }, {
            source: 'tree',
            provider: 'dotnet',
            resource_type: 'project',
            requested_strategy: 'attach',
            effective_strategy: 'attach',
        });

        try {
            events.terminate(attempt.configuration);
            attempt.markStarted();

            assert.strictEqual(sessions.hasActiveSession(target, 'api'), false);
        }
        finally {
            sessions.dispose();
        }
    });

    test('tracks attach sessions separately for overlapping AppHost processes', () => {
        const events = new TestDebugSessionEvents();
        const sessions = new ResourceDebugSessionRegistry(events);
        const firstTarget = {
            ...target,
            appHostPid: 1111,
        };
        const secondTarget = {
            ...target,
            appHostPid: 2222,
        };
        const attempt = sessions.createAttempt(firstTarget, 'api', {
            type: 'coreclr',
            request: 'attach',
            name: 'Attach debugger: API',
        }, {
            source: 'tree',
            provider: 'dotnet',
            resource_type: 'project',
            requested_strategy: 'attach',
            effective_strategy: 'attach',
        });

        try {
            attempt.markStarted();

            assert.strictEqual(sessions.hasActiveSession(firstTarget, 'api'), true);
            assert.strictEqual(sessions.hasActiveSession(secondTarget, 'api'), false);
        }
        finally {
            sessions.dispose();
        }
    });

    test('serializes aliases that resolve to the same running AppHost', async () => {
        let completeStart: ((value: boolean) => void) | undefined;
        let signalStart: (() => void) | undefined;
        const startRequest = new Promise<boolean>(resolve => {
            completeStart = resolve;
        });
        const startCalled = new Promise<void>(resolve => {
            signalStart = resolve;
        });
        const startDebugging = sinon.stub().callsFake(() => {
            signalStart!();
            return startRequest;
        });
        const { service, sessions } = createService({
            startDebugging,
            compareAppHostIdentity: () => 'same',
            appHosts: [createAppHost({ appHostPath: '/repo/resolved/AppHost.csproj' })],
        });
        const first = service.debug(createRequest({
            appHost: { absolutePath: '/repo/alias-one/AppHost.csproj', displayPath: 'alias-one/AppHost.csproj' },
        }));
        const second = service.debug(createRequest({
            appHost: { absolutePath: '/repo/alias-two/AppHost.csproj', displayPath: 'alias-two/AppHost.csproj' },
        }));

        await startCalled;
        assert.strictEqual(startDebugging.callCount, 1);

        completeStart!(true);

        assert.deepStrictEqual(await first, { outcome: 'started', providerId: 'dotnet' });
        assert.deepStrictEqual(await second, { outcome: 'alreadyDebugging' });
        sessions.dispose();
    });

    test('returns a bounded failure when VS Code declines to start debugging', async () => {
        const { service, sessions } = createService({
            startDebugging: async () => false,
        });

        assert.deepStrictEqual(await service.debug(createRequest()), {
            outcome: 'error',
            errorKind: 'debuggerStartDeclined',
        });
        sessions.dispose();
    });

    test('normalizes configuration errors without exposing their details', async () => {
        const { service, sessions } = createService({
            provider: createProvider({
                createDebugConfiguration: async () => {
                    throw new Error('process 1234 at /repo/private/Api.dll');
                },
            }),
        });

        const result = await service.debug(createRequest());

        assert.deepStrictEqual(result, { outcome: 'error', errorKind: 'configurationFailed' });
        assert.doesNotMatch(JSON.stringify(result), /1234|\/repo|Api\.dll/);
        sessions.dispose();
    });

    test('normalizes unexpected service errors while logging their raw details internally', async () => {
        const rawError = 'process 1234 at /repo/private/AppHost.csproj';
        const logError = sinon.stub(extensionLogOutputChannel, 'error');
        const { service, sessions } = createService({
            compareAppHostIdentity: () => {
                throw new Error(rawError);
            },
        });

        try {
            const result = await service.debug(createRequest());

            assert.deepStrictEqual(result, { outcome: 'error', errorKind: 'unexpected' });
            assert.doesNotMatch(JSON.stringify(result), /1234|\/repo|AppHost\.csproj/);
            assert.ok(logError.calledWithMatch(rawError));
        }
        finally {
            sessions.dispose();
        }
    });

    test('returns cancelled when the request cancellation token is already cancelled', async () => {
        const cancellation = new vscode.CancellationTokenSource();
        cancellation.cancel();
        const startDebugging = sinon.stub().resolves(true);
        const { service, sessions } = createService({ startDebugging });

        assert.deepStrictEqual(await service.debug(createRequest({ cancellationToken: cancellation.token })), {
            outcome: 'cancelled',
        });
        assert.strictEqual(startDebugging.callCount, 0);
        cancellation.dispose();
        sessions.dispose();
    });

    test('does not start debugging when cancellation occurs during configuration', async () => {
        let finishConfiguration: (() => void) | undefined;
        let markConfigurationStarted: (() => void) | undefined;
        const configuration = new Promise<void>(resolve => {
            finishConfiguration = resolve;
        });
        const configurationStarted = new Promise<void>(resolve => {
            markConfigurationStarted = resolve;
        });
        const cancellation = new vscode.CancellationTokenSource();
        const startDebugging = sinon.stub().resolves(true);
        const { service, sessions } = createService({
            provider: createProvider({
                createDebugConfiguration: async () => {
                    markConfigurationStarted!();
                    await configuration;
                    return { type: 'coreclr', request: 'attach', name: 'Attach debugger: API' };
                },
            }),
            startDebugging,
        });

        const operation = service.debug(createRequest({ cancellationToken: cancellation.token }));
        await configurationStarted;
        cancellation.cancel();
        finishConfiguration!();

        assert.deepStrictEqual(await operation, { outcome: 'cancelled' });
        assert.strictEqual(startDebugging.callCount, 0);
        cancellation.dispose();
        sessions.dispose();
    });

    test('does not start debugging when cancellation occurs during the fresh resource snapshot', async () => {
        let finishSnapshot: (() => void) | undefined;
        let markSnapshotStarted: (() => void) | undefined;
        const snapshot = new Promise<void>(resolve => {
            finishSnapshot = resolve;
        });
        const snapshotStarted = new Promise<void>(resolve => {
            markSnapshotStarted = resolve;
        });
        const cancellation = new vscode.CancellationTokenSource();
        const startDebugging = sinon.stub().resolves(true);
        const { service, repository, sessions } = createService({ startDebugging });
        repository.fetchAppHostResourcesOnce = async () => {
            markSnapshotStarted!();
            await snapshot;
            return [createResource()];
        };

        try {
            const operation = service.debug(createRequest({ cancellationToken: cancellation.token }));
            await snapshotStarted;
            cancellation.cancel();
            finishSnapshot!();

            assert.deepStrictEqual(await operation, { outcome: 'cancelled' });
            assert.strictEqual(startDebugging.callCount, 0);
        }
        finally {
            cancellation.dispose();
            sessions.dispose();
        }
    });

    test('keeps an accepted attach session when cancellation arrives after debugging starts', async () => {
        const cancellation = new vscode.CancellationTokenSource();
        let startedConfiguration: vscode.DebugConfiguration | undefined;
        const { service, sessions, events } = createService({
            startDebugging: async (_folder, configuration) => {
                startedConfiguration = configuration;
                events.start(configuration);
                cancellation.cancel();
                return true;
            },
        });

        try {
            assert.deepStrictEqual(
                await service.debug(createRequest({ cancellationToken: cancellation.token })),
                { outcome: 'started', providerId: 'dotnet' });
            assert.ok(startedConfiguration);
            assert.strictEqual(sessions.hasActiveSession(resolvedTarget, 'api'), true);
        }
        finally {
            cancellation.dispose();
            sessions.dispose();
        }
    });

    test('removes a terminated independent attach session without stopping its resource', async () => {
        let startedConfiguration: vscode.DebugConfiguration | undefined;
        const { service, sessions, events } = createService({
            startDebugging: async (_folder, configuration) => {
                startedConfiguration = configuration;
                events.start(configuration);
                return true;
            },
        });

        assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
        assert.ok(startedConfiguration);
        assert.strictEqual(sessions.hasActiveSession(resolvedTarget, 'api'), true);

        events.terminate(startedConfiguration!);

        assert.strictEqual(sessions.hasActiveSession(resolvedTarget, 'api'), false);
        sessions.dispose();
    });

    test('publishes attach session start and termination changes to tree consumers', async () => {
        const { service, sessions, events } = createService({
            startDebugging: async (_folder, configuration) => {
                events.start(configuration);
                return true;
            },
        });
        const lifecycle = service.onDidChangeDebugSessions;
        let changeCount = 0;
        const subscription = lifecycle(() => changeCount++);

        try {
            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.strictEqual(changeCount, 1);
            assert.ok(events.startedConfiguration);

            events.terminate(events.startedConfiguration);

            assert.strictEqual(changeCount, 2);
        }
        finally {
            subscription.dispose();
            sessions.dispose();
        }
    });

    test('emits a bounded start and success result with deterministic durations', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        telemetry.currentTime = 100;
        const { service, sessions } = createService({
            telemetry,
            provider: createProvider({
                createDebugConfiguration: async () => {
                    telemetry.currentTime = 105;
                    return { type: 'coreclr', request: 'attach', name: 'Attach debugger: API' };
                },
            }),
            startDebugging: async () => {
                telemetry.currentTime = 108;
                return true;
            },
        });

        try {
            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.deepStrictEqual(telemetry.events, [
                {
                    name: 'aspire/vscode/resourcedebug/start',
                    properties: {
                        source: 'tree',
                        requested_strategy: 'attach',
                        controller: 'editor',
                    },
                    measurements: undefined,
                },
                {
                    name: 'aspire/vscode/resourcedebug/result',
                    properties: {
                        source: 'tree',
                        provider: 'dotnet',
                        resource_type: 'project',
                        requested_strategy: 'attach',
                        effective_strategy: 'attach',
                        outcome: 'started',
                        controller: 'editor',
                        state: 'running',
                        debugger_requirement: 'installed',
                        error_kind: 'none',
                    },
                    measurements: {
                        resolution_duration_ms: 5,
                        debug_start_duration_ms: 3,
                        total_duration_ms: 8,
                    },
                },
            ]);
        }
        finally {
            sessions.dispose();
        }
    });

    test('selects attach centrally for the auto strategy and records the requested strategy', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        const { service, sessions } = createService({ telemetry });

        try {
            assert.deepStrictEqual(
                await service.debug(createRequest({ source: 'languageModelTool', strategy: 'auto' })),
                { outcome: 'started', providerId: 'dotnet' });
            assert.deepStrictEqual(
                telemetry.events.map(event => ({
                    name: event.name,
                    requestedStrategy: event.properties.requested_strategy,
                    effectiveStrategy: event.properties.effective_strategy,
                })),
                [
                    {
                        name: 'aspire/vscode/resourcedebug/start',
                        requestedStrategy: 'auto',
                        effectiveStrategy: undefined,
                    },
                    {
                        name: 'aspire/vscode/resourcedebug/result',
                        requestedStrategy: 'auto',
                        effectiveStrategy: 'attach',
                    },
                ]);
        }
        finally {
            sessions.dispose();
        }
    });

    test('fails closed when a caller bypasses the bounded debug strategy contract', async () => {
        const startDebugging = sinon.stub().resolves(true);
        const telemetry = new TestResourceDebugTelemetry();
        const { service, sessions } = createService({ startDebugging, telemetry });

        try {
            assert.deepStrictEqual(
                await service.debug(createRequest({ strategy: 'restart' as never })),
                { outcome: 'error', errorKind: 'unexpected' });
            assert.strictEqual(startDebugging.callCount, 0);
            assert.deepStrictEqual(
                telemetry.events.map(event => ({
                    name: event.name,
                    requestedStrategy: event.properties.requested_strategy,
                    effectiveStrategy: event.properties.effective_strategy,
                })),
                [
                    {
                        name: 'aspire/vscode/resourcedebug/start',
                        requestedStrategy: 'invalid',
                        effectiveStrategy: undefined,
                    },
                    {
                        name: 'aspire/vscode/resourcedebug/result',
                        requestedStrategy: 'invalid',
                        effectiveStrategy: 'none',
                    },
                ]);
        }
        finally {
            sessions.dispose();
        }
    });

    test('emits exactly one bounded result for every resource debug outcome', async () => {
        const run = async (
            create: () => {
                service: ResourceDebugService;
                sessions: ResourceDebugSessionRegistry;
                telemetry: TestResourceDebugTelemetry;
            },
            expectedOutcome: ResourceDebugResult['outcome'],
            expectedErrorKind = 'none',
        ) => {
            const { service, sessions, telemetry } = create();
            try {
                const result = await service.debug(createRequest());
                assert.strictEqual(result.outcome, expectedOutcome);

                const startEvents = telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/start');
                const resultEvents = telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/result');
                assert.strictEqual(startEvents.length, 1);
                assert.strictEqual(resultEvents.length, 1);
                assert.strictEqual(resultEvents[0].properties.outcome, expectedOutcome);
                assert.strictEqual(resultEvents[0].properties.error_kind, expectedErrorKind);
            }
            finally {
                sessions.dispose();
            }
        };

        const cancelled = new vscode.CancellationTokenSource();
        cancelled.cancel();
        await run(
            () => createService(),
            'started');
        await run(
            () => createService({
                appHosts: [],
            }),
            'appHostNotFound');
        await run(
            () => createService({
                appHosts: [createAppHost({ resources: [] })],
            }),
            'resourceNotFound');
        await run(
            () => createService({
                provider: createProvider({ canAttachToResource: () => false }),
            }),
            'unsupportedResource');
        await run(
            () => createService({
                appHosts: [createAppHost({ resources: [createResource({ state: 'Finished' })] })],
            }),
            'resourceNotRunning');
        await run(
            () => createService({
                isExtensionInstalled: () => false,
            }),
            'debuggerExtensionMissing');
        await run(
            () => createService({
                provider: createProvider({
                    createDebugConfiguration: async () => {
                        throw new Error('raw configuration error');
                    },
                }),
            }),
            'error',
            'configurationFailed');
        await run(
            () => createService({
                startDebugging: async () => false,
            }),
            'error',
            'debuggerStartDeclined');
        await run(
            () => createService({
                startDebugging: async () => {
                    throw new Error('raw debugger failure');
                },
            }),
            'error',
            'debuggerStartFailed');
        await run(
            () => {
                const fixture = createService();
                fixture.repository.fetchRunningAppHostsOnce = async () => {
                    throw new Error('raw AppHost snapshot failure');
                };
                return fixture;
            },
            'error',
            'resourceSnapshotFailed');
        await run(
            () => createService({
                provider: createProvider({
                    canRecognizeResource: () => {
                        throw new Error('raw provider resolution failure');
                    },
                }),
            }),
            'error',
            'providerResolutionFailed');
        await run(
            () => createService({
                compareAppHostIdentity: () => {
                    throw new Error('raw unexpected comparison failure');
                },
            }),
            'error',
            'unexpected');

        const cancelledFixture = createService();
        try {
            const result = await cancelledFixture.service.debug(createRequest({ cancellationToken: cancelled.token }));
            assert.deepStrictEqual(result, { outcome: 'cancelled' });
            assert.strictEqual(cancelledFixture.telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/start').length, 1);
            assert.strictEqual(cancelledFixture.telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/result').length, 1);
            assert.strictEqual(
                cancelledFixture.telemetry.events.find(event => event.name === 'aspire/vscode/resourcedebug/result')?.properties.error_kind,
                'none');
        }
        finally {
            cancelled.dispose();
            cancelledFixture.sessions.dispose();
        }

        const duplicate = createService();
        try {
            assert.deepStrictEqual(await duplicate.service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.deepStrictEqual(await duplicate.service.debug(createRequest()), { outcome: 'alreadyDebugging' });
            const resultEvents = duplicate.telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/result');
            assert.strictEqual(resultEvents.length, 2);
            assert.deepStrictEqual(resultEvents.map(event => event.properties.outcome), ['started', 'alreadyDebugging']);
        }
        finally {
            duplicate.sessions.dispose();
        }
    });

    test('emits an exact private-data-free payload without correlation identifiers', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        telemetry.currentTime = 50;
        const secrets = [
            '/Users/example/Private Workspace/Secret AppHost.csproj',
            'Secret AppHost.csproj',
            'private-resource-name',
            'Private Resource Display Name',
            '54321',
            'session-secret-marker',
            '/opt/private/bin/secret-process --api-key very-secret',
            'https://private.example.test/dashboard?token=very-secret',
            'PRIVATE_ENVIRONMENT_VARIABLE',
            '--private-argument',
            'private-property-value',
            'private.debugger.extension',
            'raw configuration error with stack trace',
        ];
        const privateTarget: ResourceDebugAppHostTarget = {
            absolutePath: secrets[0],
            displayPath: secrets[1],
        };
        const { service, sessions } = createService({
            telemetry,
            appHosts: [createAppHost({
                appHostPath: privateTarget.absolutePath,
                appHostPid: Number(secrets[4]),
                dashboardUrl: secrets[7],
                resources: [createResource({
                    name: secrets[2],
                    displayName: secrets[3],
                    resourceType: 'Container',
                    properties: {
                        pid: secrets[4],
                        marker: secrets[5],
                        executable: secrets[6],
                        url: secrets[7],
                        environment: secrets[8],
                        args: secrets[9],
                        property: secrets[10],
                    },
                })],
            })],
            provider: createProvider({
                requiredDebuggerExtensions: [{ id: secrets[11], label: secrets[11] }],
                createDebugConfiguration: async () => {
                    telemetry.currentTime = 70;
                    throw new Error(secrets[12]);
                },
            }),
        });

        try {
            assert.deepStrictEqual(await service.debug(createRequest({
                source: 'languageModelTool',
                appHost: privateTarget,
                resourceName: secrets[2],
            })), {
                outcome: 'error',
                errorKind: 'configurationFailed',
            });

            const serializedEvents = JSON.stringify(telemetry.events);
            assert.deepStrictEqual(telemetry.events, [
                {
                    name: 'aspire/vscode/resourcedebug/start',
                    properties: {
                        source: 'languageModelTool',
                        requested_strategy: 'attach',
                        controller: 'editor',
                    },
                    measurements: undefined,
                },
                {
                    name: 'aspire/vscode/resourcedebug/result',
                    properties: {
                        source: 'languageModelTool',
                        provider: 'dotnet',
                        resource_type: 'container',
                        requested_strategy: 'attach',
                        effective_strategy: 'none',
                        outcome: 'error',
                        controller: 'editor',
                        state: 'running',
                        debugger_requirement: 'installed',
                        error_kind: 'configurationFailed',
                    },
                    measurements: {
                        resolution_duration_ms: 20,
                        total_duration_ms: 20,
                    },
                },
            ]);
            assert.strictEqual(secrets.every(secret => !serializedEvents.includes(secret)), true);
        }
        finally {
            sessions.dispose();
        }
    });

    test('emits one session-end only after a correlated attach session starts and terminates', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        telemetry.currentTime = 100;
        let events: TestDebugSessionEvents | undefined;
        const fixture = createService({
            telemetry,
            provider: createProvider({
                createDebugConfiguration: async () => {
                    telemetry.currentTime = 110;
                    return { type: 'coreclr', request: 'attach', name: 'Attach debugger: API' };
                },
            }),
            startDebugging: async (_folder, configuration) => {
                events!.start(configuration);
                telemetry.currentTime = 115;
                return true;
            },
        });
        events = fixture.events;

        try {
            assert.deepStrictEqual(
                await fixture.service.debug(createRequest({ source: 'languageModelTool', strategy: 'auto' })),
                { outcome: 'started', providerId: 'dotnet' });
            telemetry.currentTime = 140;
            assert.ok(events.startedConfiguration);
            events.terminate(events.startedConfiguration);

            assert.deepStrictEqual(telemetry.events.at(-1), {
                name: 'aspire/vscode/resourcedebug/session/end',
                properties: {
                    source: 'languageModelTool',
                    provider: 'dotnet',
                    resource_type: 'project',
                    requested_strategy: 'auto',
                    effective_strategy: 'attach',
                    controller: 'editor',
                    session_end_reason: 'terminated',
                },
                measurements: {
                    session_duration_ms: 30,
                },
            });
            assert.strictEqual(telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/session/end').length, 1);
        }
        finally {
            fixture.sessions.dispose();
        }
    });

    test('does not emit a session-end for a pending attach that expires before a session starts', async () => {
        const clock = sinon.useFakeTimers();
        const telemetry = new TestResourceDebugTelemetry();
        const fixture = createService({
            telemetry,
            pendingStartTimeoutMs: 10,
        });

        try {
            assert.deepStrictEqual(await fixture.service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            await clock.tickAsync(10);

            assert.strictEqual(telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/start').length, 1);
            assert.strictEqual(telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/result').length, 1);
            assert.strictEqual(telemetry.events.filter(event => event.name === 'aspire/vscode/resourcedebug/session/end').length, 0);
        }
        finally {
            fixture.sessions.dispose();
            clock.restore();
        }
    });

    test('ignores telemetry sink failures when debugging a resource', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        sinon.stub(telemetry, 'recordStart').throws(new Error('raw telemetry start failure'));
        const { service, sessions } = createService({ telemetry });

        try {
            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.strictEqual((telemetry.recordStart as sinon.SinonStub).callCount, 1);
        }
        finally {
            sessions.dispose();
        }
    });

    test('ignores a throwing result telemetry sink when debugging a resource', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        sinon.stub(telemetry, 'recordResult').throws(new Error('raw telemetry result failure'));
        const { service, sessions } = createService({ telemetry });

        try {
            assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.strictEqual((telemetry.recordResult as sinon.SinonStub).callCount, 1);
        }
        finally {
            sessions.dispose();
        }
    });

    test('treats non-string and missing resource types as other without changing the debug result', async () => {
        for (const resourceType of [undefined, 42] as const) {
            const telemetry = new TestResourceDebugTelemetry();
            const { service, sessions } = createService({
                telemetry,
                appHosts: [createAppHost({
                    resources: [createResource({ resourceType: resourceType as unknown as string })],
                })],
            });

            try {
                assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
                assert.strictEqual(
                    telemetry.events.find(event => event.name === 'aspire/vscode/resourcedebug/result')?.properties.resource_type,
                    'other');
            }
            finally {
                sessions.dispose();
            }
        }
    });

    test('omits invalid result durations from a separate monotonic clock', async () => {
        const testCases: readonly {
            readonly name: string;
            readonly clock: TestResourceDebugClock;
            readonly measurements: Record<string, number>;
        }[] = [
            {
                name: 'throws',
                clock: new TestResourceDebugClock(
                    new Error('clock failure'),
                    new Error('clock failure'),
                    new Error('clock failure')),
                measurements: {},
            },
            {
                name: 'moves backwards',
                clock: new TestResourceDebugClock(100, 150, 50),
                measurements: { resolution_duration_ms: 50 },
            },
            {
                name: 'returns NaN',
                clock: new TestResourceDebugClock(Number.NaN, Number.NaN, Number.NaN),
                measurements: {},
            },
            {
                name: 'returns infinity',
                clock: new TestResourceDebugClock(
                    Number.POSITIVE_INFINITY,
                    Number.POSITIVE_INFINITY,
                    Number.POSITIVE_INFINITY),
                measurements: {},
            },
        ];

        for (const testCase of testCases) {
            const telemetry = new TestResourceDebugTelemetry();
            const { service, sessions } = createService({ telemetry, clock: testCase.clock });

            try {
                assert.deepStrictEqual(await service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' }, testCase.name);
                assert.deepStrictEqual(
                    telemetry.events.find(event => event.name === 'aspire/vscode/resourcedebug/result')?.measurements,
                    testCase.measurements,
                    testCase.name);
            }
            finally {
                sessions.dispose();
            }
        }
    });

    test('swallows a throwing session-end sink and cleans up the session exactly once', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        const recordSessionEnd = sinon.stub(telemetry, 'recordSessionEnd').throws(new Error('raw session-end telemetry failure'));
        let events: TestDebugSessionEvents | undefined;
        const fixture = createService({
            telemetry,
            startDebugging: async (_folder, configuration) => {
                events!.start(configuration);
                return true;
            },
        });
        events = fixture.events;

        try {
            assert.deepStrictEqual(await fixture.service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.ok(events.startedConfiguration);

            events.terminate(events.startedConfiguration);
            events.terminate(events.startedConfiguration);

            assert.strictEqual(recordSessionEnd.callCount, 1);
            assert.strictEqual(fixture.sessions.hasActiveSession(resolvedTarget, 'api'), false);
        }
        finally {
            fixture.sessions.dispose();
        }
    });

    test('omits the session duration when the injected monotonic clock moves backwards', async () => {
        const telemetry = new TestResourceDebugTelemetry();
        const clock = new TestResourceDebugClock(100, 110, 120, 130, 90);
        let events: TestDebugSessionEvents | undefined;
        const fixture = createService({
            telemetry,
            clock,
            startDebugging: async (_folder, configuration) => {
                events!.start(configuration);
                return true;
            },
        });
        events = fixture.events;

        try {
            assert.deepStrictEqual(await fixture.service.debug(createRequest()), { outcome: 'started', providerId: 'dotnet' });
            assert.ok(events.startedConfiguration);

            events.terminate(events.startedConfiguration);

            assert.deepStrictEqual(
                telemetry.events.find(event => event.name === 'aspire/vscode/resourcedebug/session/end')?.measurements,
                {});
        }
        finally {
            fixture.sessions.dispose();
        }
    });
});
