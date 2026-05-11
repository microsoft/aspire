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
    registerHandleWrapper,
    isPromiseLike
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

export {
    InputType,
    InteractionInputCollection
} from './base.js';

export type {
    InteractionInput,
    InteractionInputOption
} from './base.js';

import type {
    Awaitable,
    InteractionInput,
    InteractionInputCollection,
    InputType
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

/** Handle to TestMutableCollectionContext */
type TestMutableCollectionContextHandle = Handle<'Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestMutableCollectionContext'>;

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
// Exported Values
// ============================================================================

export namespace TestConfigs {
    export const Default = { name: "default", port: 6379, enabled: true, optionalField: "cache" } as TestConfigDto;

    export namespace Profiles {
        export const Development = { name: "development", port: 5001, enabled: false, optionalField: null } as TestConfigDto;

    }

    export const Secure = { name: "secure", port: 6380, enabled: true, optionalField: null } as TestConfigDto;

    export const UnicodeGreeting = "你好こんにちは";

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
    /** Gets the Name property */
    name: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    /** Gets the Value property */
    value: {
        get: () => Promise<number>;
        set: (value: number) => Promise<void>;
    };
    /** Gets the CancellationToken property */
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
    /** Gets the Items property */
    items(): Promise<AspireList<string>>;
    /** Gets the Metadata property */
    metadata(): Promise<AspireDict<string, string>>;
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

    private _items?: AspireList<string>;
    async items(): Promise<AspireList<string>> {
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

    private _metadata?: AspireDict<string, string>;
    async metadata(): Promise<AspireDict<string, string>> {
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
    /** Gets the Name property */
    name: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    /** Gets the Description property */
    description: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    /** Gets the Priority property */
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
// TestMutableCollectionContext
// ============================================================================

export interface TestMutableCollectionContext {
    toJSON(): MarshalledHandle;
    /** Gets the Tags property */
    readonly tags: AspireList<string>;
    /** Gets the Counts property */
    readonly counts: AspireDict<string, number>;
}

// ============================================================================
// TestMutableCollectionContextImpl
// ============================================================================

/**
 * Type class for TestMutableCollectionContext.
 */
class TestMutableCollectionContextImpl implements TestMutableCollectionContext {
    constructor(private _handle: TestMutableCollectionContextHandle, private _client: AspireClientRpc) {}

    /** Serialize for JSON-RPC transport */
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    private _tags?: AspireList<string>;
    get tags(): AspireList<string> {
        if (!this._tags) {
            this._tags = new AspireList<string>(
                this._handle,
                this._client,
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.tags',
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.tags'
            );
        }
        return this._tags;
    }

    private _counts?: AspireDict<string, number>;
    get counts(): AspireDict<string, number> {
        if (!this._counts) {
            this._counts = new AspireDict<string, number>(
                this._handle,
                this._client,
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.counts',
                'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestMutableCollectionContext.counts'
            );
        }
        return this._counts;
    }

}

// ============================================================================
// TestResourceContext
// ============================================================================

export interface TestResourceContext {
    toJSON(): MarshalledHandle;
    /** Gets the Name property */
    name: {
        get: () => Promise<string>;
        set: (value: string) => Promise<void>;
    };
    /** Gets the Value property */
    value: {
        get: () => Promise<number>;
        set: (value: number) => Promise<void>;
    };
    /** Invokes the GetValueAsync method */
    getValueAsync(): Promise<string>;
    /** Invokes the SetValueAsync method */
    setValueAsync(value: string): TestResourceContextPromise;
    /** Invokes the ValidateAsync method */
    validateAsync(): Promise<boolean>;
}

export interface TestResourceContextPromise extends PromiseLike<TestResourceContext> {
    /** Invokes the GetValueAsync method */
    getValueAsync(): Promise<string>;
    /** Invokes the SetValueAsync method */
    setValueAsync(value: string): TestResourceContextPromise;
    /** Invokes the ValidateAsync method */
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

    async getValueAsync(): Promise<string> {
        const rpcArgs: Record<string, unknown> = { context: this._handle };
        return await this._client.invokeCapability<string>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync',
            rpcArgs
        );
    }

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
        return new TestResourceContextPromiseImpl(this._setValueAsyncInternal(value), this._client);
    }

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
    constructor(private _promise: Promise<TestResourceContext>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = TestResourceContext, TResult2 = never>(
        onfulfilled?: ((value: TestResourceContext) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    getValueAsync(): Promise<string> {
        return this._promise.then(obj => obj.getValueAsync());
    }

    setValueAsync(value: string): TestResourceContextPromise {
        return new TestResourceContextPromiseImpl(this._promise.then(obj => obj.setValueAsync(value)), this._client);
    }

    validateAsync(): Promise<boolean> {
        return this._promise.then(obj => obj.validateAsync());
    }

}

// ============================================================================
// DistributedApplicationBuilder
// ============================================================================

export interface DistributedApplicationBuilder {
    toJSON(): MarshalledHandle;
    /** Adds a test Redis resource */
    addTestRedis(name: string, options?: AddTestRedisOptions): TestRedisResourcePromise;
    /** Adds a test vault resource */
    addTestVault(name: string): TestVaultResourcePromise;
}

export interface DistributedApplicationBuilderPromise extends PromiseLike<DistributedApplicationBuilder> {
    /** Adds a test Redis resource */
    addTestRedis(name: string, options?: AddTestRedisOptions): TestRedisResourcePromise;
    /** Adds a test vault resource */
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
        return new TestRedisResourcePromiseImpl(this._addTestRedisInternal(name, port), this._client);
    }

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
        return new TestVaultResourcePromiseImpl(this._addTestVaultInternal(name), this._client);
    }

}

/**
 * Thenable wrapper for DistributedApplicationBuilder that enables fluent chaining.
 */
class DistributedApplicationBuilderPromiseImpl implements DistributedApplicationBuilderPromise {
    constructor(private _promise: Promise<DistributedApplicationBuilder>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = DistributedApplicationBuilder, TResult2 = never>(
        onfulfilled?: ((value: DistributedApplicationBuilder) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    addTestRedis(name: string, options?: AddTestRedisOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.addTestRedis(name, options)), this._client);
    }

    addTestVault(name: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.addTestVault(name)), this._client);
    }

}

// ============================================================================
// TestDatabaseResource
// ============================================================================

export interface TestDatabaseResource {
    toJSON(): MarshalledHandle;
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestDatabaseResourcePromise;
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestDatabaseResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestDatabaseResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestDatabaseResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise;
    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise;
    /** Adds a data volume */
    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): TestDatabaseResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise;
    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestDatabaseResourcePromise;
}

export interface TestDatabaseResourcePromise extends PromiseLike<TestDatabaseResource> {
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestDatabaseResourcePromise;
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestDatabaseResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestDatabaseResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestDatabaseResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise;
    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise;
    /** Adds a data volume */
    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): TestDatabaseResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise;
    /** Configures a route with middleware */
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

    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new TestDatabaseResourcePromiseImpl(this._withOptionalStringInternal(value, enabled), this._client);
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

    withConfig(config: TestConfigDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withConfigInternal(config), this._client);
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

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._testWithEnvironmentCallbackInternal(callback), this._client);
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

    withCreatedAt(createdAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withCreatedAtInternal(createdAt), this._client);
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

    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt), this._client);
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

    withCorrelationId(correlationId: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withCorrelationIdInternal(correlationId), this._client);
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

    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise {
        const callback = options?.callback;
        return new TestDatabaseResourcePromiseImpl(this._withOptionalCallbackInternal(callback), this._client);
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

    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withStatusInternal(status), this._client);
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

    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withNestedConfigInternal(config), this._client);
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

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withValidatorInternal(validator), this._client);
    }

    /** @internal */
    private async _testWaitForInternal(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): Promise<TestDatabaseResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._testWaitForInternal(dependency), this._client);
    }

    /** @internal */
    private async _withDependencyInternal(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<TestDatabaseResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withDependencyInternal(dependency), this._client);
    }

    /** @internal */
    private async _withUnionDependencyInternal(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<TestDatabaseResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestDatabaseResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withUnionDependency',
            rpcArgs
        );
        return new TestDatabaseResourceImpl(result, this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withUnionDependencyInternal(dependency), this._client);
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

    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withEndpointsInternal(endpoints), this._client);
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

    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withEnvironmentVariablesInternal(variables), this._client);
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

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withCancellableOperationInternal(operation), this._client);
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

    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise {
        const name = options?.name;
        return new TestDatabaseResourcePromiseImpl(this._withDataVolumeInternal(name), this._client);
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

    withMergeLabel(label: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeLabelInternal(label), this._client);
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

    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category), this._client);
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

    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port), this._client);
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

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme), this._client);
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

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestDatabaseResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles), this._client);
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

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestDatabaseResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles), this._client);
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

    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority), this._client);
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

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware), this._client);
    }

}

/**
 * Thenable wrapper for TestDatabaseResource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class TestDatabaseResourcePromiseImpl implements TestDatabaseResourcePromise {
    constructor(private _promise: Promise<TestDatabaseResource>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = TestDatabaseResource, TResult2 = never>(
        onfulfilled?: ((value: TestDatabaseResource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    withOptionalString(options?: WithOptionalStringOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)), this._client);
    }

    withConfig(config: TestConfigDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)), this._client);
    }

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)), this._client);
    }

    withCreatedAt(createdAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)), this._client);
    }

    withModifiedAt(modifiedAt: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)), this._client);
    }

    withCorrelationId(correlationId: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)), this._client);
    }

    withOptionalCallback(options?: WithOptionalCallbackOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)), this._client);
    }

    withStatus(status: TestResourceStatus): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)), this._client);
    }

    withNestedConfig(config: TestNestedDto): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)), this._client);
    }

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)), this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)), this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)), this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withUnionDependency(dependency)), this._client);
    }

    withEndpoints(endpoints: string[]): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)), this._client);
    }

    withEnvironmentVariables(variables: Record<string, string>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)), this._client);
    }

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)), this._client);
    }

    withDataVolume(options?: WithDataVolumeOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withDataVolume(options)), this._client);
    }

    withMergeLabel(label: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)), this._client);
    }

    withMergeLabelCategorized(label: string, category: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)), this._client);
    }

    withMergeEndpoint(endpointName: string, port: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)), this._client);
    }

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)), this._client);
    }

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)), this._client);
    }

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)), this._client);
    }

    withMergeRoute(path: string, method: string, handler: string, priority: number): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)), this._client);
    }

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)), this._client);
    }

}

// ============================================================================
// TestRedisResource
// ============================================================================

export interface TestRedisResource {
    toJSON(): MarshalledHandle;
    /** Adds a child database to a test Redis resource */
    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise;
    /** Configures the Redis resource with persistence */
    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise;
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestRedisResourcePromise;
    /** Gets the tags for the resource */
    getTags(): Promise<AspireList<string>>;
    /** Gets the metadata for the resource */
    getMetadata(): Promise<AspireDict<string, string>>;
    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise;
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestRedisResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestRedisResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestRedisResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestRedisResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestRedisResourcePromise;
    /** Gets the endpoints */
    getEndpoints(): Promise<string[]>;
    /** Sets connection string using direct interface target */
    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise;
    /** Redis-specific configuration */
    withRedisSpecific(option: string): TestRedisResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestRedisResourcePromise;
    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise;
    /** Gets the status of the resource asynchronously */
    getStatusAsync(options?: GetStatusAsyncOptions): Promise<string>;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise;
    /** Waits for the resource to be ready */
    waitForReadyAsync(timeout: number, options?: WaitForReadyAsyncOptions): Promise<boolean>;
    /** Tests multi-param callback destructuring */
    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    /** Adds a data volume with persistence */
    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): TestRedisResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise;
    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestRedisResourcePromise;
}

export interface TestRedisResourcePromise extends PromiseLike<TestRedisResource> {
    /** Adds a child database to a test Redis resource */
    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise;
    /** Configures the Redis resource with persistence */
    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise;
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestRedisResourcePromise;
    /** Gets the tags for the resource */
    getTags(): Promise<AspireList<string>>;
    /** Gets the metadata for the resource */
    getMetadata(): Promise<AspireDict<string, string>>;
    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise;
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestRedisResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestRedisResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestRedisResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestRedisResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestRedisResourcePromise;
    /** Gets the endpoints */
    getEndpoints(): Promise<string[]>;
    /** Sets connection string using direct interface target */
    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise;
    /** Redis-specific configuration */
    withRedisSpecific(option: string): TestRedisResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestRedisResourcePromise;
    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise;
    /** Gets the status of the resource asynchronously */
    getStatusAsync(options?: GetStatusAsyncOptions): Promise<string>;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise;
    /** Waits for the resource to be ready */
    waitForReadyAsync(timeout: number, options?: WaitForReadyAsyncOptions): Promise<boolean>;
    /** Tests multi-param callback destructuring */
    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise;
    /** Adds a data volume with persistence */
    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): TestRedisResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise;
    /** Configures a route with middleware */
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

    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise {
        const databaseName = options?.databaseName;
        return new TestDatabaseResourcePromiseImpl(this._addTestChildDatabaseInternal(name, databaseName), this._client);
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

    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise {
        const mode = options?.mode;
        return new TestRedisResourcePromiseImpl(this._withPersistenceInternal(mode), this._client);
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

    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new TestRedisResourcePromiseImpl(this._withOptionalStringInternal(value, enabled), this._client);
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

    withConfig(config: TestConfigDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withConfigInternal(config), this._client);
    }

    async getTags(): Promise<AspireList<string>> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle };
        return await this._client.invokeCapability<AspireList<string>>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/getTags',
            rpcArgs
        );
    }

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

    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withConnectionStringInternal(connectionString), this._client);
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

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._testWithEnvironmentCallbackInternal(callback), this._client);
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

    withCreatedAt(createdAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withCreatedAtInternal(createdAt), this._client);
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

    withModifiedAt(modifiedAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt), this._client);
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

    withCorrelationId(correlationId: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withCorrelationIdInternal(correlationId), this._client);
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

    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise {
        const callback = options?.callback;
        return new TestRedisResourcePromiseImpl(this._withOptionalCallbackInternal(callback), this._client);
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

    withStatus(status: TestResourceStatus): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withStatusInternal(status), this._client);
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

    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withNestedConfigInternal(config), this._client);
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

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withValidatorInternal(validator), this._client);
    }

    /** @internal */
    private async _testWaitForInternal(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): Promise<TestRedisResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._testWaitForInternal(dependency), this._client);
    }

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

    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withConnectionStringDirectInternal(connectionString), this._client);
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

    withRedisSpecific(option: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withRedisSpecificInternal(option), this._client);
    }

    /** @internal */
    private async _withDependencyInternal(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<TestRedisResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withDependencyInternal(dependency), this._client);
    }

    /** @internal */
    private async _withUnionDependencyInternal(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<TestRedisResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestRedisResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withUnionDependency',
            rpcArgs
        );
        return new TestRedisResourceImpl(result, this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withUnionDependencyInternal(dependency), this._client);
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

    withEndpoints(endpoints: string[]): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withEndpointsInternal(endpoints), this._client);
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

    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withEnvironmentVariablesInternal(variables), this._client);
    }

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

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withCancellableOperationInternal(operation), this._client);
    }

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

    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMultiParamHandleCallbackInternal(callback), this._client);
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

    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise {
        const name = options?.name;
        const isReadOnly = options?.isReadOnly;
        return new TestRedisResourcePromiseImpl(this._withDataVolumeInternal(name, isReadOnly), this._client);
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

    withMergeLabel(label: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeLabelInternal(label), this._client);
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

    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category), this._client);
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

    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port), this._client);
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

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme), this._client);
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

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestRedisResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles), this._client);
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

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestRedisResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles), this._client);
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

    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority), this._client);
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

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware), this._client);
    }

}

/**
 * Thenable wrapper for TestRedisResource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class TestRedisResourcePromiseImpl implements TestRedisResourcePromise {
    constructor(private _promise: Promise<TestRedisResource>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = TestRedisResource, TResult2 = never>(
        onfulfilled?: ((value: TestRedisResource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    addTestChildDatabase(name: string, options?: AddTestChildDatabaseOptions): TestDatabaseResourcePromise {
        return new TestDatabaseResourcePromiseImpl(this._promise.then(obj => obj.addTestChildDatabase(name, options)), this._client);
    }

    withPersistence(options?: WithPersistenceOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withPersistence(options)), this._client);
    }

    withOptionalString(options?: WithOptionalStringOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)), this._client);
    }

    withConfig(config: TestConfigDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)), this._client);
    }

    getTags(): Promise<AspireList<string>> {
        return this._promise.then(obj => obj.getTags());
    }

    getMetadata(): Promise<AspireDict<string, string>> {
        return this._promise.then(obj => obj.getMetadata());
    }

    withConnectionString(connectionString: ReferenceExpression): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withConnectionString(connectionString)), this._client);
    }

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)), this._client);
    }

    withCreatedAt(createdAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)), this._client);
    }

    withModifiedAt(modifiedAt: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)), this._client);
    }

    withCorrelationId(correlationId: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)), this._client);
    }

    withOptionalCallback(options?: WithOptionalCallbackOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)), this._client);
    }

    withStatus(status: TestResourceStatus): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)), this._client);
    }

    withNestedConfig(config: TestNestedDto): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)), this._client);
    }

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)), this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)), this._client);
    }

    getEndpoints(): Promise<string[]> {
        return this._promise.then(obj => obj.getEndpoints());
    }

    withConnectionStringDirect(connectionString: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withConnectionStringDirect(connectionString)), this._client);
    }

    withRedisSpecific(option: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withRedisSpecific(option)), this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)), this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withUnionDependency(dependency)), this._client);
    }

    withEndpoints(endpoints: string[]): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)), this._client);
    }

    withEnvironmentVariables(variables: Record<string, string>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)), this._client);
    }

    getStatusAsync(options?: GetStatusAsyncOptions): Promise<string> {
        return this._promise.then(obj => obj.getStatusAsync(options));
    }

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)), this._client);
    }

    waitForReadyAsync(timeout: number, options?: WaitForReadyAsyncOptions): Promise<boolean> {
        return this._promise.then(obj => obj.waitForReadyAsync(timeout, options));
    }

    withMultiParamHandleCallback(callback: (arg1: TestCallbackContext, arg2: TestEnvironmentContext) => Promise<void>): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMultiParamHandleCallback(callback)), this._client);
    }

    withDataVolume(options?: WithDataVolumeOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withDataVolume(options)), this._client);
    }

    withMergeLabel(label: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)), this._client);
    }

    withMergeLabelCategorized(label: string, category: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)), this._client);
    }

    withMergeEndpoint(endpointName: string, port: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)), this._client);
    }

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)), this._client);
    }

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)), this._client);
    }

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)), this._client);
    }

    withMergeRoute(path: string, method: string, handler: string, priority: number): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)), this._client);
    }

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestRedisResourcePromise {
        return new TestRedisResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)), this._client);
    }

}

// ============================================================================
// TestVaultResource
// ============================================================================

export interface TestVaultResource {
    toJSON(): MarshalledHandle;
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestVaultResourcePromise;
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestVaultResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestVaultResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestVaultResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestVaultResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestVaultResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestVaultResourcePromise;
    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise;
    /** Configures vault using direct interface target */
    withVaultDirect(option: string): TestVaultResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): TestVaultResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise;
    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestVaultResourcePromise;
}

export interface TestVaultResourcePromise extends PromiseLike<TestVaultResource> {
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): TestVaultResourcePromise;
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): TestVaultResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): TestVaultResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): TestVaultResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): TestVaultResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestVaultResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): TestVaultResourcePromise;
    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise;
    /** Configures vault using direct interface target */
    withVaultDirect(option: string): TestVaultResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): TestVaultResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise;
    /** Configures a route with middleware */
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

    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new TestVaultResourcePromiseImpl(this._withOptionalStringInternal(value, enabled), this._client);
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

    withConfig(config: TestConfigDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withConfigInternal(config), this._client);
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

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._testWithEnvironmentCallbackInternal(callback), this._client);
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

    withCreatedAt(createdAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withCreatedAtInternal(createdAt), this._client);
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

    withModifiedAt(modifiedAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt), this._client);
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

    withCorrelationId(correlationId: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withCorrelationIdInternal(correlationId), this._client);
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

    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise {
        const callback = options?.callback;
        return new TestVaultResourcePromiseImpl(this._withOptionalCallbackInternal(callback), this._client);
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

    withStatus(status: TestResourceStatus): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withStatusInternal(status), this._client);
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

    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withNestedConfigInternal(config), this._client);
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

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withValidatorInternal(validator), this._client);
    }

    /** @internal */
    private async _testWaitForInternal(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): Promise<TestVaultResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._testWaitForInternal(dependency), this._client);
    }

    /** @internal */
    private async _withDependencyInternal(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<TestVaultResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withDependencyInternal(dependency), this._client);
    }

    /** @internal */
    private async _withUnionDependencyInternal(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<TestVaultResource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<TestVaultResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withUnionDependency',
            rpcArgs
        );
        return new TestVaultResourceImpl(result, this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withUnionDependencyInternal(dependency), this._client);
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

    withEndpoints(endpoints: string[]): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withEndpointsInternal(endpoints), this._client);
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

    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withEnvironmentVariablesInternal(variables), this._client);
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

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withCancellableOperationInternal(operation), this._client);
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

    withVaultDirect(option: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withVaultDirectInternal(option), this._client);
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

    withMergeLabel(label: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeLabelInternal(label), this._client);
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

    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category), this._client);
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

    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port), this._client);
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

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme), this._client);
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

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestVaultResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles), this._client);
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

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new TestVaultResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles), this._client);
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

    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority), this._client);
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

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware), this._client);
    }

}

/**
 * Thenable wrapper for TestVaultResource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class TestVaultResourcePromiseImpl implements TestVaultResourcePromise {
    constructor(private _promise: Promise<TestVaultResource>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = TestVaultResource, TResult2 = never>(
        onfulfilled?: ((value: TestVaultResource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    withOptionalString(options?: WithOptionalStringOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)), this._client);
    }

    withConfig(config: TestConfigDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)), this._client);
    }

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)), this._client);
    }

    withCreatedAt(createdAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)), this._client);
    }

    withModifiedAt(modifiedAt: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)), this._client);
    }

    withCorrelationId(correlationId: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)), this._client);
    }

    withOptionalCallback(options?: WithOptionalCallbackOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)), this._client);
    }

    withStatus(status: TestResourceStatus): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)), this._client);
    }

    withNestedConfig(config: TestNestedDto): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)), this._client);
    }

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)), this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)), this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)), this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withUnionDependency(dependency)), this._client);
    }

    withEndpoints(endpoints: string[]): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)), this._client);
    }

    withEnvironmentVariables(variables: Record<string, string>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)), this._client);
    }

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)), this._client);
    }

    withVaultDirect(option: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withVaultDirect(option)), this._client);
    }

    withMergeLabel(label: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)), this._client);
    }

    withMergeLabelCategorized(label: string, category: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)), this._client);
    }

    withMergeEndpoint(endpointName: string, port: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)), this._client);
    }

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)), this._client);
    }

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)), this._client);
    }

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)), this._client);
    }

    withMergeRoute(path: string, method: string, handler: string, priority: number): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)), this._client);
    }

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): TestVaultResourcePromise {
        return new TestVaultResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)), this._client);
    }

}

// ============================================================================
// Resource
// ============================================================================

export interface Resource {
    toJSON(): MarshalledHandle;
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): ResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): ResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): ResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): ResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): ResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): ResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): ResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): ResourcePromise;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): ResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): ResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): ResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise;
    /** Configures a route with middleware */
    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): ResourcePromise;
}

export interface ResourcePromise extends PromiseLike<Resource> {
    /** Adds an optional string parameter */
    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise;
    /** Configures the resource with a DTO */
    withConfig(config: TestConfigDto): ResourcePromise;
    /** Sets the created timestamp */
    withCreatedAt(createdAt: string): ResourcePromise;
    /** Sets the modified timestamp */
    withModifiedAt(modifiedAt: string): ResourcePromise;
    /** Sets the correlation ID */
    withCorrelationId(correlationId: string): ResourcePromise;
    /** Configures with optional callback */
    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise;
    /** Sets the resource status */
    withStatus(status: TestResourceStatus): ResourcePromise;
    /** Configures with nested DTO */
    withNestedConfig(config: TestNestedDto): ResourcePromise;
    /** Adds validation callback */
    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise;
    /** Waits for another resource (test version) */
    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): ResourcePromise;
    /** Adds a dependency on another resource */
    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise;
    /** Adds a dependency from a string or another resource */
    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise;
    /** Sets the endpoints */
    withEndpoints(endpoints: string[]): ResourcePromise;
    /** Performs a cancellable operation */
    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise;
    /** Adds a label to the resource */
    withMergeLabel(label: string): ResourcePromise;
    /** Adds a categorized label to the resource */
    withMergeLabelCategorized(label: string, category: string): ResourcePromise;
    /** Configures a named endpoint */
    withMergeEndpoint(endpointName: string, port: number): ResourcePromise;
    /** Configures a named endpoint with scheme */
    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise;
    /** Configures resource logging */
    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise;
    /** Configures resource logging with file path */
    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise;
    /** Configures a route */
    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise;
    /** Configures a route with middleware */
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

    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise {
        const value = options?.value;
        const enabled = options?.enabled;
        return new ResourcePromiseImpl(this._withOptionalStringInternal(value, enabled), this._client);
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

    withConfig(config: TestConfigDto): ResourcePromise {
        return new ResourcePromiseImpl(this._withConfigInternal(config), this._client);
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

    withCreatedAt(createdAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withCreatedAtInternal(createdAt), this._client);
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

    withModifiedAt(modifiedAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withModifiedAtInternal(modifiedAt), this._client);
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

    withCorrelationId(correlationId: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withCorrelationIdInternal(correlationId), this._client);
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

    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise {
        const callback = options?.callback;
        return new ResourcePromiseImpl(this._withOptionalCallbackInternal(callback), this._client);
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

    withStatus(status: TestResourceStatus): ResourcePromise {
        return new ResourcePromiseImpl(this._withStatusInternal(status), this._client);
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

    withNestedConfig(config: TestNestedDto): ResourcePromise {
        return new ResourcePromiseImpl(this._withNestedConfigInternal(config), this._client);
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

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise {
        return new ResourcePromiseImpl(this._withValidatorInternal(validator), this._client);
    }

    /** @internal */
    private async _testWaitForInternal(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): Promise<Resource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/testWaitFor',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): ResourcePromise {
        return new ResourcePromiseImpl(this._testWaitForInternal(dependency), this._client);
    }

    /** @internal */
    private async _withDependencyInternal(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<Resource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withDependency',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise {
        return new ResourcePromiseImpl(this._withDependencyInternal(dependency), this._client);
    }

    /** @internal */
    private async _withUnionDependencyInternal(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): Promise<Resource> {
        dependency = isPromiseLike(dependency) ? await dependency : dependency;
        const rpcArgs: Record<string, unknown> = { builder: this._handle, dependency };
        const result = await this._client.invokeCapability<IResourceHandle>(
            'Aspire.Hosting.CodeGeneration.TypeScript.Tests/withUnionDependency',
            rpcArgs
        );
        return new ResourceImpl(result, this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise {
        return new ResourcePromiseImpl(this._withUnionDependencyInternal(dependency), this._client);
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

    withEndpoints(endpoints: string[]): ResourcePromise {
        return new ResourcePromiseImpl(this._withEndpointsInternal(endpoints), this._client);
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

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise {
        return new ResourcePromiseImpl(this._withCancellableOperationInternal(operation), this._client);
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

    withMergeLabel(label: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeLabelInternal(label), this._client);
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

    withMergeLabelCategorized(label: string, category: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeLabelCategorizedInternal(label, category), this._client);
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

    withMergeEndpoint(endpointName: string, port: number): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeEndpointInternal(endpointName, port), this._client);
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

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeEndpointSchemeInternal(endpointName, port, scheme), this._client);
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

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new ResourcePromiseImpl(this._withMergeLoggingInternal(logLevel, enableConsole, maxFiles), this._client);
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

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise {
        const enableConsole = options?.enableConsole;
        const maxFiles = options?.maxFiles;
        return new ResourcePromiseImpl(this._withMergeLoggingPathInternal(logLevel, logPath, enableConsole, maxFiles), this._client);
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

    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeRouteInternal(path, method, handler, priority), this._client);
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

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): ResourcePromise {
        return new ResourcePromiseImpl(this._withMergeRouteMiddlewareInternal(path, method, handler, priority, middleware), this._client);
    }

}

/**
 * Thenable wrapper for Resource that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class ResourcePromiseImpl implements ResourcePromise {
    constructor(private _promise: Promise<Resource>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = Resource, TResult2 = never>(
        onfulfilled?: ((value: Resource) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    withOptionalString(options?: WithOptionalStringOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withOptionalString(options)), this._client);
    }

    withConfig(config: TestConfigDto): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withConfig(config)), this._client);
    }

    withCreatedAt(createdAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withCreatedAt(createdAt)), this._client);
    }

    withModifiedAt(modifiedAt: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withModifiedAt(modifiedAt)), this._client);
    }

    withCorrelationId(correlationId: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withCorrelationId(correlationId)), this._client);
    }

    withOptionalCallback(options?: WithOptionalCallbackOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withOptionalCallback(options)), this._client);
    }

    withStatus(status: TestResourceStatus): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withStatus(status)), this._client);
    }

    withNestedConfig(config: TestNestedDto): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withNestedConfig(config)), this._client);
    }

    withValidator(validator: (arg: TestResourceContext) => Promise<boolean>): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withValidator(validator)), this._client);
    }

    testWaitFor(dependency: Awaitable<Resource | ResourceWithConnectionString | ResourceWithEnvironment | TestDatabaseResource | TestRedisResource | TestVaultResource>): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.testWaitFor(dependency)), this._client);
    }

    withDependency(dependency: Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withDependency(dependency)), this._client);
    }

    withUnionDependency(dependency: string | ResourceWithConnectionString | TestRedisResource | Awaitable<ResourceWithConnectionString | TestRedisResource>): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withUnionDependency(dependency)), this._client);
    }

    withEndpoints(endpoints: string[]): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withEndpoints(endpoints)), this._client);
    }

    withCancellableOperation(operation: (arg: CancellationToken) => Promise<void>): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withCancellableOperation(operation)), this._client);
    }

    withMergeLabel(label: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabel(label)), this._client);
    }

    withMergeLabelCategorized(label: string, category: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLabelCategorized(label, category)), this._client);
    }

    withMergeEndpoint(endpointName: string, port: number): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpoint(endpointName, port)), this._client);
    }

    withMergeEndpointScheme(endpointName: string, port: number, scheme: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeEndpointScheme(endpointName, port, scheme)), this._client);
    }

    withMergeLogging(logLevel: string, options?: WithMergeLoggingOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLogging(logLevel, options)), this._client);
    }

    withMergeLoggingPath(logLevel: string, logPath: string, options?: WithMergeLoggingPathOptions): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeLoggingPath(logLevel, logPath, options)), this._client);
    }

    withMergeRoute(path: string, method: string, handler: string, priority: number): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeRoute(path, method, handler, priority)), this._client);
    }

    withMergeRouteMiddleware(path: string, method: string, handler: string, priority: number, middleware: string): ResourcePromise {
        return new ResourcePromiseImpl(this._promise.then(obj => obj.withMergeRouteMiddleware(path, method, handler, priority, middleware)), this._client);
    }

}

// ============================================================================
// ResourceWithConnectionString
// ============================================================================

export interface ResourceWithConnectionString {
    toJSON(): MarshalledHandle;
    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise;
    /** Sets connection string using direct interface target */
    withConnectionStringDirect(connectionString: string): ResourceWithConnectionStringPromise;
}

export interface ResourceWithConnectionStringPromise extends PromiseLike<ResourceWithConnectionString> {
    /** Sets the connection string using a reference expression */
    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise;
    /** Sets connection string using direct interface target */
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

    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._withConnectionStringInternal(connectionString), this._client);
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

    withConnectionStringDirect(connectionString: string): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._withConnectionStringDirectInternal(connectionString), this._client);
    }

}

/**
 * Thenable wrapper for ResourceWithConnectionString that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class ResourceWithConnectionStringPromiseImpl implements ResourceWithConnectionStringPromise {
    constructor(private _promise: Promise<ResourceWithConnectionString>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = ResourceWithConnectionString, TResult2 = never>(
        onfulfilled?: ((value: ResourceWithConnectionString) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    withConnectionString(connectionString: ReferenceExpression): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._promise.then(obj => obj.withConnectionString(connectionString)), this._client);
    }

    withConnectionStringDirect(connectionString: string): ResourceWithConnectionStringPromise {
        return new ResourceWithConnectionStringPromiseImpl(this._promise.then(obj => obj.withConnectionStringDirect(connectionString)), this._client);
    }

}

// ============================================================================
// ResourceWithEnvironment
// ============================================================================

export interface ResourceWithEnvironment {
    toJSON(): MarshalledHandle;
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise;
    /** Sets environment variables */
    withEnvironmentVariables(variables: Record<string, string>): ResourceWithEnvironmentPromise;
}

export interface ResourceWithEnvironmentPromise extends PromiseLike<ResourceWithEnvironment> {
    /** Configures environment with callback (test version) */
    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise;
    /** Sets environment variables */
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

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._testWithEnvironmentCallbackInternal(callback), this._client);
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

    withEnvironmentVariables(variables: Record<string, string>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._withEnvironmentVariablesInternal(variables), this._client);
    }

}

/**
 * Thenable wrapper for ResourceWithEnvironment that enables fluent chaining.
 * @example
 * await builder.addSomething().withX().withY();
 */
class ResourceWithEnvironmentPromiseImpl implements ResourceWithEnvironmentPromise {
    constructor(private _promise: Promise<ResourceWithEnvironment>, private _client: AspireClientRpc, track = true) {
        if (track) { _client.trackPromise(_promise); }
    }

    then<TResult1 = ResourceWithEnvironment, TResult2 = never>(
        onfulfilled?: ((value: ResourceWithEnvironment) => TResult1 | PromiseLike<TResult1>) | null,
        onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
    ): PromiseLike<TResult1 | TResult2> {
        return this._promise.then(onfulfilled, onrejected);
    }

    testWithEnvironmentCallback(callback: (arg: TestEnvironmentContext) => Promise<void>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._promise.then(obj => obj.testWithEnvironmentCallback(callback)), this._client);
    }

    withEnvironmentVariables(variables: Record<string, string>): ResourceWithEnvironmentPromise {
        return new ResourceWithEnvironmentPromiseImpl(this._promise.then(obj => obj.withEnvironmentVariables(variables)), this._client);
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
 * await builder.addRedis("cache");
 * await builder.addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp");
 * const app = await builder.build();
 * await app.run();
 */
export async function createBuilder(options?: CreateBuilderOptions): Promise<DistributedApplicationBuilder> {
    const client = await connect();

    // Apply client-side options before any tracking begins
    if (options?.throwOnPendingRejections === false) {
        client.throwOnPendingRejections = false;
    }

    // Default args, projectDirectory, and appHostFilePath if not provided
    // ASPIRE_APPHOST_FILEPATH is set by the CLI for consistent socket hash computation
    const effectiveOptions: CreateBuilderOptions = {
        ...options,
        args: options?.args ?? process.argv.slice(2),
        projectDirectory: options?.projectDirectory ?? process.env.ASPIRE_PROJECT_DIRECTORY ?? process.cwd(),
        appHostFilePath: options?.appHostFilePath ?? process.env.ASPIRE_APPHOST_FILEPATH
    };

    // Strip client-only options before sending to the host
    delete effectiveOptions.throwOnPendingRejections;

    const handle = await client.invokeCapability<IDistributedApplicationBuilderHandle>(
        'Aspire.Hosting/createBuilder',
        { argsOrOptions: effectiveOptions }
    );
    return new DistributedApplicationBuilderImpl(handle, client);
}

// Re-export commonly used types
export { Handle, AppHostUsageError, CancellationToken, CapabilityError, registerCallback } from './transport.js';
export { refExpr, ReferenceExpression } from './base.js';
export type { HandleReference, Awaitable } from './base.js';

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
    } else if (error instanceof CapabilityError) {
        console.error(`\n❌ Capability Error: ${error.message}`);
        console.error(`   Code: ${error.code}`);
        if (error.capability) {
            console.error(`   Capability: ${error.capability}`);
        }
    } else {
        console.error(`\n❌ Uncaught Exception: ${error.message}`);
    }
    // Suppress stack traces for structured errors (AppHostUsageError, CapabilityError)
    // to keep polyglot output clean. Use --verbose for full diagnostics.
    if (!(error instanceof AppHostUsageError) && !(error instanceof CapabilityError) && error.stack) {
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
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestMutableCollectionContext', (handle, client) => new TestMutableCollectionContextImpl(handle as TestMutableCollectionContextHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext', (handle, client) => new TestResourceContextImpl(handle as TestResourceContextHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder', (handle, client) => new DistributedApplicationBuilderImpl(handle as IDistributedApplicationBuilderHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource', (handle, client) => new TestDatabaseResourceImpl(handle as TestDatabaseResourceHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource', (handle, client) => new TestRedisResourceImpl(handle as TestRedisResourceHandle, client));
registerHandleWrapper('Aspire.Hosting.CodeGeneration.TypeScript.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource', (handle, client) => new TestVaultResourceImpl(handle as TestVaultResourceHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource', (handle, client) => new ResourceImpl(handle as IResourceHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString', (handle, client) => new ResourceWithConnectionStringImpl(handle as IResourceWithConnectionStringHandle, client));
registerHandleWrapper('Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment', (handle, client) => new ResourceWithEnvironmentImpl(handle as IResourceWithEnvironmentHandle, client));

