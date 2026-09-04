export interface TestHandlePropertyContext {
    toJSON(): MarshalledHandle;
    optionalResource: { get: () => Promise<TestResourceContext | null>; set: (value: Awaitable<TestResourceContext>) => Promise<void> };
    readOnlyOptionalResource(): Promise<TestResourceContext | null>;
    requiredResource: { get: () => TestResourceContextPromise; set: (value: Awaitable<TestResourceContext>) => Promise<void> };
    readOnlyRequiredResource(): TestResourceContextPromise;
    optionalContext: { get: () => Promise<TestEnvironmentContext | null>; set: (value: Awaitable<TestEnvironmentContext>) => Promise<void> };
    readOnlyOptionalContext(): Promise<TestEnvironmentContext | null>;
    requiredContext: { get: () => Promise<TestEnvironmentContext>; set: (value: Awaitable<TestEnvironmentContext>) => Promise<void> };
    readOnlyRequiredContext(): Promise<TestEnvironmentContext>;
}