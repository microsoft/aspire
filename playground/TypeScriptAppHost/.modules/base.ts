// aspire.ts - Core Aspire types: base classes, ReferenceExpression
import { Handle, AspireClient, MarshalledHandle } from './transport.js';

// Re-export transport types for convenience
export { Handle, AspireClient, CapabilityError, registerCallback, unregisterCallback, registerCancellation, unregisterCancellation } from './transport.js';
export type { MarshalledHandle, AtsError, AtsErrorDetails, CallbackFunction } from './transport.js';
export { AtsErrorCodes, isMarshalledHandle, isAtsError, wrapIfHandle } from './transport.js';

// ============================================================================
// Reference Expression
// ============================================================================

/**
 * Represents a reference expression that can be passed to capabilities.
 *
 * Reference expressions are serialized in the protocol as:
 * ```json
 * {
 *   "$expr": {
 *     "format": "redis://{0}:{1}",
 *     "valueProviders": [
 *       { "$handle": "Aspire.Hosting.ApplicationModel/EndpointReference:1" },
 *       { "$handle": "Aspire.Hosting.ApplicationModel/EndpointReference:2" }
 *     ]
 *   }
 * }
 * ```
 *
 * @example
 * ```typescript
 * const redis = await builder.addRedis("cache");
 * const endpoint = await redis.getEndpoint("tcp");
 *
 * // Create a reference expression
 * const expr = refExpr`redis://${endpoint}:6379`;
 *
 * // Use it in an environment variable
 * await api.withEnvironment("REDIS_URL", expr);
 * ```
 */
export class ReferenceExpression {
    private readonly _format: string;
    private readonly _valueProviders: unknown[];

    private constructor(format: string, valueProviders: unknown[]) {
        this._format = format;
        this._valueProviders = valueProviders;
    }

    /**
     * Creates a reference expression from a tagged template literal.
     *
     * @param strings - The template literal string parts
     * @param values - The interpolated values (handles to value providers)
     * @returns A ReferenceExpression instance
     */
    static create(strings: TemplateStringsArray, ...values: unknown[]): ReferenceExpression {
        // Build the format string with {0}, {1}, etc. placeholders
        let format = '';
        for (let i = 0; i < strings.length; i++) {
            format += strings[i];
            if (i < values.length) {
                format += `{${i}}`;
            }
        }

        // Extract handles from values
        const valueProviders = values.map(extractHandleForExpr);

        return new ReferenceExpression(format, valueProviders);
    }

    /**
     * Serializes the reference expression for JSON-RPC transport.
     * Uses the $expr format recognized by the server.
     */
    toJSON(): { $expr: { format: string; valueProviders?: unknown[] } } {
        return {
            $expr: {
                format: this._format,
                valueProviders: this._valueProviders.length > 0 ? this._valueProviders : undefined
            }
        };
    }

    /**
     * String representation for debugging.
     */
    toString(): string {
        return `ReferenceExpression(${this._format})`;
    }
}

/**
 * Extracts a value for use in reference expressions.
 * Supports handles (objects) and string literals.
 * @internal
 */
function extractHandleForExpr(value: unknown): unknown {
    if (value === null || value === undefined) {
        throw new Error('Cannot use null or undefined in reference expression');
    }

    // String literals - include directly in the expression
    if (typeof value === 'string') {
        return value;
    }

    // Number literals - convert to string
    if (typeof value === 'number') {
        return String(value);
    }

    // Handle objects - get their JSON representation
    if (value instanceof Handle) {
        return value.toJSON();
    }

    // Objects with $handle property (already in handle format)
    if (typeof value === 'object' && value !== null && '$handle' in value) {
        return value;
    }

    // Objects with toJSON that returns a handle
    if (typeof value === 'object' && value !== null && 'toJSON' in value && typeof value.toJSON === 'function') {
        const json = value.toJSON();
        if (json && typeof json === 'object' && '$handle' in json) {
            return json;
        }
    }

    throw new Error(
        `Cannot use value of type ${typeof value} in reference expression. ` +
        `Expected a Handle, string, or number.`
    );
}

/**
 * Tagged template function for creating reference expressions.
 *
 * Use this to create dynamic expressions that reference endpoints, parameters, and other
 * value providers. The expression is evaluated at runtime by Aspire.
 *
 * @example
 * ```typescript
 * const redis = await builder.addRedis("cache");
 * const endpoint = await redis.getEndpoint("tcp");
 *
 * // Create a reference expression using the tagged template
 * const expr = refExpr`redis://${endpoint}:6379`;
 *
 * // Use it in an environment variable
 * await api.withEnvironment("REDIS_URL", expr);
 * ```
 */
export function refExpr(strings: TemplateStringsArray, ...values: unknown[]): ReferenceExpression {
    return ReferenceExpression.create(strings, ...values);
}

// ============================================================================
// ResourceBuilderBase
// ============================================================================

/**
 * Base class for resource builders (e.g., RedisBuilder, ContainerBuilder).
 * Provides handle management and JSON serialization.
 */
export class ResourceBuilderBase<THandle extends Handle = Handle> {
    constructor(protected _handle: THandle, protected _client: AspireClient) {}

    toJSON(): MarshalledHandle { return this._handle.toJSON(); }
}

// ============================================================================
// AspireList<T> - Mutable List Wrapper
// ============================================================================

/**
 * Wrapper for a mutable .NET List<T>.
 * Provides array-like methods that invoke capabilities on the underlying collection.
 *
 * @example
 * ```typescript
 * const items = await resource.getItems(); // Returns AspireList<ItemBuilder>
 * const count = await items.count();
 * const first = await items.get(0);
 * await items.add(newItem);
 * ```
 */
export class AspireList<T> {
    private _resolvedHandle?: Handle;
    private _resolvePromise?: Promise<Handle>;

    constructor(
        private readonly _handleOrContext: Handle,
        private readonly _client: AspireClient,
        private readonly _typeId: string,
        private readonly _getterCapabilityId?: string
    ) {
        // If no getter capability, the handle is already the list handle
        if (!_getterCapabilityId) {
            this._resolvedHandle = _handleOrContext;
        }
    }

    /**
     * Ensures we have the actual list handle by calling the getter if needed.
     */
    private async _ensureHandle(): Promise<Handle> {
        if (this._resolvedHandle) {
            return this._resolvedHandle;
        }
        if (this._resolvePromise) {
            return this._resolvePromise;
        }
        // Call the getter capability to get the actual list handle
        this._resolvePromise = (async () => {
            const result = await this._client.invokeCapability(this._getterCapabilityId!, {
                context: this._handleOrContext
            });
            this._resolvedHandle = result as Handle;
            return this._resolvedHandle;
        })();
        return this._resolvePromise;
    }

    /**
     * Gets the number of elements in the list.
     */
    async count(): Promise<number> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/List.length', {
            list: handle
        }) as number;
    }

    /**
     * Gets the element at the specified index.
     */
    async get(index: number): Promise<T> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/List.get', {
            list: handle,
            index
        }) as T;
    }

    /**
     * Adds an element to the end of the list.
     */
    async add(item: T): Promise<void> {
        const handle = await this._ensureHandle();
        await this._client.invokeCapability('Aspire.Hosting/List.add', {
            list: handle,
            item
        });
    }

    /**
     * Removes the element at the specified index.
     */
    async removeAt(index: number): Promise<void> {
        const handle = await this._ensureHandle();
        await this._client.invokeCapability('Aspire.Hosting/List.removeAt', {
            list: handle,
            index
        });
    }

    /**
     * Clears all elements from the list.
     */
    async clear(): Promise<void> {
        const handle = await this._ensureHandle();
        await this._client.invokeCapability('Aspire.Hosting/List.clear', {
            list: handle
        });
    }

    /**
     * Converts the list to an array (creates a copy).
     */
    async toArray(): Promise<T[]> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/List.toArray', {
            list: handle
        }) as T[];
    }

    toJSON(): MarshalledHandle { 
        if (this._resolvedHandle) {
            return this._resolvedHandle.toJSON();
        }
        return this._handleOrContext.toJSON(); 
    }
}

// ============================================================================
// AspireDict<K, V> - Mutable Dictionary Wrapper
// ============================================================================

/**
 * Wrapper for a mutable .NET Dictionary<K, V>.
 * Provides object-like methods that invoke capabilities on the underlying collection.
 *
 * @example
 * ```typescript
 * const config = await resource.getConfig(); // Returns AspireDict<string, string>
 * const value = await config.get("key");
 * await config.set("key", "value");
 * const hasKey = await config.containsKey("key");
 * ```
 */
export class AspireDict<K, V> {
    private _resolvedHandle?: Handle;
    private _resolvePromise?: Promise<Handle>;

    constructor(
        private readonly _handleOrContext: Handle,
        private readonly _client: AspireClient,
        private readonly _typeId: string,
        private readonly _getterCapabilityId?: string
    ) {
        // If no getter capability, the handle is already the dictionary handle
        if (!_getterCapabilityId) {
            this._resolvedHandle = _handleOrContext;
        }
    }

    /**
     * Ensures we have the actual dictionary handle by calling the getter if needed.
     */
    private async _ensureHandle(): Promise<Handle> {
        if (this._resolvedHandle) {
            return this._resolvedHandle;
        }
        if (this._resolvePromise) {
            return this._resolvePromise;
        }
        // Call the getter capability to get the actual dictionary handle
        this._resolvePromise = (async () => {
            const result = await this._client.invokeCapability(this._getterCapabilityId!, {
                context: this._handleOrContext
            });
            this._resolvedHandle = result as Handle;
            return this._resolvedHandle;
        })();
        return this._resolvePromise;
    }

    /**
     * Gets the number of key-value pairs in the dictionary.
     */
    async count(): Promise<number> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/Dict.count', {
            dict: handle
        }) as number;
    }

    /**
     * Gets the value associated with the specified key.
     * @throws If the key is not found.
     */
    async get(key: K): Promise<V> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/Dict.get', {
            dict: handle,
            key
        }) as V;
    }

    /**
     * Sets the value for the specified key.
     */
    async set(key: K, value: V): Promise<void> {
        const handle = await this._ensureHandle();
        await this._client.invokeCapability('Aspire.Hosting/Dict.set', {
            dict: handle,
            key,
            value
        });
    }

    /**
     * Determines whether the dictionary contains the specified key.
     */
    async containsKey(key: K): Promise<boolean> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/Dict.has', {
            dict: handle,
            key
        }) as boolean;
    }

    /**
     * Removes the value with the specified key.
     * @returns True if the element was removed; false if the key was not found.
     */
    async remove(key: K): Promise<boolean> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/Dict.remove', {
            dict: handle,
            key
        }) as boolean;
    }

    /**
     * Clears all key-value pairs from the dictionary.
     */
    async clear(): Promise<void> {
        const handle = await this._ensureHandle();
        await this._client.invokeCapability('Aspire.Hosting/Dict.clear', {
            dict: handle
        });
    }

    /**
     * Gets all keys in the dictionary.
     */
    async keys(): Promise<K[]> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/Dict.keys', {
            dict: handle
        }) as K[];
    }

    /**
     * Gets all values in the dictionary.
     */
    async values(): Promise<V[]> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/Dict.values', {
            dict: handle
        }) as V[];
    }

    /**
     * Converts the dictionary to a plain object (creates a copy).
     * Only works when K is string.
     */
    async toObject(): Promise<Record<string, V>> {
        const handle = await this._ensureHandle();
        return await this._client.invokeCapability('Aspire.Hosting/Dict.toObject', {
            dict: handle
        }) as Record<string, V>;
    }

    async toJSON(): Promise<MarshalledHandle> {
        const handle = await this._ensureHandle();
        return handle.toJSON();
    }
}
