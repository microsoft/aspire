// aspire.ts - Capability-based Aspire SDK
// This SDK uses the ATS (Aspire Type System) capability API.
// Capabilities are endpoints like 'Aspire.Hosting/createBuilder'.
//
// GENERATED CODE - DO NOT EDIT

import {
    AspireClient,
    Handle,
    MarshalledHandle,
    AppHostUsageError,
    CancellationToken,
    CapabilityError,
    registerCallback,
    wrapIfHandle,
    registerHandleWrapper
} from './transport.js';
import type { AspireClientRpc } from './transport.js';

import type { HandleReference } from './base.js';

import {
    ResourceBuilderBase,
    ReferenceExpression,
    refExpr,
    AspireDict,
    AspireList
} from './base.js';

// ============================================================================
// Handle Type Aliases (Internal - not exported to users)
// ============================================================================

/** Handle to ITestVaultResource */
type ITestVaultResourceHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource'>;

/** Handle to TestCallbackContext */
type TestCallbackContextHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext'>;

/** Handle to TestCollectionContext */
type TestCollectionContextHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext'>;

/** Handle to TestDatabaseResource */
type TestDatabaseResourceHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource'>;

/** Handle to TestEnvironmentContext */
type TestEnvironmentContextHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext'>;

/** Handle to TestRedisResource */
type TestRedisResourceHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource'>;

/** Handle to TestResourceContext */
type TestResourceContextHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext'>;

/** Handle to TestVaultResource */
type TestVaultResourceHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource'>;

/** Handle to IResource */
type IResourceHandle = Handle<'Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource'>;

/** Handle to IResourceWithConnectionString */
type IResourceWithConnectionStringHandle = Handle<'Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString'>;

/** Handle to IResourceWithEnvironment */
type IResourceWithEnvironmentHandle = Handle<'Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment'>;

/** Handle to ReferenceExpression */
type ReferenceExpressionHandle = Handle<'Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpression'>;

/** Handle to IDistributedApplicationBuilder */
type IDistributedApplicationBuilderHandle = Handle<'Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder'>;

// ============================================================================
// Enum Types
// ============================================================================

/** Enum type for TestPersistenceMode */
export enum TestPersistenceMode {
    None = "None",
    Volume = "Volume",
    Bind = "Bind",
}

/** Enum type for TestResourceStatus */
export enum TestResourceStatus {
    Pending = "Pending",
    Running = "Running",
    Stopped = "Stopped",
    Failed = "Failed",
}

// ============================================================================
// DTO Interfaces
// ============================================================================

/** DTO interface for TestConfigDto */
export interface TestConfigDto {
    name?: string;
    port?: number;
    enabled?: boolean;
    optionalField?: string;
}

/** DTO interface for TestDeeplyNestedDto */
export interface TestDeeplyNestedDto {
    nestedData?: AspireDict<string, AspireList<TestConfigDto>>;
    metadataArray?: AspireDict<string, string>[];
}

/** DTO interface for TestNestedDto */
export interface TestNestedDto {
    id?: string;
    config?: TestConfigDto;
    tags?: AspireList<string>;
    counts?: AspireDict<string, number>;
}

// ============================================================================
// Options Interfaces
// ============================================================================

export interface AddTestChildDatabaseOptions {
    databaseName?: string;
}

export interface AddTestRedisOptions {
    port?: number;
}

export interface GetStatusAsyncOptions {
    cancellationToken?: AbortSignal | CancellationToken;
}

export interface WaitForReadyAsyncOptions {
    cancellationToken?: AbortSignal | CancellationToken;
}

export interface WithDataVolumeOptions {
    name?: string;
    isReadOnly?: boolean;
}

export interface WithMergeLoggingOptions {
    enableConsole?: boolean;
    maxFiles?: number;
}

export interface WithMergeLoggingPathOptions {
    enableConsole?: boolean;
    maxFiles?: number;
}

export interface WithOptionalCallbackOptions {
    callback?: (arg: TestCallbackContext) => Promise<void>;
}

export interface WithOptionalStringOptions {
    value?: string;
    enabled?: boolean;
}

export interface WithPersistenceOptions {
    mode?: TestPersistenceMode;
}

// ============================================================================
// TestCallbackContext
// ============================================================================

export interface TestCallbackContext {
    toJSON(): MarshalledHandle;
    name: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    value: {
        get: () => Promise<number>;
        set: (value: number) => Promise<void>;
    };
    cancellationToken: {
        get: () => Promise<CancellationToken>;
        set: (value: AbortSignal | CancellationToken) => Promise<void>;
    };
}

// ============================================================================
// TestCallbackContextImpl
// ============================================================================

/**
 * Type class for TestCallbackContext.
 */
class TestCallbackContextImpl implements TestCallbackContext {
    constructor(private _handle: TestCallbackContextHandle, private _client: AspireClientRpc) {}

    /** Serialize for JSON-RPC transport */
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    /** Gets the Name property */
    name = {
        get: async (): Promise<string> => {
            return await this._client.invokeCapability<string>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name',
                { context: this._handle }
            );
        },
        set: async (value: string): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName',
                { context: this._handle, value }
            );
        }
    };

    /** Gets the Value property */
    value = {
        get: async (): Promise<number> => {
            return await this._client.invokeCapability<number>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value',
                { context: this._handle }
            );
        },
        set: async (value: number): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue',
                { context: this._handle, value }
            );
        }
    };

    /** Gets the CancellationToken property */
    cancellationToken = {
        get: async (): Promise<CancellationToken> => {
            const result = await this._client.invokeCapability<string | null>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken',
                { context: this._handle }
            );
            return CancellationToken.fromValue(result);
        },
        set: async (value: AbortSignal | CancellationToken): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setCancellationToken',
                { context: this._handle, value: CancellationToken.fromValue(value) }
            );
        }
    };

}

// ============================================================================
// TestCollectionContext
// ============================================================================

export interface TestCollectionContext {
    toJSON(): MarshalledHandle;
    readonly items: AspireList<string>;
    readonly metadata: AspireDict<string, string>;
}

// ============================================================================
// TestCollectionContextImpl
// ============================================================================

/**
 * Type class for TestCollectionContext.
 */
class TestCollectionContextImpl implements TestCollectionContext {
    constructor(private _handle: TestCollectionContextHandle, private _client: AspireClientRpc) {}

    /** Serialize for JSON-RPC transport */
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    /** Gets the Items property */
    private _items?: AspireList<string>;
    get items(): AspireList<string> {
        if (!this._items) {
            this._items = new AspireList<string>(
                this._handle,
                this._client,
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items',
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items'
            );
        }
        return this._items;
    }

    /** Gets the Metadata property */
    private _metadata?: AspireDict<string, string>;
    get metadata(): AspireDict<string, string> {
        if (!this._metadata) {
            this._metadata = new AspireDict<string, string>(
                this._handle,
                this._client,
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata',
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata'
            );
        }
        return this._metadata;
    }

}

// ============================================================================
// TestEnvironmentContext
// ============================================================================

export interface TestEnvironmentContext {
    toJSON(): MarshalledHandle;
    name: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    description: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    priority: {
        get: () => Promise<number>;
        set: (value: number) => Promise<void>;
    };
}

// ============================================================================
// TestEnvironmentContextImpl
// ============================================================================

/**
 * Type class for TestEnvironmentContext.
 */
class TestEnvironmentContextImpl implements TestEnvironmentContext {
    constructor(private _handle: TestEnvironmentContextHandle, private _client: AspireClientRpc) {}

    /** Serialize for JSON-RPC transport */
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    /** Gets the Name property */
    name = {
        get: async (): Promise<string> => {
            return await this._client.invokeCapability<string>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name',
                { context: this._handle }
            );
        },
        set: async (value: string): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName',
                { context: this._handle, value }
            );
        }
    };

    /** Gets the Description property */
    description = {
        get: async (): Promise<string> => {
            return await this._client.invokeCapability<string>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description',
                { context: this._handle }
            );
        },
        set: async (value: string): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription',
                { context: this._handle, value }
            );
        }
    };

    /** Gets the Priority property */
    priority = {
        get: async (): Promise<number> => {
            return await this._client.invokeCapability<number>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority',
                { context: this._handle }
            );
        },
        set: async (value: number): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority',
                { context: this._handle, value }
            );
        }
    };

}

// ============================================================================
// TestResourceContext
// ============================================================================

export interface TestResourceContext {
    toJSON(): MarshalledHandle;
    name: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    value: {
        get: () => Promise<number>;
        set: (value: number) => Promise<void>;
    };
    getValueAsync(): Promise<string>;
    setValueAsync(value: string): TestResourceContextPromise;
    validateAsync(): Promise<boolean>;
}

export interface TestResourceContextPromise extends PromiseLike<TestResourceContext> {
    getValueAsync(): Promise<string>;
    setValueAsync(value: string): TestResourceContextPromise;
    validateAsync(): Promise<boolean>;
}

// ============================================================================
// TestResourceContextImpl
// ============================================================================

/**
 * Type class for TestResourceContext.
 */
class TestResourceContextImpl implements TestResourceContext {
    constructor(private _handle: TestResourceContextHandle, private _client: AspireClientRpc) {}

    /** Serialize for JSON-RPC transport */
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    /** Gets the Name property */
    name = {
        get: async (): Promise<string> => {
            return await this._client.invokeCapability<string>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name',
                { context: this._handle }
            );
        },
        set: async (value: string): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName',
                { context: this._handle, value }
            );
        }
    };

    /** Gets the Value property */
    value = {
        get: async (): Promise<number> => {
            return await this._client.invokeCapability<number>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value',
                { context: this._handle }
            );
        },
        set: async (value: number): Promise<void> => {
            await this._client.invokeCapability<void>(
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue',
                { context: this._handle, value }
            );
        }
    };

    /** Invokes the GetValueAsync method */
    async getValueAsync(): Promise<string> {
        const rpcArgs: Record<string, unknown> = { context: this._handle };
        return await this._client.invokeCapability<string>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync',
            rpcArgs
        );
    }

    /** Invokes the SetValueAsync method */
    /** @internal */
    async _setValueAsyncInternal(value: string): Promise<TestResourceContext> {
        const rpcArgs: Record<string, unknown> = { context: this._handle, value };
        await this._client.invokeCapability<void>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync',
            rpcArgs
        );
        return this;
    }

    setValueAsync(value: string): TestResourceContextPromise {
        return new TestResourceContextPromiseImpl(this._setValueAsyncInternal(value));
    }

    /** Invokes the ValidateAsync method */
    async validateAsync(): Promise<boolean> {
        const rpcArgs: Record<string, unknown> = { context: this._handle };
        return await this._client.invokeCapability<boolean>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync',
            rpcArgs
        );
    }

}

/**
 * Thenable wrapper for TestResourceContext that enables fluent chaining.
 */
class TestResourceContextPromiseImpl implements TestResourceContextPromise {
    constructor(private _promise: Promise<TestResourceContext>) {}

    then<TResult1 = TestResourceContext, TResult2 = never>(
        onfulfilled?: ((value: TestResourceContext) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Invokes the GetValueAsync method */
    getValueAsync(): Promise<string> {
        return this._promise.then(obj => obj.getValueAsync());
    }

    /** Invokes the SetValueAsync method */
    setValueAsync(value: string): TestResourceContextPromise {
        return new TestResourceContextPromiseImpl(this._promise.then(obj => obj.setValueAsync(value)));
    }

    /** Invokes the ValidateAsync method */
    validateAsync(): Promise<boolean> {
        return this._promise.then(obj => obj.validateAsync());
    }

}

// ============================================================================
// DistributedApplicationBuilder
// ============================================================================

export interface DistributedApplicationBuilder {
    toJSON(): MarshalledHandle;
    addTestRedis(name: string, options?: AddTestRedisOptions): TestRedisResourcePromise;
    addTestVault(name: string): TestVaultResourcePromise;
}

export interface DistributedApplicationBuilderPromise extends PromiseLike<DistributedApplicationBuilder> {
    addTestRedis(name: string, options?: AddTestRedisOptions): TestRedisResourcePromise;
    addTestVault(name: string): TestVaultResourcePromise;
}

// ============================================================================
// DistributedApplicationBuilderImpl
// ============================================================================

/**
 * Type class for DistributedApplicationBuilder.
 */
class DistributedApplicationBuilderImpl implements DistributedApplicationBuilder {
    constructor(private _handle: IDistributedApplicationBuilderHandle, private _client: AspireClientRpc) {}

    /** Serialize for JSON-RPC transport */
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    /** Adds a test Redis resource */
    /** @internal */
    async _addTestRedisInternal(name: string, port?: number): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, name };
        if (port !== undefined) rpcArgs.port = port;
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestRedis',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    addTestRedis(name: string, options?: AddTestRedisOptions): TestRedisResourcePromise {
        const port = options?.port;
        return new TestRedisResourcePromiseImpl(this._addTestRedisInternal(name, port));
    }

    /** Adds a test vault resource */
    /** @internal */
    async _addTestVaultInternal(name: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, name };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestVault',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    addTestVault(name: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._addTestVaultInternal(name));
    }

}

/**
 * Thenable wrapper for DistributedApplicationBuilder that enables fluent chaining.
 */
class DistributedApplicationBuilderPromiseImpl implements DistributedApplicationBuilderPromise {
    constructor(private _promise: Promise<DistributedApplicationBuilder>) {}

    then<TResult1 = DistributedApplicationBuilder, TResult2 = never>(
        onfulfilled?: ((value: DistributedApplicationBuilder) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Adds a test Redis resource */
    addTestRedis(name: string, options?: AddTestRedisOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.addTestRedis(name, options)));
    }

    /** Adds a test vault resource */
    addTestVault(name: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.addTestVault(name)));
    }

}

// ============================================================================
// TestDatabaseResource
// ============================================================================

export interface TestDatabaseResource {
    toJSON(): MarshalledHandle;
    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise;
    withConfig(config: TestConfigDto): TestDatabaseResourcePromise;
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise;
    withCreatedAt(createdAt: string): TestDatabaseResourcePromise;
    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise;
    withCorrelationId(correlationId: string): TestDatabaseResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise;
    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise;
    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise;
    testWaitFor(dependency: HandleReference): TestDatabaseResourcePromise;
    withDependency(dependency: HandleReference): TestDatabaseResourcePromise;
    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise;
    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise;
    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise;
    withMergeLabel(label: string): TestDatabaseResourcePromise;
    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestDatabaseResourcePromise;
}

export interface TestDatabaseResourcePromise extends PromiseLike<TestDatabaseResource> {
    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise;
    withConfig(config: TestConfigDto): TestDatabaseResourcePromise;
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise;
    withCreatedAt(createdAt: string): TestDatabaseResourcePromise;
    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise;
    withCorrelationId(correlationId: string): TestDatabaseResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise;
    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise;
    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise;
    testWaitFor(dependency: HandleReference): TestDatabaseResourcePromise;
    withDependency(dependency: HandleReference): TestDatabaseResourcePromise;
    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise;
    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise;
    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise;
    withMergeLabel(label: string): TestDatabaseResourcePromise;
    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestDatabaseResourcePromise;
}

// ============================================================================
// TestDatabaseResourceImpl
// ============================================================================

class TestDatabaseResourceImpl extends ResourceBuilderBase<TestDatabaseResourceHandle> implements TestDatabaseResource {
    constructor(handle: TestDatabaseResourceHandle, client: AspireClientRpc) {
        super(handle, client);
    }

    /** @internal */
    private async _withOptionalStringInternal(value?: string, enabled?: boolean): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (value !== undefined) rpcArgs.value = value;
        if (enabled !== undefined) rpcArgs.enabled = enabled;
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new TestDatabaseResourcePromiseImpl(this._withOptionalStringInternal(value, enabled));
    }

    /** @internal */
    private async _withConfigInternal(config: TestConfigDto): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConfig',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withConfigInternal(config));
    }

    /** @internal */
    private async _testWithEnvironmentCallbackInternal(callback: (arg: TestEnvironmentContext) => Promise<void>): Promise<TestDatabaseResource> {
        const callbackId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestEnvironmentContextHandle;
            const arg = new TestEnvironmentContextImpl(argHandle, this._client);
            await callback(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, callback: callbackId };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWithEnvironmentCallback',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._testWithEnvironmentCallbackInternal(callback));
    }

    /** @internal */
    private async _withCreatedAtInternal(createdAt: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, createdAt };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCreatedAt',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withCreatedAtInternal(createdAt));
    }

    /** @internal */
    private async _withModifiedAtInternal(modifiedAt: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, modifiedAt };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withModifiedAt',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt));
    }

    /** @internal */
    private async _withCorrelationIdInternal(correlationId: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, correlationId };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCorrelationId',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withCorrelationIdInternal(correlationId));
    }

    /** @internal */
    private async _withOptionalCallbackInternal(callback?: (arg: TestCallbackContext) => Promise<void>): Promise<TestDatabaseResource> {
        const callbackId = callback ? registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestCallbackContextHandle;
            const arg = new TestCallbackContextImpl(argHandle, this._client);
            await callback(arg);
        }) : undefined;
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (callback !== undefined) rpcArgs.callback = callbackId;
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalCallback',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise {
        const callback = options?.callback;
        return new TestDatabaseResourcePromiseImpl(this._withOptionalCallbackInternal(callback));
    }

    /** @internal */
    private async _withStatusInternal(status: TestResourceStatus): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, status };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withStatus',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withStatusInternal(status));
    }

    /** @internal */
    private async _withNestedConfigInternal(config: TestNestedDto): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withNestedConfig',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withNestedConfigInternal(config));
    }

    /** @internal */
    private async _withValidatorInternal(validator: (arg: TestResourceContext) => Promise<boolean>): Promise<TestDatabaseResource> {
        const validatorId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestResourceContextHandle;
            const arg = new TestResourceContextImpl(argHandle, this._client);
            return await validator(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, validator: validatorId };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withValidator',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withValidatorInternal(validator));
    }

    /** @internal */
    private async _testWaitForInternal(dependency: HandleReference): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._testWaitForInternal(dependency));
    }

    /** @internal */
    private async _withDependencyInternal(dependency: HandleReference): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withDependencyInternal(dependency));
    }

    /** @internal */
    private async _withEndpointsInternal(endpoints: string[]): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpoints };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEndpoints',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withEndpointsInternal(endpoints));
    }

    /** @internal */
    private async _withEnvironmentVariablesInternal(variables: Record<string, string>): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, variables };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEnvironmentVariables',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withEnvironmentVariablesInternal(variables));
    }

    /** @internal */
    private async _withCancellableOperationInternal(operation: (arg: CancellationToken) => Promise<void>): Promise<TestDatabaseResource> {
        const operationId = registerCallback(async (argData: unknown) => {
            const arg = CancellationToken.fromValue(argData);
            await operation(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, operation: operationId };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCancellableOperation',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withCancellableOperationInternal(operation));
    }

    /** @internal */
    private async _withDataVolumeInternal(name?: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (name !== undefined) rpcArgs.name = name;
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDataVolume',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Adds a data volume */
    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise {
        const name = options?.name;
        return new TestDatabaseResourcePromiseImpl(this._withDataVolumeInternal(name));
    }

    /** @internal */
    private async _withMergeLabelInternal(label: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabel',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeLabelInternal(label));
    }

    /** @internal */
    private async _withMergeLabelCategorizedInternal(label: string, category: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label, category };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabelCategorized',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category));
    }

    /** @internal */
    private async _withMergeEndpointInternal(endpointName: string, port: number): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpoint',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port));
    }

    /** @internal */
    private async _withMergeEndpointSchemeInternal(endpointName: string, port: number, scheme: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port, scheme };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpointScheme',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme));
    }

    /** @internal */
    private async _withMergeLoggingInternal(logLevel: string, enableConsole?: boolean, maxFiles?: number): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLogging',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestDatabaseResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeLoggingPathInternal(logLevel: string, logPath: string, enableConsole?: boolean, maxFiles?: number): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel, logPath };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLoggingPath',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestDatabaseResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeRouteInternal(path: string, method: string, handler: string, priority: number): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRoute',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority));
    }

    /** @internal */
    private async _withMergeRouteMiddlewareInternal(path: string, method: string, handler: string, priority: number, middleware: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority, middleware };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRouteMiddleware',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware));
    }

}

/**
 * Thenable wrapper for TestDatabaseResource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class TestDatabaseResourcePromiseImpl implements TestDatabaseResourcePromise {
    constructor(private _promise: Promise<TestDatabaseResource>) {}

    then<TResult1 = TestDatabaseResource, TResult2 = never>(
        onfulfilled?: ((value: TestDatabaseResource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)));
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)));
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)));
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)));
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)));
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)));
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)));
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)));
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)));
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)));
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)));
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)));
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)));
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)));
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)));
    }

    /** Adds a data volume */
    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withDataVolume(options)));
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)));
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)));
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)));
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)));
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)));
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)));
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)));
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)));
    }

}

// ============================================================================
// TestRedisResource
// ============================================================================

export interface TestRedisResource {
    toJSON(): MarshalledHandle;
    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise;
    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise;
    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise;
    withConfig(config: TestConfigDto): TestRedisResourcePromise;
    getTags(): Promise<AspireList<string>>;
    getMetadata(): Promise<AspireDict<string, string>>;
    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise;
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    withCreatedAt(createdAt: string): TestRedisResourcePromise;
    withModifiedAt(modifiedAt: string): TestRedisResourcePromise;
    withCorrelationId(correlationId: string): TestRedisResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise;
    withStatus(status: TestResourceStatus): TestRedisResourcePromise;
    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise;
    testWaitFor(dependency: HandleReference): TestRedisResourcePromise;
    getEndpoints(): Promise<string[]>;
    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise;
    withRedisSpecific(option: string): TestRedisResourcePromise;
    withDependency(dependency: HandleReference): TestRedisResourcePromise;
    withEndpoints(endpoints: string[]): TestRedisResourcePromise;
    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise;
    getStatusAsync(options?: GetStatusAsyncOptions): Promise<string>;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise;
    waitForReadyAsync(timeout: number, options?: WaitForReadyAsyncOptions): Promise<boolean>;
    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise;
    withMergeLabel(label: string): TestRedisResourcePromise;
    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestRedisResourcePromise;
}

export interface TestRedisResourcePromise extends PromiseLike<TestRedisResource> {
    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise;
    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise;
    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise;
    withConfig(config: TestConfigDto): TestRedisResourcePromise;
    getTags(): Promise<AspireList<string>>;
    getMetadata(): Promise<AspireDict<string, string>>;
    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise;
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    withCreatedAt(createdAt: string): TestRedisResourcePromise;
    withModifiedAt(modifiedAt: string): TestRedisResourcePromise;
    withCorrelationId(correlationId: string): TestRedisResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise;
    withStatus(status: TestResourceStatus): TestRedisResourcePromise;
    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise;
    testWaitFor(dependency: HandleReference): TestRedisResourcePromise;
    getEndpoints(): Promise<string[]>;
    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise;
    withRedisSpecific(option: string): TestRedisResourcePromise;
    withDependency(dependency: HandleReference): TestRedisResourcePromise;
    withEndpoints(endpoints: string[]): TestRedisResourcePromise;
    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise;
    getStatusAsync(options?: GetStatusAsyncOptions): Promise<string>;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise;
    waitForReadyAsync(timeout: number, options?: WaitForReadyAsyncOptions): Promise<boolean>;
    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise;
    withMergeLabel(label: string): TestRedisResourcePromise;
    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestRedisResourcePromise;
}

// ============================================================================
// TestRedisResourceImpl
// ============================================================================

class TestRedisResourceImpl extends ResourceBuilderBase<TestRedisResourceHandle> implements TestRedisResource {
    constructor(handle: TestRedisResourceHandle, client: AspireClientRpc) {
        super(handle, client);
    }

    /** @internal */
    private async _addTestChildDatabaseInternal(name: string, databaseName?: string): Promise<TestDatabaseResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, name };
        if (databaseName !== undefined) rpcArgs.databaseName = databaseName;
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/addTestChildDatabase',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    /** Adds a child database to a test Redis resource */
    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise {
        const databaseName = options?.databaseName;
        return new TestDatabaseResourcePromiseImpl(this._addTestChildDatabaseInternal(name, databaseName));
    }

    /** @internal */
    private async _withPersistenceInternal(mode?: TestPersistenceMode): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (mode !== undefined) rpcArgs.mode = mode;
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withPersistence',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures the Redis resource with persistence */
    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise {
        const mode = options?.mode;
        return new TestRedisResourcePromiseImpl(this._withPersistenceInternal(mode));
    }

    /** @internal */
    private async _withOptionalStringInternal(value?: string, enabled?: boolean): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (value !== undefined) rpcArgs.value = value;
        if (enabled !== undefined) rpcArgs.enabled = enabled;
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new TestRedisResourcePromiseImpl(this._withOptionalStringInternal(value, enabled));
    }

    /** @internal */
    private async _withConfigInternal(config: TestConfigDto): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConfig',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withConfigInternal(config));
    }

    /** Gets the tags for the resource */
    async getTags(): Promise<AspireList<string>> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        return await this._client.invokeCapability<AspireList<string>>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/getTags',
            rpcArgs
        );
    }

    /** Gets the metadata for the resource */
    async getMetadata(): Promise<AspireDict<string, string>> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        return await this._client.invokeCapability<AspireDict<string, string>>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/getMetadata',
            rpcArgs
        );
    }

    /** @internal */
    private async _withConnectionStringInternal(connectionString: ReferenceExpression): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, connectionString };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConnectionString',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withConnectionStringInternal(connectionString));
    }

    /** @internal */
    private async _testWithEnvironmentCallbackInternal(callback: (arg: TestEnvironmentContext) => Promise<void>): Promise<TestRedisResource> {
        const callbackId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestEnvironmentContextHandle;
            const arg = new TestEnvironmentContextImpl(argHandle, this._client);
            await callback(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, callback: callbackId };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWithEnvironmentCallback',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._testWithEnvironmentCallbackInternal(callback));
    }

    /** @internal */
    private async _withCreatedAtInternal(createdAt: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, createdAt };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCreatedAt',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withCreatedAtInternal(createdAt));
    }

    /** @internal */
    private async _withModifiedAtInternal(modifiedAt: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, modifiedAt };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withModifiedAt',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt));
    }

    /** @internal */
    private async _withCorrelationIdInternal(correlationId: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, correlationId };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCorrelationId',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withCorrelationIdInternal(correlationId));
    }

    /** @internal */
    private async _withOptionalCallbackInternal(callback?: (arg: TestCallbackContext) => Promise<void>): Promise<TestRedisResource> {
        const callbackId = callback ? registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestCallbackContextHandle;
            const arg = new TestCallbackContextImpl(argHandle, this._client);
            await callback(arg);
        }) : undefined;
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (callback !== undefined) rpcArgs.callback = callbackId;
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalCallback',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise {
        const callback = options?.callback;
        return new TestRedisResourcePromiseImpl(this._withOptionalCallbackInternal(callback));
    }

    /** @internal */
    private async _withStatusInternal(status: TestResourceStatus): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, status };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withStatus',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withStatusInternal(status));
    }

    /** @internal */
    private async _withNestedConfigInternal(config: TestNestedDto): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withNestedConfig',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withNestedConfigInternal(config));
    }

    /** @internal */
    private async _withValidatorInternal(validator: (arg: TestResourceContext) => Promise<boolean>): Promise<TestRedisResource> {
        const validatorId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestResourceContextHandle;
            const arg = new TestResourceContextImpl(argHandle, this._client);
            return await validator(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, validator: validatorId };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withValidator',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withValidatorInternal(validator));
    }

    /** @internal */
    private async _testWaitForInternal(dependency: HandleReference): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._testWaitForInternal(dependency));
    }

    /** Gets the endpoints */
    async getEndpoints(): Promise<string[]> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        return await this._client.invokeCapability<string[]>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/getEndpoints',
            rpcArgs
        );
    }

    /** @internal */
    private async _withConnectionStringDirectInternal(connectionString: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, connectionString };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConnectionStringDirect',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets connection string using direct interface target */
    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withConnectionStringDirectInternal(connectionString));
    }

    /** @internal */
    private async _withRedisSpecificInternal(option: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, option };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withRedisSpecific',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Redis-specific configuration */
    withRedisSpecific(option: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withRedisSpecificInternal(option));
    }

    /** @internal */
    private async _withDependencyInternal(dependency: HandleReference): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withDependencyInternal(dependency));
    }

    /** @internal */
    private async _withEndpointsInternal(endpoints: string[]): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpoints };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEndpoints',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withEndpointsInternal(endpoints));
    }

    /** @internal */
    private async _withEnvironmentVariablesInternal(variables: Record<string, string>): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, variables };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEnvironmentVariables',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withEnvironmentVariablesInternal(variables));
    }

    /** Gets the status of the resource asynchronously */
    async getStatusAsync(options?: GetStatusAsyncOptions): Promise<string> {
        const cancellationToken = options?.cancellationToken;
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (cancellationToken !== undefined) rpcArgs.cancellationToken = CancellationToken.fromValue(cancellationToken);
        return await this._client.invokeCapability<string>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/getStatusAsync',
            rpcArgs
        );
    }

    /** @internal */
    private async _withCancellableOperationInternal(operation: (arg: CancellationToken) => Promise<void>): Promise<TestRedisResource> {
        const operationId = registerCallback(async (argData: unknown) => {
            const arg = CancellationToken.fromValue(argData);
            await operation(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, operation: operationId };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCancellableOperation',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withCancellableOperationInternal(operation));
    }

    /** Waits for the resource to be ready */
    async waitForReadyAsync(timeout: number, options?: WaitForReadyAsyncOptions): Promise<boolean> {
        const cancellationToken = options?.cancellationToken;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, timeout };
        if (cancellationToken !== undefined) rpcArgs.cancellationToken = CancellationToken.fromValue(cancellationToken);
        return await this._client.invokeCapability<boolean>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/waitForReadyAsync',
            rpcArgs
        );
    }

    /** @internal */
    private async _withMultiParamHandleCallbackInternal(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): Promise<TestRedisResource> {
        const callbackId = registerCallback(async (arg1Data: unknown, arg2Data: unknown) => {
            const arg1Handle = wrapIfHandle(arg1Data) as TestCallbackContextHandle;
            const arg1 = new TestCallbackContextImpl(arg1Handle, this._client);
            const arg2Handle = wrapIfHandle(arg2Data) as TestEnvironmentContextHandle;
            const arg2 = new TestEnvironmentContextImpl(arg2Handle, this._client);
            await callback(arg1, arg2);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, callback: callbackId };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMultiParamHandleCallback',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Tests multi-param callback destructuring */
    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMultiParamHandleCallbackInternal(callback));
    }

    /** @internal */
    private async _withDataVolumeInternal(name?: string, isReadOnly?: boolean): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (name !== undefined) rpcArgs.name = name;
        if (isReadOnly !== undefined) rpcArgs.isReadOnly = isReadOnly;
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDataVolume',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Adds a data volume with persistence */
    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise {
        const name = options?.name;
        const isReadOnly = options?.isReadOnly;
        return new TestRedisResourcePromiseImpl(this._withDataVolumeInternal(name, isReadOnly));
    }

    /** @internal */
    private async _withMergeLabelInternal(label: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabel',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeLabelInternal(label));
    }

    /** @internal */
    private async _withMergeLabelCategorizedInternal(label: string, category: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label, category };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabelCategorized',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category));
    }

    /** @internal */
    private async _withMergeEndpointInternal(endpointName: string, port: number): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpoint',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port));
    }

    /** @internal */
    private async _withMergeEndpointSchemeInternal(endpointName: string, port: number, scheme: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port, scheme };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpointScheme',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme));
    }

    /** @internal */
    private async _withMergeLoggingInternal(logLevel: string, enableConsole?: boolean, maxFiles?: number): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLogging',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestRedisResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeLoggingPathInternal(logLevel: string, logPath: string, enableConsole?: boolean, maxFiles?: number): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel, logPath };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLoggingPath',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestRedisResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeRouteInternal(path: string, method: string, handler: string, priority: number): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRoute',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority));
    }

    /** @internal */
    private async _withMergeRouteMiddlewareInternal(path: string, method: string, handler: string, priority: number, middleware: string): Promise<TestRedisResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority, middleware };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRouteMiddleware',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware));
    }

}

/**
 * Thenable wrapper for TestRedisResource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class TestRedisResourcePromiseImpl implements TestRedisResourcePromise {
    constructor(private _promise: Promise<TestRedisResource>) {}

    then<TResult1 = TestRedisResource, TResult2 = never>(
        onfulfilled?: ((value: TestRedisResource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Adds a child database to a test Redis resource */
    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.addTestChildDatabase(name, options)));
    }

    /** Configures the Redis resource with persistence */
    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withPersistence(options)));
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)));
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)));
    }

    /** Gets the tags for the resource */
    getTags(): Promise<AspireList<string>> {
        return this._promise.then(obj => obj.getTags());
    }

    /** Gets the metadata for the resource */
    getMetadata(): Promise<AspireDict<string, string>> {
        return this._promise.then(obj => obj.getMetadata());
    }

    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withConnectionString(connectionString)));
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)));
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)));
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)));
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)));
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)));
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)));
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)));
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)));
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)));
    }

    /** Gets the endpoints */
    getEndpoints(): Promise<string[]> {
        return this._promise.then(obj => obj.getEndpoints());
    }

    /** Sets connection string using direct interface target */
    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withConnectionStringDirect(connectionString)));
    }

    /** Redis-specific configuration */
    withRedisSpecific(option: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withRedisSpecific(option)));
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)));
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)));
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)));
    }

    /** Gets the status of the resource asynchronously */
    getStatusAsync(options?: GetStatusAsyncOptions): Promise<string> {
        return this._promise.then(obj => obj.getStatusAsync(options));
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)));
    }

    /** Waits for the resource to be ready */
    waitForReadyAsync(timeout: number, options?: WaitForReadyAsyncOptions): Promise<boolean> {
        return this._promise.then(obj => obj.waitForReadyAsync(timeout, options));
    }

    /** Tests multi-param callback destructuring */
    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMultiParamHandleCallback(callback)));
    }

    /** Adds a data volume with persistence */
    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withDataVolume(options)));
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)));
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)));
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)));
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)));
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)));
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)));
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)));
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)));
    }

}

// ============================================================================
// TestVaultResource
// ============================================================================

export interface TestVaultResource {
    toJSON(): MarshalledHandle;
    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise;
    withConfig(config: TestConfigDto): TestVaultResourcePromise;
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise;
    withCreatedAt(createdAt: string): TestVaultResourcePromise;
    withModifiedAt(modifiedAt: string): TestVaultResourcePromise;
    withCorrelationId(correlationId: string): TestVaultResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise;
    withStatus(status: TestResourceStatus): TestVaultResourcePromise;
    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise;
    testWaitFor(dependency: HandleReference): TestVaultResourcePromise;
    withDependency(dependency: HandleReference): TestVaultResourcePromise;
    withEndpoints(endpoints: string[]): TestVaultResourcePromise;
    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise;
    withVaultDirect(option: string): TestVaultResourcePromise;
    withMergeLabel(label: string): TestVaultResourcePromise;
    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestVaultResourcePromise;
}

export interface TestVaultResourcePromise extends PromiseLike<TestVaultResource> {
    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise;
    withConfig(config: TestConfigDto): TestVaultResourcePromise;
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise;
    withCreatedAt(createdAt: string): TestVaultResourcePromise;
    withModifiedAt(modifiedAt: string): TestVaultResourcePromise;
    withCorrelationId(correlationId: string): TestVaultResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise;
    withStatus(status: TestResourceStatus): TestVaultResourcePromise;
    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise;
    testWaitFor(dependency: HandleReference): TestVaultResourcePromise;
    withDependency(dependency: HandleReference): TestVaultResourcePromise;
    withEndpoints(endpoints: string[]): TestVaultResourcePromise;
    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise;
    withVaultDirect(option: string): TestVaultResourcePromise;
    withMergeLabel(label: string): TestVaultResourcePromise;
    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestVaultResourcePromise;
}

// ============================================================================
// TestVaultResourceImpl
// ============================================================================

class TestVaultResourceImpl extends ResourceBuilderBase<TestVaultResourceHandle> implements TestVaultResource {
    constructor(handle: TestVaultResourceHandle, client: AspireClientRpc) {
        super(handle, client);
    }

    /** @internal */
    private async _withOptionalStringInternal(value?: string, enabled?: boolean): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (value !== undefined) rpcArgs.value = value;
        if (enabled !== undefined) rpcArgs.enabled = enabled;
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new TestVaultResourcePromiseImpl(this._withOptionalStringInternal(value, enabled));
    }

    /** @internal */
    private async _withConfigInternal(config: TestConfigDto): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConfig',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withConfigInternal(config));
    }

    /** @internal */
    private async _testWithEnvironmentCallbackInternal(callback: (arg: TestEnvironmentContext) => Promise<void>): Promise<TestVaultResource> {
        const callbackId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestEnvironmentContextHandle;
            const arg = new TestEnvironmentContextImpl(argHandle, this._client);
            await callback(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, callback: callbackId };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWithEnvironmentCallback',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._testWithEnvironmentCallbackInternal(callback));
    }

    /** @internal */
    private async _withCreatedAtInternal(createdAt: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, createdAt };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCreatedAt',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withCreatedAtInternal(createdAt));
    }

    /** @internal */
    private async _withModifiedAtInternal(modifiedAt: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, modifiedAt };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withModifiedAt',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt));
    }

    /** @internal */
    private async _withCorrelationIdInternal(correlationId: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, correlationId };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCorrelationId',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withCorrelationIdInternal(correlationId));
    }

    /** @internal */
    private async _withOptionalCallbackInternal(callback?: (arg: TestCallbackContext) => Promise<void>): Promise<TestVaultResource> {
        const callbackId = callback ? registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestCallbackContextHandle;
            const arg = new TestCallbackContextImpl(argHandle, this._client);
            await callback(arg);
        }) : undefined;
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (callback !== undefined) rpcArgs.callback = callbackId;
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalCallback',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise {
        const callback = options?.callback;
        return new TestVaultResourcePromiseImpl(this._withOptionalCallbackInternal(callback));
    }

    /** @internal */
    private async _withStatusInternal(status: TestResourceStatus): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, status };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withStatus',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withStatusInternal(status));
    }

    /** @internal */
    private async _withNestedConfigInternal(config: TestNestedDto): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withNestedConfig',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withNestedConfigInternal(config));
    }

    /** @internal */
    private async _withValidatorInternal(validator: (arg: TestResourceContext) => Promise<boolean>): Promise<TestVaultResource> {
        const validatorId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestResourceContextHandle;
            const arg = new TestResourceContextImpl(argHandle, this._client);
            return await validator(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, validator: validatorId };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withValidator',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withValidatorInternal(validator));
    }

    /** @internal */
    private async _testWaitForInternal(dependency: HandleReference): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._testWaitForInternal(dependency));
    }

    /** @internal */
    private async _withDependencyInternal(dependency: HandleReference): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withDependencyInternal(dependency));
    }

    /** @internal */
    private async _withEndpointsInternal(endpoints: string[]): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpoints };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEndpoints',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withEndpointsInternal(endpoints));
    }

    /** @internal */
    private async _withEnvironmentVariablesInternal(variables: Record<string, string>): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, variables };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEnvironmentVariables',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withEnvironmentVariablesInternal(variables));
    }

    /** @internal */
    private async _withCancellableOperationInternal(operation: (arg: CancellationToken) => Promise<void>): Promise<TestVaultResource> {
        const operationId = registerCallback(async (argData: unknown) => {
            const arg = CancellationToken.fromValue(argData);
            await operation(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, operation: operationId };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCancellableOperation',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withCancellableOperationInternal(operation));
    }

    /** @internal */
    private async _withVaultDirectInternal(option: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, option };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withVaultDirect',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures vault using direct interface target */
    withVaultDirect(option: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withVaultDirectInternal(option));
    }

    /** @internal */
    private async _withMergeLabelInternal(label: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabel',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeLabelInternal(label));
    }

    /** @internal */
    private async _withMergeLabelCategorizedInternal(label: string, category: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label, category };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabelCategorized',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category));
    }

    /** @internal */
    private async _withMergeEndpointInternal(endpointName: string, port: number): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpoint',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port));
    }

    /** @internal */
    private async _withMergeEndpointSchemeInternal(endpointName: string, port: number, scheme: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port, scheme };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpointScheme',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme));
    }

    /** @internal */
    private async _withMergeLoggingInternal(logLevel: string, enableConsole?: boolean, maxFiles?: number): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLogging',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestVaultResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeLoggingPathInternal(logLevel: string, logPath: string, enableConsole?: boolean, maxFiles?: number): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel, logPath };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLoggingPath',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestVaultResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeRouteInternal(path: string, method: string, handler: string, priority: number): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRoute',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority));
    }

    /** @internal */
    private async _withMergeRouteMiddlewareInternal(path: string, method: string, handler: string, priority: number, middleware: string): Promise<TestVaultResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority, middleware };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRouteMiddleware',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware));
    }

}

/**
 * Thenable wrapper for TestVaultResource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class TestVaultResourcePromiseImpl implements TestVaultResourcePromise {
    constructor(private _promise: Promise<TestVaultResource>) {}

    then<TResult1 = TestVaultResource, TResult2 = never>(
        onfulfilled?: ((value: TestVaultResource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)));
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)));
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)));
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)));
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)));
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)));
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)));
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)));
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)));
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)));
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)));
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)));
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)));
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)));
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)));
    }

    /** Configures vault using direct interface target */
    withVaultDirect(option: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withVaultDirect(option)));
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)));
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)));
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)));
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)));
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)));
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)));
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)));
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)));
    }

}

// ============================================================================
// Resource
// ============================================================================

export interface Resource {
    toJSON(): MarshalledHandle;
    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise;
    withConfig(config: TestConfigDto): ResourcePromise;
    withCreatedAt(createdAt: string): ResourcePromise;
    withModifiedAt(modifiedAt: string): ResourcePromise;
    withCorrelationId(correlationId: string): ResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise;
    withStatus(status: TestResourceStatus): ResourcePromise;
    withNestedConfig(config: TestNestedDto): ResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise;
    testWaitFor(dependency: HandleReference): ResourcePromise;
    withDependency(dependency: HandleReference): ResourcePromise;
    withEndpoints(endpoints: string[]): ResourcePromise;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise;
    withMergeLabel(label: string): ResourcePromise;
    withMergeLabelCategorized(label: string, category: string): ResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): ResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): ResourcePromise;
}

export interface ResourcePromise extends PromiseLike<Resource> {
    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise;
    withConfig(config: TestConfigDto): ResourcePromise;
    withCreatedAt(createdAt: string): ResourcePromise;
    withModifiedAt(modifiedAt: string): ResourcePromise;
    withCorrelationId(correlationId: string): ResourcePromise;
    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise;
    withStatus(status: TestResourceStatus): ResourcePromise;
    withNestedConfig(config: TestNestedDto): ResourcePromise;
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise;
    testWaitFor(dependency: HandleReference): ResourcePromise;
    withDependency(dependency: HandleReference): ResourcePromise;
    withEndpoints(endpoints: string[]): ResourcePromise;
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise;
    withMergeLabel(label: string): ResourcePromise;
    withMergeLabelCategorized(label: string, category: string): ResourcePromise;
    withMergeEndpoint(endpointName: string, port: number): ResourcePromise;
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise;
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise;
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise;
    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise;
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): ResourcePromise;
}

// ============================================================================
// ResourceImpl
// ============================================================================

class ResourceImpl extends ResourceBuilderBase<IResourceHandle> implements Resource {
    constructor(handle: IResourceHandle, client: AspireClientRpc) {
        super(handle, client);
    }

    /** @internal */
    private async _withOptionalStringInternal(value?: string, enabled?: boolean): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (value !== undefined) rpcArgs.value = value;
        if (enabled !== undefined) rpcArgs.enabled = enabled;
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalString',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new ResourcePromiseImpl(this._withOptionalStringInternal(value, enabled));
    }

    /** @internal */
    private async _withConfigInternal(config: TestConfigDto): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConfig',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): ResourcePromise {
        return new ResourcePromiseImpl(this._withConfigInternal(config));
    }

    /** @internal */
    private async _withCreatedAtInternal(createdAt: string): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, createdAt };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCreatedAt',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withCreatedAtInternal(createdAt));
    }

    /** @internal */
    private async _withModifiedAtInternal(modifiedAt: string): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, modifiedAt };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withModifiedAt',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt));
    }

    /** @internal */
    private async _withCorrelationIdInternal(correlationId: string): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, correlationId };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCorrelationId',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withCorrelationIdInternal(correlationId));
    }

    /** @internal */
    private async _withOptionalCallbackInternal(callback?: (arg: TestCallbackContext) => Promise<void>): Promise<Resource> {
        const callbackId = callback ? registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestCallbackContextHandle;
            const arg = new TestCallbackContextImpl(argHandle, this._client);
            await callback(arg);
        }) : undefined;
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        if (callback !== undefined) rpcArgs.callback = callbackId;
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withOptionalCallback',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise {
        const callback = options?.callback;
        return new ResourcePromiseImpl(this._withOptionalCallbackInternal(callback));
    }

    /** @internal */
    private async _withStatusInternal(status: TestResourceStatus): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, status };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withStatus',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): ResourcePromise {
        return new ResourcePromiseImpl(this._withStatusInternal(status));
    }

    /** @internal */
    private async _withNestedConfigInternal(config: TestNestedDto): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, config };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withNestedConfig',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): ResourcePromise {
        return new ResourcePromiseImpl(this._withNestedConfigInternal(config));
    }

    /** @internal */
    private async _withValidatorInternal(validator: (arg: TestResourceContext) => Promise<boolean>): Promise<Resource> {
        const validatorId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestResourceContextHandle;
            const arg = new TestResourceContextImpl(argHandle, this._client);
            return await validator(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, validator: validatorId };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withValidator',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise {
        return new ResourcePromiseImpl(this._withValidatorInternal(validator));
    }

    /** @internal */
    private async _testWaitForInternal(dependency: HandleReference): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): ResourcePromise {
        return new ResourcePromiseImpl(this._testWaitForInternal(dependency));
    }

    /** @internal */
    private async _withDependencyInternal(dependency: HandleReference): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): ResourcePromise {
        return new ResourcePromiseImpl(this._withDependencyInternal(dependency));
    }

    /** @internal */
    private async _withEndpointsInternal(endpoints: string[]): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpoints };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEndpoints',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): ResourcePromise {
        return new ResourcePromiseImpl(this._withEndpointsInternal(endpoints));
    }

    /** @internal */
    private async _withCancellableOperationInternal(operation: (arg: CancellationToken) => Promise<void>): Promise<Resource> {
        const operationId = registerCallback(async (argData: unknown) => {
            const arg = CancellationToken.fromValue(argData);
            await operation(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, operation: operationId };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withCancellableOperation',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise {
        return new ResourcePromiseImpl(this._withCancellableOperationInternal(operation));
    }

    /** @internal */
    private async _withMergeLabelInternal(label: string): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabel',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeLabelInternal(label));
    }

    /** @internal */
    private async _withMergeLabelCategorizedInternal(label: string, category: string): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, label, category };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLabelCategorized',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category));
    }

    /** @internal */
    private async _withMergeEndpointInternal(endpointName: string, port: number): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpoint',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port));
    }

    /** @internal */
    private async _withMergeEndpointSchemeInternal(endpointName: string, port: number, scheme: string): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, port, scheme };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeEndpointScheme',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme));
    }

    /** @internal */
    private async _withMergeLoggingInternal(logLevel: string, enableConsole?: boolean, maxFiles?: number): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLogging',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new ResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeLoggingPathInternal(logLevel: string, logPath: string, enableConsole?: boolean, maxFiles?: number): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, logLevel, logPath };
        if (enableConsole !== undefined) rpcArgs.enableConsole = enableConsole;
        if (maxFiles !== undefined) rpcArgs.maxFiles = maxFiles;
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeLoggingPath',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new ResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles));
    }

    /** @internal */
    private async _withMergeRouteInternal(path: string, method: string, handler: string, priority: number): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRoute',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority));
    }

    /** @internal */
    private async _withMergeRouteMiddlewareInternal(path: string, method: string, handler: string, priority: number, middleware: string): Promise<Resource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, path, method, handler, priority, middleware };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withMergeRouteMiddleware',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware));
    }

}

/**
 * Thenable wrapper for Resource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class ResourcePromiseImpl implements ResourcePromise {
    constructor(private _promise: Promise<Resource>) {}

    then<TResult1 = Resource, TResult2 = never>(
        onfulfilled?: ((value: Resource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)));
    }

    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)));
    }

    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)));
    }

    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)));
    }

    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)));
    }

    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)));
    }

    /** Sets the resource status */
    withStatus(status: TestResourceStatus): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)));
    }

    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)));
    }

    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)));
    }

    /** Waits for another resource (test version) */
    testWaitFor(dependency: HandleReference): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)));
    }

    /** Adds a dependency on another resource */
    withDependency(dependency: HandleReference): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)));
    }

    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)));
    }

    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)));
    }

    /** Adds a label to the resource */
    withMergeLabel(label: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)));
    }

    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)));
    }

    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)));
    }

    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)));
    }

    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)));
    }

    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)));
    }

    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)));
    }

    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)));
    }

}

// ============================================================================
// ResourceWithConnectionString
// ============================================================================

export interface ResourceWithConnectionString {
    toJSON(): MarshalledHandle;
    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise;
    withConnectionStringDirect(connectionString: string): ResourceWithConnectionStringPromise;
}

export interface ResourceWithConnectionStringPromise extends PromiseLike<ResourceWithConnectionString> {
    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise;
    withConnectionStringDirect(connectionString: string): ResourceWithConnectionStringPromise;
}

// ============================================================================
// ResourceWithConnectionStringImpl
// ============================================================================

class ResourceWithConnectionStringImpl extends ResourceBuilderBase<IResourceWithConnectionStringHandle> implements ResourceWithConnectionString {
    constructor(handle: IResourceWithConnectionStringHandle, client: AspireClientRpc) {
        super(handle, client);
    }

    /** @internal */
    private async _withConnectionStringInternal(connectionString: ReferenceExpression): Promise<ResourceWithConnectionString> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, connectionString };
        const result = await this._client.invokeCapability<IResourceWithConnectionStringHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConnectionString',
            rpcArgs
        );
        return new ResourceWithConnectionStringImpl(result, this._client);
    }

    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._withConnectionStringInternal(connectionString));
    }

    /** @internal */
    private async _withConnectionStringDirectInternal(connectionString: string): Promise<ResourceWithConnectionString> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, connectionString };
        const result = await this._client.invokeCapability<IResourceWithConnectionStringHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withConnectionStringDirect',
            rpcArgs
        );
        return new ResourceWithConnectionStringImpl(result, this._client);
    }

    /** Sets connection string using direct interface target */
    withConnectionStringDirect(connectionString: string): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._withConnectionStringDirectInternal(connectionString));
    }

}

/**
 * Thenable wrapper for ResourceWithConnectionString that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class ResourceWithConnectionStringPromiseImpl implements ResourceWithConnectionStringPromise {
    constructor(private _promise: Promise<ResourceWithConnectionString>) {}

    then<TResult1 = ResourceWithConnectionString, TResult2 = never>(
        onfulfilled?: ((value: ResourceWithConnectionString) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._promise.then(obj => obj.withConnectionString(connectionString)));
    }

    /** Sets connection string using direct interface target */
    withConnectionStringDirect(connectionString: string): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._promise.then(obj => obj.withConnectionStringDirect(connectionString)));
    }

}

// ============================================================================
// ResourceWithEnvironment
// ============================================================================

export interface ResourceWithEnvironment {
    toJSON(): MarshalledHandle;
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise;
    withEnvironmentVariables(variables: Record<string, string>): ResourceWithEnvironmentPromise;
}

export interface ResourceWithEnvironmentPromise extends PromiseLike<ResourceWithEnvironment> {
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise;
    withEnvironmentVariables(variables: Record<string, string>): ResourceWithEnvironmentPromise;
}

// ============================================================================
// ResourceWithEnvironmentImpl
// ============================================================================

class ResourceWithEnvironmentImpl extends ResourceBuilderBase<IResourceWithEnvironmentHandle> implements ResourceWithEnvironment {
    constructor(handle: IResourceWithEnvironmentHandle, client: AspireClientRpc) {
        super(handle, client);
    }

    /** @internal */
    private async _testWithEnvironmentCallbackInternal(callback: (arg: TestEnvironmentContext) => Promise<void>): Promise<ResourceWithEnvironment> {
        const callbackId = registerCallback(async (argData: unknown) => {
            const argHandle = wrapIfHandle(argData) as TestEnvironmentContextHandle;
            const arg = new TestEnvironmentContextImpl(argHandle, this._client);
            await callback(arg);
        });
        const rpcArgs: Record<string, unknown> = { builder: this._handle, callback: callbackId };
        const result = await this._client.invokeCapability<IResourceWithEnvironmentHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWithEnvironmentCallback',
            rpcArgs
        );
        return new ResourceWithEnvironmentImpl(result, this._client);
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._testWithEnvironmentCallbackInternal(callback));
    }

    /** @internal */
    private async _withEnvironmentVariablesInternal(variables: Record<string, string>): Promise<ResourceWithEnvironment> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, variables };
        const result = await this._client.invokeCapability<IResourceWithEnvironmentHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withEnvironmentVariables',
            rpcArgs
        );
        return new ResourceWithEnvironmentImpl(result, this._client);
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._withEnvironmentVariablesInternal(variables));
    }

}

/**
 * Thenable wrapper for ResourceWithEnvironment that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class ResourceWithEnvironmentPromiseImpl implements ResourceWithEnvironmentPromise {
    constructor(private _promise: Promise<ResourceWithEnvironment>) {}

    then<TResult1 = ResourceWithEnvironment, TResult2 = never>(
        onfulfilled?: ((value: ResourceWithEnvironment) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)));
    }

    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)));
    }

}

// ============================================================================
// Connection Helper
// ============================================================================

/**
 * Creates and connects to the Aspire AppHost.
 * Reads connection info from environment variables set by `aspire run`.
 */
export async function connect(): Promise<AspireClientRpc> {
    const socketPath = process.env.REMOTE_APP_HOST_SOCKET_PATH;
    if (!socketPath) {
        throw new Error(
            'REMOTE_APP_HOST_SOCKET_PATH environment variable not set. ' +
            'Run this application using `aspire run`.'
        );
    }

    const client = new AspireClient(socketPath);
    await client.connect();

    // Exit the process if the server connection is lost
    client.onDisconnect(() => {
        console.error('Connection to AppHost lost. Exiting...');
        process.exit(1);
    });

    return client;
}

/**
 * Creates a new distributed application builder.
 * This is the entry point for building Aspire applications.
 *
 * @param options - Optional configuration options for the builder
 * @returns A DistributedApplicationBuilder instance
 *
 * @example
 * const builder = await createBuilder();
 * builder.addRedis("cache");
 * builder.addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp");
 * const app = await builder.build();
 * await app.run();
 */
export async function createBuilder(options?: CreateBuilderOptions): Promise<DistributedApplicationBuilder> {
    const client = await connect();

    // Default args, projectDirectory, and appHostFilePath if not provided
    // ASPIRE_APPHOST_FILEPATH is set by the CLI for consistent socket hash computation
    const effectiveOptions: CreateBuilderOptions = {
        ...options,
        args: options?.args ?? process.argv.slice(2),
        projectDirectory: options?.projectDirectory ?? process.env.ASPIRE_PROJECT_DIRECTORY ?? process.cwd(),
        appHostFilePath: options?.appHostFilePath ?? process.env.ASPIRE_APPHOST_FILEPATH
    };

    const handle = await client.invokeCapability<IDistributedApplicationBuilderHandle>(
        'Aspire.Hosting/createBuilderWithOptions',
        { options: effectiveOptions }
    );
    return new DistributedApplicationBuilderImpl(handle, client);
}

// Re-export commonly used types
export { Handle, AppHostUsageError, CancellationToken, CapabilityError, registerCallback } from './transport.js';
export { refExpr, ReferenceExpression } from './base.js';
export type { HandleReference } from './base.js';

// ============================================================================
// Global Error Handling
// ============================================================================

/**
 * Set up global error handlers to ensure the process exits properly on errors.
 * Node.js doesn't exit on unhandled rejections by default, so we need to handle them.
 */
process.on('unhandledRejection', (reason: unknown) => {
    const error = reason instanceof Error ? reason : new Error(String(reason));

    if (reason instanceof AppHostUsageError) {
        console.error(`\n❌ AppHost Error: ${error.message}`);
    } else if (reason instanceof CapabilityError) {
        console.error(`\n❌ Capability Error: ${error.message}`);
        console.error(`   Code: ${(reason as CapabilityError).code}`);
        if ((reason as CapabilityError).capability) {
            console.error(`   Capability: ${(reason as CapabilityError).capability}`);
        }
    } else {
        console.error(`\n❌ Unhandled Error: ${error.message}`);
        if (error.stack) {
            console.error(error.stack);
        }
    }

    process.exit(1);
});

process.on('uncaughtException', (error: Error) => {
    if (error instanceof AppHostUsageError) {
        console.error(`\n❌ AppHost Error: ${error.message}`);
    } else {
        console.error(`\n❌ Uncaught Exception: ${error.message}`);
    }
    if (!(error instanceof AppHostUsageError) && error.stack) {
        console.error(error.stack);
    }
    process.exit(1);
});

// ============================================================================
// Handle Wrapper Registrations
// ============================================================================

// Register wrapper factories for typed handle wrapping in callbacks
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext', (handle, client) => new TestCallbackContextImpl(handle as TestCallbackContextHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext', (handle, client) => new TestCollectionContextImpl(handle as TestCollectionContextHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext', (handle, client) => new TestEnvironmentContextImpl(handle as TestEnvironmentContextHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext', (handle, client) => new TestResourceContextImpl(handle as TestResourceContextHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder', (handle, client) => new DistributedApplicationBuilderImpl(handle as IDistributedApplicationBuilderHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource', (handle, client) => new TestDatabaseResourceImpl(handle as TestDatabaseResourceHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource', (handle, client) => new TestRedisResourceImpl(handle as TestRedisResourceHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource', (handle, client) => new TestVaultResourceImpl(handle as TestVaultResourceHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource', (handle, client) => new ResourceImpl(handle as IResourceHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString', (handle, client) => new ResourceWithConnectionStringImpl(handle as IResourceWithConnectionStringHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment', (handle, client) => new ResourceWithEnvironmentImpl(handle as IResourceWithEnvironmentHandle, client));

