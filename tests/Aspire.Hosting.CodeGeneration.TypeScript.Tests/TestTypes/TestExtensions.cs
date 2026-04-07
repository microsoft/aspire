// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

/// <summary>
/// Extension methods for testing code generation.
/// </summary>
public static class TestExtensions
{
    /// <summary>
    /// Adds a test Redis resource.
    /// </summary>
    [AspireExport(Description = "Adds a test Redis resource")]
    public static IResourceBuilder<TestRedisResource> AddTestRedis(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    {
        var resource = new TestRedisResource(name);
        return builder.AddResource(resource)
            .WithEndpoint(port: port, name: "tcp");
    }

    /// <summary>
    /// Adds a test database resource.
    /// </summary>
    public static IResourceBuilder<TestDatabaseResource> AddTestDatabase(
        this IDistributedApplicationBuilder builder,
        string name,
        string? databaseName = null)
    {
        var resource = new TestDatabaseResource(name)
        {
            DatabaseName = databaseName
        };
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Adds a child database to a Redis server resource (factory method pattern).
    /// </summary>
    /// <remarks>
    /// This method tests the factory method codegen pattern where a method on builder type A
    /// returns builder type B (e.g., SqlServerServerResource.AddDatabase returning SqlServerDatabaseResource).
    /// </remarks>
    [AspireExport(Description = "Adds a child database to a test Redis resource")]
    public static IResourceBuilder<TestDatabaseResource> AddTestChildDatabase(
        this IResourceBuilder<TestRedisResource> builder,
        string name,
        string? databaseName = null)
    {
        var resource = new TestDatabaseResource(name)
        {
            DatabaseName = databaseName
        };
        return builder.ApplicationBuilder.AddResource(resource);
    }

    /// <summary>
    /// Configures the Redis resource with persistence.
    /// </summary>
    [AspireExport(Description = "Configures the Redis resource with persistence")]
    public static IResourceBuilder<TestRedisResource> WithPersistence(
        this IResourceBuilder<TestRedisResource> builder,
        TestPersistenceMode mode = TestPersistenceMode.Volume)
    {
        return builder.WithAnnotation(new TestPersistenceAnnotation(mode));
    }

    /// <summary>
    /// Configures the resource with a custom callback.
    /// </summary>
    public static IResourceBuilder<T> WithCustomCallback<T>(
        this IResourceBuilder<T> builder,
        Action<TestCallbackContext> callback) where T : IResourceWithEnvironment
    {
        callback(new TestCallbackContext());
        return builder;
    }

    /// <summary>
    /// Adds an optional string parameter.
    /// </summary>
    [AspireExport(Description = "Adds an optional string parameter")]
    public static IResourceBuilder<T> WithOptionalString<T>(
        this IResourceBuilder<T> builder,
        string? value = null,
        bool enabled = true) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Adds multiple parameters with defaults.
    /// </summary>
    public static IResourceBuilder<T> WithMultipleDefaults<T>(
        this IResourceBuilder<T> builder,
        int count = 10,
        string prefix = "item",
        bool useUpperCase = false,
        double multiplier = 1.5) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Configures the resource with a builder callback.
    /// This tests the Action&lt;IResourceBuilder&lt;T&gt;&gt; pattern.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithBuilderCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Action<IResourceBuilder<TestRedisResource>>? configure = null)
    {
        configure?.Invoke(builder);
        return builder;
    }

    /// <summary>
    /// Returns the resource as IResourceWithConnectionString builder.
    /// This tests that interface types are discovered and get builder classes generated.
    /// </summary>
    public static IResourceBuilder<IResourceWithConnectionString> AsConnectionString(
        this IResourceBuilder<TestRedisResource> builder)
    {
        // Cast to interface builder
        return builder;
    }

    /// <summary>
    /// Tests circular type reference: Action takes IResourceBuilder which has methods that take Actions.
    /// This ensures the queue-based discovery handles cycles correctly.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithCircularCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Action<IResourceBuilder<TestRedisResource>> configure)
    {
        configure?.Invoke(builder);
        return builder;
    }

    /// <summary>
    /// Tests nested circular references: Action&lt;Action&lt;IResourceBuilder&gt;&gt;.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithNestedCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Action<Action<IResourceBuilder<TestRedisResource>>> outerConfigure)
    {
        outerConfigure?.Invoke(_ => { });
        return builder;
    }

    // ===== Edge Case Tests =====

    /// <summary>
    /// Tests async delegate callback: Func&lt;T, Task&gt;.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithAsyncCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Func<TestCallbackContext, Task> asyncCallback)
    {
        asyncCallback?.Invoke(new TestCallbackContext());
        return builder;
    }

    /// <summary>
    /// Tests async delegate with return value: Func&lt;T, Task&lt;TResult&gt;&gt;.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithAsyncCallbackWithResult(
        this IResourceBuilder<TestRedisResource> builder,
        Func<TestCallbackContext, Task<bool>> asyncCallback)
    {
        asyncCallback?.Invoke(new TestCallbackContext());
        return builder;
    }

    /// <summary>
    /// Tests async builder callback: Func&lt;IResourceBuilder&lt;T&gt;, Task&gt;.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithAsyncBuilderCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Func<IResourceBuilder<TestRedisResource>, Task> asyncConfigure)
    {
        asyncConfigure?.Invoke(builder);
        return builder;
    }

    /// <summary>
    /// Tests array parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithTags(
        this IResourceBuilder<TestRedisResource> builder,
        string[] tags)
    {
        return builder;
    }

    /// <summary>
    /// Tests List&lt;T&gt; parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithLabels(
        this IResourceBuilder<TestRedisResource> builder,
        List<string> labels)
    {
        return builder;
    }

    /// <summary>
    /// Tests Dictionary parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithMetadata(
        this IResourceBuilder<TestRedisResource> builder,
        Dictionary<string, string> metadata)
    {
        return builder;
    }

    /// <summary>
    /// Tests IEnumerable&lt;T&gt; parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithItems(
        this IResourceBuilder<TestRedisResource> builder,
        IEnumerable<string> items)
    {
        return builder;
    }

    /// <summary>
    /// Tests nullable value type parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithTimeout(
        this IResourceBuilder<TestRedisResource> builder,
        int? timeoutSeconds = null)
    {
        return builder;
    }

    /// <summary>
    /// Tests multiple nullable value types.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithLimits(
        this IResourceBuilder<TestRedisResource> builder,
        int? maxConnections = null,
        double? memoryLimitMb = null,
        bool? enableLogging = null)
    {
        return builder;
    }

    /// <summary>
    /// Tests TimeSpan parameter (common .NET type).
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithExpiry(
        this IResourceBuilder<TestRedisResource> builder,
        TimeSpan expiry)
    {
        return builder;
    }

    /// <summary>
    /// Tests nullable TimeSpan parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithOptionalExpiry(
        this IResourceBuilder<TestRedisResource> builder,
        TimeSpan? expiry = null)
    {
        return builder;
    }

    /// <summary>
    /// Tests multi-type-parameter callback: Func&lt;T1, T2, TResult&gt;.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithTransform(
        this IResourceBuilder<TestRedisResource> builder,
        Func<string, int, string> transform)
    {
        return builder;
    }

    /// <summary>
    /// Tests Action with multiple parameters.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithMultiParamCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Action<string, int, bool> callback)
    {
        return builder;
    }

    /// <summary>
    /// Tests KeyValuePair parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithSetting(
        this IResourceBuilder<TestRedisResource> builder,
        KeyValuePair<string, string> setting)
    {
        return builder;
    }

    /// <summary>
    /// Tests Tuple parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithEndpointMapping(
        this IResourceBuilder<TestRedisResource> builder,
        (string name, int port) endpoint)
    {
        return builder;
    }

    /// <summary>
    /// Tests Uri parameter.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithProxyUrl(
        this IResourceBuilder<TestRedisResource> builder,
        Uri proxyUrl)
    {
        return builder;
    }

    /// <summary>
    /// Tests array of complex types.
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithCallbackContexts(
        this IResourceBuilder<TestRedisResource> builder,
        TestCallbackContext[] contexts)
    {
        return builder;
    }

    // ===== Additional Delegate Edge Cases =====

    /// <summary>
    /// Tests non-generic Action (no parameters).
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithSimpleCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Action callback)
    {
        callback?.Invoke();
        return builder;
    }

    /// <summary>
    /// Tests Func with no parameters (just return type).
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithValueProvider(
        this IResourceBuilder<TestRedisResource> builder,
        Func<string> valueProvider)
    {
        valueProvider?.Invoke();
        return builder;
    }

    /// <summary>
    /// Tests Func with no parameters returning Task (async factory).
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithAsyncValueProvider(
        this IResourceBuilder<TestRedisResource> builder,
        Func<Task<string>> asyncValueProvider)
    {
        asyncValueProvider?.Invoke();
        return builder;
    }

    /// <summary>
    /// Tests Action with 4 parameters (higher arity).
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithQuadCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Action<string, int, bool, double> callback)
    {
        return builder;
    }

    /// <summary>
    /// Tests Func with 4 parameters plus return (higher arity).
    /// </summary>
    public static IResourceBuilder<TestRedisResource> WithQuadTransform(
        this IResourceBuilder<TestRedisResource> builder,
        Func<string, int, bool, double, string> transform)
    {
        return builder;
    }

    // ===== Additional Test Cases for Full ATS Type Coverage =====

    /// <summary>
    /// Tests DTO parameter - verifies [AspireDto] generates TypeScript interface.
    /// </summary>
    [AspireExport(Description = "Configures the resource with a DTO")]
    public static IResourceBuilder<T> WithConfig<T>(
        this IResourceBuilder<T> builder,
        TestConfigDto config) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests mutable List return type - verifies AspireList wrapper generation.
    /// </summary>
    [AspireExport(Description = "Gets the tags for the resource")]
    public static List<string> GetTags(this IResourceBuilder<TestRedisResource> builder)
    {
        return [];
    }

    /// <summary>
    /// Tests mutable Dictionary return type - verifies AspireDict wrapper generation.
    /// </summary>
    [AspireExport(Description = "Gets the metadata for the resource")]
    public static Dictionary<string, string> GetMetadata(this IResourceBuilder<TestRedisResource> builder)
    {
        return [];
    }

    /// <summary>
    /// Tests ReferenceExpression parameter - verifies special handling (pass directly via toJSON).
    /// </summary>
    [AspireExport(Description = "Sets the connection string using a reference expression")]
    public static IResourceBuilder<T> WithConnectionString<T>(
        this IResourceBuilder<T> builder,
        ReferenceExpression connectionString) where T : IResourceWithConnectionString
    {
        return builder;
    }

    /// <summary>
    /// Tests callback receiving context wrapper.
    /// Verifies callback auto-wraps handle into context class with property-like objects.
    /// </summary>
    [AspireExport(Description = "Configures environment with callback (test version)")]
    public static IResourceBuilder<T> TestWithEnvironmentCallback<T>(
        this IResourceBuilder<T> builder,
        Func<TestEnvironmentContext, Task> callback) where T : IResourceWithEnvironment
    {
        return builder;
    }

    /// <summary>
    /// Tests DateTime parameter - verifies mapping to ISO 8601 string.
    /// </summary>
    [AspireExport(Description = "Sets the created timestamp")]
    public static IResourceBuilder<T> WithCreatedAt<T>(
        this IResourceBuilder<T> builder,
        DateTime createdAt) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests DateTimeOffset parameter - verifies mapping to ISO 8601 string.
    /// </summary>
    [AspireExport(Description = "Sets the modified timestamp")]
    public static IResourceBuilder<T> WithModifiedAt<T>(
        this IResourceBuilder<T> builder,
        DateTimeOffset modifiedAt) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests Guid parameter - verifies mapping to string.
    /// </summary>
    [AspireExport(Description = "Sets the correlation ID")]
    public static IResourceBuilder<T> WithCorrelationId<T>(
        this IResourceBuilder<T> builder,
        Guid correlationId) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests optional callback parameter - verifies conditional callback registration.
    /// </summary>
    [AspireExport(Description = "Configures with optional callback")]
    public static IResourceBuilder<T> WithOptionalCallback<T>(
        this IResourceBuilder<T> builder,
        Func<TestCallbackContext, Task>? callback = null) where T : IResource
    {
        callback?.Invoke(new TestCallbackContext());
        return builder;
    }

    /// <summary>
    /// Tests enum parameter - verifies string literal union generation.
    /// </summary>
    [AspireExport(Description = "Sets the resource status")]
    public static IResourceBuilder<T> WithStatus<T>(
        this IResourceBuilder<T> builder,
        TestResourceStatus status) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests nested DTO parameter.
    /// </summary>
    [AspireExport(Description = "Configures with nested DTO")]
    public static IResourceBuilder<T> WithNestedConfig<T>(
        this IResourceBuilder<T> builder,
        TestNestedDto config) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests async callback with context that returns a value.
    /// </summary>
    [AspireExport(Description = "Adds validation callback")]
    public static IResourceBuilder<T> WithValidator<T>(
        this IResourceBuilder<T> builder,
        Func<TestResourceContext, Task<bool>> validator) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests builder passed as parameter to another capability.
    /// Verifies wrapper class acceptance with internal handle extraction.
    /// </summary>
    [AspireExport(Description = "Waits for another resource (test version)")]
    public static IResourceBuilder<T> TestWaitFor<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<IResource> dependency) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests readonly array return type - verifies copy/pass directly.
    /// </summary>
    [AspireExport(Description = "Gets the endpoints")]
    public static string[] GetEndpoints(this IResourceBuilder<TestRedisResource> builder)
    {
        return [];
    }

    // ===== Polymorphism Pattern Tests =====

    /// <summary>
    /// Pattern 2: Tests interface type directly as target (NOT generic constraint).
    /// This targets IResourceWithConnectionString directly, not via generic parameter.
    /// Should expand to all types implementing IResourceWithConnectionString.
    /// </summary>
    [AspireExport(Description = "Sets connection string using direct interface target")]
    public static IResourceBuilder<IResourceWithConnectionString> WithConnectionStringDirect(
        IResourceBuilder<IResourceWithConnectionString> builder,
        string connectionString)
    {
        return builder;
    }

    /// <summary>
    /// Pattern 3: Tests concrete type with inheritance.
    /// This targets TestRedisResource directly (extends ContainerResource).
    /// Should expand to TestRedisResource AND any types that inherit from it.
    /// </summary>
    [AspireExport(Description = "Redis-specific configuration")]
    public static IResourceBuilder<TestRedisResource> WithRedisSpecific(
        IResourceBuilder<TestRedisResource> builder,
        string option)
    {
        return builder;
    }

    /// <summary>
    /// Pattern 4/5: Tests interface/concrete type as parameter (not target).
    /// The dependency parameter should generate a union type: Handle | ResourceBuilderBase.
    /// </summary>
    [AspireExport(Description = "Adds a dependency on another resource")]
    public static IResourceBuilder<T> WithDependency<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<IResourceWithConnectionString> dependency) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests an Aspire union parameter that includes an interface handle.
    /// The dependency parameter should generate a union type: string | ResourceBuilderBase.
    /// </summary>
    [AspireExport("withUnionDependency", Description = "Adds a dependency from a string or another resource")]
    public static IResourceBuilder<T> WithUnionDependency<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<IResourceWithConnectionString>))] object dependency) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests IReadOnlyList parameter - verifies readonly array handling.
    /// </summary>
    [AspireExport(Description = "Sets the endpoints")]
    public static IResourceBuilder<T> WithEndpoints<T>(
        this IResourceBuilder<T> builder,
        IReadOnlyList<string> endpoints) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests IReadOnlyDictionary parameter - verifies readonly dict handling.
    /// </summary>
    [AspireExport(Description = "Sets environment variables")]
    public static IResourceBuilder<T> WithEnvironmentVariables<T>(
        this IResourceBuilder<T> builder,
        IReadOnlyDictionary<string, string> variables) where T : IResourceWithEnvironment
    {
        return builder;
    }

    // ===== CancellationToken Tests =====

    /// <summary>
    /// Tests CancellationToken parameter - generated TypeScript should accept AbortSignal or CancellationToken for inputs.
    /// </summary>
    [AspireExport(Description = "Gets the status of the resource asynchronously")]
    public static Task<string> GetStatusAsync(
        this IResourceBuilder<TestRedisResource> builder,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("running");
    }

    /// <summary>
    /// Tests CancellationToken in callback parameter - generated TypeScript should materialize host values as CancellationToken.
    /// </summary>
    [AspireExport(Description = "Performs a cancellable operation")]
    public static IResourceBuilder<T> WithCancellableOperation<T>(
        this IResourceBuilder<T> builder,
        Func<CancellationToken, Task> operation) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Tests CancellationToken mixed with other parameters.
    /// </summary>
    [AspireExport(Description = "Waits for the resource to be ready")]
    public static Task<bool> WaitForReadyAsync(
        this IResourceBuilder<TestRedisResource> builder,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    // ===== Multi-Parameter Callback Tests =====

    /// <summary>
    /// Tests multi-parameter callback with handle types for destructuring codegen.
    /// </summary>
    [AspireExport(Description = "Tests multi-param callback destructuring")]
    public static IResourceBuilder<TestRedisResource> WithMultiParamHandleCallback(
        this IResourceBuilder<TestRedisResource> builder,
        Func<TestCallbackContext, TestEnvironmentContext, Task> callback)
    {
        return builder;
    }

    // ===== Options Interface Merging Tests =====

    /// <summary>
    /// WithDataVolume on TestRedisResource — has both name and isReadOnly parameters.
    /// Tests that options interfaces merge parameters across overloads targeting different types.
    /// </summary>
    [AspireExport(Description = "Adds a data volume with persistence")]
    public static IResourceBuilder<TestRedisResource> WithDataVolume(
        this IResourceBuilder<TestRedisResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        return builder;
    }

    /// <summary>
    /// WithDataVolume on TestDatabaseResource — has only name parameter.
    /// When combined with the TestRedisResource overload, the generated WithDataVolumeOptions
    /// interface must include both name and isReadOnly (the union of all parameters).
    /// </summary>
    [AspireExport(Description = "Adds a data volume")]
    public static IResourceBuilder<TestDatabaseResource> WithDataVolume(
        this IResourceBuilder<TestDatabaseResource> builder,
        string? name = null)
    {
        return builder;
    }

    // ===== Duplicate Class Name Tests =====

    /// <summary>
    /// Targets the concrete TestVaultResource so it gets a builder class named "TestVaultResource".
    /// </summary>
    [AspireExport(Description = "Adds a test vault resource")]
    public static IResourceBuilder<TestVaultResource> AddTestVault(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        return builder.AddResource(new TestVaultResource(name));
    }

    /// <summary>
    /// Directly targets the ITestVaultResource interface.
    /// DeriveClassName strips the 'I' prefix producing "TestVaultResource" — the same name
    /// as the concrete builder. The codegen must deduplicate to avoid emitting two classes.
    /// </summary>
    [AspireExport(Description = "Configures vault using direct interface target")]
    public static IResourceBuilder<ITestVaultResource> WithVaultDirect(
        IResourceBuilder<ITestVaultResource> builder,
        string option)
    {
        return builder;
    }

    // ===== SourceLocation Merge Tests =====
    // These test pairs share the same C# method name (SourceLocation) but differ by
    // a single required parameter. The codegen should merge them into one method with
    // that parameter made optional, and dispatch to the correct capability ID.

    /// <summary>
    /// Scenario 1: Single-param merge — one overload has name only, the other adds tag.
    /// Tests merge where the option variation is: single type vs tuple(type, type).
    /// </summary>
    [AspireExport(Description = "Adds a label to the resource")]
    public static IResourceBuilder<T> WithMergeLabel<T>(
        this IResourceBuilder<T> builder,
        string label) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Scenario 1b: Overload adds a category param — merge should make category optional.
    /// </summary>
    [AspireExport("withMergeLabelCategorized", Description = "Adds a categorized label to the resource")]
    public static IResourceBuilder<T> WithMergeLabel<T>(
        this IResourceBuilder<T> builder,
        string label,
        string category) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Scenario 2: Tuple-param merge — both have multiple required params, differ by one.
    /// Tests merge where the option variation is: tuple(type, type) vs tuple(type, type, type).
    /// </summary>
    [AspireExport(Description = "Configures a named endpoint")]
    public static IResourceBuilder<T> WithMergeEndpoint<T>(
        this IResourceBuilder<T> builder,
        string endpointName,
        int port) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Scenario 2b: Overload adds a scheme param — merge should make scheme optional.
    /// </summary>
    [AspireExport("withMergeEndpointScheme", Description = "Configures a named endpoint with scheme")]
    public static IResourceBuilder<T> WithMergeEndpoint<T>(
        this IResourceBuilder<T> builder,
        string endpointName,
        int port,
        string scheme) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Scenario 3: Dict/Parameters merge — both have required + optional params, differ by one.
    /// Tests merge where the option variation involves Parameters dict.
    /// </summary>
    [AspireExport(Description = "Configures resource logging")]
    public static IResourceBuilder<T> WithMergeLogging<T>(
        this IResourceBuilder<T> builder,
        string logLevel,
        bool enableConsole = true,
        int? maxFiles = null) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Scenario 3b: Overload adds a logPath param — merge should make logPath optional.
    /// </summary>
    [AspireExport("withMergeLoggingPath", Description = "Configures resource logging with file path")]
    public static IResourceBuilder<T> WithMergeLogging<T>(
        this IResourceBuilder<T> builder,
        string logLevel,
        string logPath,
        bool enableConsole = true,
        int? maxFiles = null) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Scenario 4: Both-dict merge — both overloads have 4+ required params so both use Parameters dicts.
    /// The shorter overload has 4 required params, the longer has 5.
    /// </summary>
    [AspireExport(Description = "Configures a route")]
    public static IResourceBuilder<T> WithMergeRoute<T>(
        this IResourceBuilder<T> builder,
        string path,
        string method,
        string handler,
        int priority) where T : IResource
    {
        return builder;
    }

    /// <summary>
    /// Scenario 4b: Overload adds a middleware param — merge should make middleware optional.
    /// Both variations will use Parameters dicts since they have 4+ required params.
    /// </summary>
    [AspireExport("withMergeRouteMiddleware", Description = "Configures a route with middleware")]
    public static IResourceBuilder<T> WithMergeRoute<T>(
        this IResourceBuilder<T> builder,
        string path,
        string method,
        string handler,
        int priority,
        string middleware) where T : IResource
    {
        return builder;
    }
}

/// <summary>
/// Test persistence mode enum.
/// </summary>
public enum TestPersistenceMode
{
    None,
    Volume,
    Bind
}

/// <summary>
/// Test persistence annotation.
/// </summary>
public class TestPersistenceAnnotation : IResourceAnnotation
{
    public TestPersistenceAnnotation(TestPersistenceMode mode)
    {
        Mode = mode;
    }

    public TestPersistenceMode Mode { get; }
}

/// <summary>
/// Test callback context for WithCustomCallback.
/// Also used to verify [AspireExport(ExposeProperties = true)] scanning.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class TestCallbackContext
{
    public string? Name { get; set; }
    public int Value { get; set; }

    /// <summary>
    /// CancellationToken is supported by ATS.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }
}
