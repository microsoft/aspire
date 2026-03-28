// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceDependencyTests
{
    [Fact]
    public async Task DirectReferenceViaWithReferenceIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddRedis("redis");
        var container = builder.AddContainer("container", "alpine")
            .WithReference(redis);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(redis.Resource, dependencies);
    }

    [Fact]
    public async Task EndpointReferenceViaWithEnvironmentIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var api = builder.AddContainer("api", "alpine")
            .WithHttpEndpoint(5000, 5000, "http");

        var frontend = builder.AddContainer("frontend", "alpine")
            .WithEnvironment("API_URL", api.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await frontend.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(api.Resource, dependencies);
    }

    [Fact]
    public async Task EndpointPropertyReferenceIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var api = builder.AddContainer("api", "alpine")
            .WithHttpEndpoint(5000, 5000, "http");

        var frontend = builder.AddContainer("frontend", "alpine")
            .WithEnvironment("API_PORT", api.GetEndpoint("http").Property(EndpointProperty.Port));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await frontend.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(api.Resource, dependencies);
    }

    [Fact]
    public async Task ConnectionStringReferenceIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddPostgres("postgres");
        var db = postgres.AddDatabase("db");

        var container = builder.AddContainer("container", "alpine")
            .WithReference(db);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(db.Resource, dependencies);
        Assert.Contains(postgres.Resource, dependencies); // Parent of db
    }

    [Fact]
    public async Task ConnectionStringRedirectIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddRedis("redis");
        var redirect = builder.AddRedis("redirect")
            .WithConnectionStringRedirection(redis.Resource);

        var container = builder.AddContainer("container", "alpine")
            .WithReference(redirect);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(redirect.Resource, dependencies);
        Assert.Contains(redis.Resource, dependencies); // The redirect target
    }

    [Fact]
    public async Task ParentRelationshipIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddPostgres("postgres");
        var db = postgres.AddDatabase("db");

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await db.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(postgres.Resource, dependencies);
    }

    [Fact]
    public async Task WaitForDependencyIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddRedis("redis");
        var container = builder.AddContainer("container", "alpine")
            .WaitFor(redis);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(redis.Resource, dependencies);
    }

    [Fact]
    public async Task WaitForCompletionDependencyIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var initContainer = builder.AddContainer("init", "alpine");
        var mainContainer = builder.AddContainer("main", "alpine")
            .WaitForCompletion(initContainer);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await mainContainer.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(initContainer.Resource, dependencies);
    }

    [Fact]
    public async Task ParameterInEnvironmentIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var param = builder.AddParameter("apiKey");
        var container = builder.AddContainer("container", "alpine")
            .WithEnvironment("API_KEY", param);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(param.Resource, dependencies);
    }

    [Fact]
    public async Task ParameterInArgsIsIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var param = builder.AddParameter("config");
        var exe = builder.AddExecutable("app", "myapp", ".")
            .WithArgs(param);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await exe.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(param.Resource, dependencies);
    }

    [Fact]
    public async Task ReferenceExpressionWithMultipleResourcesIncludesAll()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var host = builder.AddParameter("host");
        var port = builder.AddParameter("port");
        var password = builder.AddParameter("password", secret: true);

        var container = builder.AddContainer("container", "alpine")
            .WithEnvironment("CONNECTION", ReferenceExpression.Create($"Host={host};Port={port};Password={password}"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(host.Resource, dependencies);
        Assert.Contains(port.Resource, dependencies);
        Assert.Contains(password.Resource, dependencies);
    }

    [Fact]
    public async Task TransitiveDependenciesAreIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C via WaitFor
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WaitFor(c);
        var a = builder.AddContainer("a", "alpine")
            .WaitFor(b);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
    }

    [Fact]
    public async Task TransitiveDependenciesUsingArgsAreInclude()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C via Args
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithArgs(c);
        var a = builder.AddContainer("a", "alpine")
            .WithArgs(b);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
    }

    [Fact]
    public async Task TransitiveDependenciesUsingEnvironmentAreIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C via Environment
        var c = builder.AddRedis("c")
            .WithHttpEndpoint(6379, 6379, "redisc");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(8080, 8080, "httpb")
            .WithEnvironment("C_HOST", c.GetEndpoint("redisc"));
        var a = builder.AddContainer("a", "alpine")
            .WithEnvironment("B_HOST", b.GetEndpoint("httpb"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
    }

    [Fact]
    public async Task DiamondDependenciesAreDeduplicatedAndIncludeAll()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> D, A -> C -> D via WaitFor
        var d = builder.AddContainer("d", "alpine");
        var b = builder.AddContainer("b", "alpine").WaitFor(d);
        var c = builder.AddContainer("c", "alpine").WaitFor(d);
        var a = builder.AddContainer("a", "alpine")
            .WaitFor(b)
            .WaitFor(c);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
        Assert.Contains(d.Resource, dependencies);
        Assert.Equal(3, dependencies.Count); // D only appears once
    }

    [Fact]
    public async Task DeepChainDependenciesAreIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C -> D -> E via WaitFor
        var e = builder.AddRedis("e");
        var d = builder.AddContainer("d", "alpine").WaitFor(e);
        var c = builder.AddContainer("c", "alpine").WaitFor(d);
        var b = builder.AddContainer("b", "alpine").WaitFor(c);
        var a = builder.AddContainer("a", "alpine").WaitFor(b);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
        Assert.Contains(d.Resource, dependencies);
        Assert.Contains(e.Resource, dependencies);
    }

    [Fact]
    public async Task CircularReferencesAreHandled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C -> D -> B via Endpoint references
        var b = builder.AddContainer("b", "alpine");
        var c = builder.AddContainer("c", "alpine")
            .WithEnvironment("B_URL", b.GetEndpoint("http"));
        var d = builder.AddContainer("d", "alpine")
            .WithEnvironment("C_URL", c.GetEndpoint("http"));
        b.WithEnvironment("D_URL", d.GetEndpoint("http")); // Completes the cycle
        var a = builder.AddContainer("a", "alpine")
            .WithEnvironment("B_URL", b.GetEndpoint("http"));
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);
        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
        Assert.Contains(d.Resource, dependencies);
        Assert.Equal(3, dependencies.Count); // Each resource only appears once
    }

    [Fact]
    public async Task MixedReferenceTypesToSameResourceIsDeduplicatedAndIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Use a container instead of Redis to avoid auto-generated password parameter
        var backend = builder.AddContainer("backend", "alpine")
            .WithHttpEndpoint(8080, 8080, "http");

        var frontend = builder.AddContainer("frontend", "alpine")
            .WithEnvironment("BACKEND_URL", backend.GetEndpoint("http"))  // Endpoint reference
            .WaitFor(backend);                                            // Also wait for it

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await frontend.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(backend.Resource, dependencies);
        Assert.Single(dependencies); // Backend should only appear once despite multiple reference types
    }

    [Fact]
    public async Task WaitForChildAlsoIncludesParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddPostgres("postgres");
        var db = postgres.AddDatabase("db");

        var container = builder.AddContainer("container", "alpine")
            .WaitFor(db);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(db.Resource, dependencies);
        Assert.Contains(postgres.Resource, dependencies); // Parent included due to transitive dependency
    }

    [Fact]
    public async Task UnrelatedResourceIsNotIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddRedis("redis");
        var unrelatedResource = builder.AddRedis("unrelated");
        var container = builder.AddContainer("container", "alpine")
            .WithReference(redis);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.DoesNotContain(unrelatedResource.Resource, dependencies);
    }

    [Fact]
    public async Task InputResourceIsExcludedFromOwnDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "alpine");

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.DoesNotContain(container.Resource, dependencies);
    }

    [Fact]
    public async Task ResourceThatDependsOnInputIsNotIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "alpine")
            .WithHttpEndpoint(5000, 5000, "http");
        var dependentContainer = builder.AddContainer("dependent", "alpine")
            .WithReference(container.GetEndpoint("http")); // Reverse direction

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.DoesNotContain(dependentContainer.Resource, dependencies);
    }

    [Fact]
    public async Task SiblingResourceUnderSameParentIsNotIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var postgres = builder.AddPostgres("postgres");
        var db1 = postgres.AddDatabase("db1");
        var db2 = postgres.AddDatabase("db2");

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await db1.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.DoesNotContain(db2.Resource, dependencies);
        Assert.Contains(postgres.Resource, dependencies); // Parent IS included
    }

    [Fact]
    public async Task ResourceOnlyReferencedByThirdResourceIsNotIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A references B, C references D. D should not be in A's dependencies.
        var d = builder.AddRedis("d");
        var c = builder.AddContainer("c", "alpine").WithReference(d);
        var b = builder.AddRedis("b");
        var a = builder.AddContainer("a", "alpine").WithReference(b);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.DoesNotContain(c.Resource, dependencies);
        Assert.DoesNotContain(d.Resource, dependencies);
    }

    [Fact]
    public async Task ResourceWithZeroDependenciesReturnsEmptySet()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "alpine");

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Empty(dependencies);
    }

    [Fact]
    public async Task MultipleWaitAnnotationsForSameTargetAreDeduplicatedAndIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddContainer("redis", "redis");
        var container = builder.AddContainer("container", "alpine")
            .WaitFor(redis)
            .WaitFor(redis); // Add wait twice

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(redis.Resource, dependencies);
        Assert.Single(dependencies);
    }

    [Fact]
    public async Task DirectOnlyExcludesTransitiveDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Chain: A -> B -> C
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(5000, 5000, "http")
            .WithReference(c);
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.DirectOnly);

        Assert.Contains(b.Resource, dependencies);
        Assert.DoesNotContain(c.Resource, dependencies);
    }

    [Fact]
    public async Task TransitiveClosureIncludesAllDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Chain: A -> B -> C
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(5000, 5000, "http")
            .WithReference(c);
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.Recursive);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
    }

    [Fact]
    public async Task DirectOnlyWithDeepChainOnlyIncludesDirectDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Chain: A -> B -> C -> D -> E
        var e = builder.AddRedis("e");
        var d = builder.AddContainer("d", "alpine")
            .WithHttpEndpoint(5003, 5003, "http")
            .WithReference(e);
        var c = builder.AddContainer("c", "alpine")
            .WithHttpEndpoint(5002, 5002, "http")
            .WithReference(d.GetEndpoint("http"));
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(5001, 5001, "http")
            .WithReference(c.GetEndpoint("http"));
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.DirectOnly);

        Assert.Single(dependencies);
        Assert.Contains(b.Resource, dependencies);
    }

    [Fact]
    public async Task DirectOnlyIncludesReferencedResourcesFromConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A references database, which has postgres as parent.
        // The database's connection string expression references postgres.
        var postgres = builder.AddPostgres("postgres");
        var db = postgres.AddDatabase("db");
        var a = builder.AddContainer("a", "alpine").WithReference(db);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.DirectOnly);

        // With DirectOnly, we get both db and postgres because db's ConnectionStringExpression
        // references postgres, and that reference is discovered while traversing a's environment.
        Assert.Contains(db.Resource, dependencies);
        Assert.Contains(postgres.Resource, dependencies);
    }

    [Fact]
    public async Task DirectOnlyDoesNotIncludeWaitForDependenciesFromReferencedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Chain: A -> (ref) B -> (waitfor) C
        // A has WithReference(B) and WaitFor(B)
        // B has WaitFor(C) but A does NOT reference C directly
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(5000, 5000, "http")
            .WaitFor(c);
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"))
            .WaitFor(b);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext, ResourceDependencyDiscoveryMode.DirectOnly);

        // A depends on B (via WithReference and WaitFor)
        Assert.Contains(b.Resource, dependencies);
        // A should NOT depend on C because C is only a WaitFor dependency of B, not of A
        Assert.DoesNotContain(c.Resource, dependencies);
    }

    [Fact]
    public async Task DefaultOverloadUsesTransitiveClosure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Chain: A -> B -> C
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(5000, 5000, "http")
            .WithReference(c);
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Default overload should include transitive dependencies
        var dependencies = await a.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(c.Resource, dependencies);
    }

    #region Multi-Resource GetDependenciesAsync Tests

    [Fact]
    public async Task MultiResourceIndependentResourcesDependenciesAreMerged()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> X, B -> Y (independent dependencies)
        var x = builder.AddContainer("x", "alpine")
            .WithHttpEndpoint(8001, 8001, "http");
        var y = builder.AddContainer("y", "alpine")
            .WithHttpEndpoint(8002, 8002, "http");
        var a = builder.AddContainer("a", "alpine").WithEnvironment("X_URL", x.GetEndpoint("http"));
        var b = builder.AddContainer("b", "alpine").WithEnvironment("Y_URL", y.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, b.Resource], executionContext);

        Assert.Contains(x.Resource, dependencies);
        Assert.Contains(y.Resource, dependencies);
        Assert.Equal(2, dependencies.Count);
    }

    [Fact]
    public async Task MultiResourceOverlappingDependenciesAreDeduplicatedAndCombined()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> X, B -> X (shared dependency)
        var x = builder.AddContainer("x", "alpine")
            .WithHttpEndpoint(8001, 8001, "http");
        var a = builder.AddContainer("a", "alpine").WithEnvironment("X_URL", x.GetEndpoint("http"));
        var b = builder.AddContainer("b", "alpine").WithEnvironment("X_URL", x.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, b.Resource], executionContext);

        Assert.Contains(x.Resource, dependencies);
        Assert.Single(dependencies); // X should only appear once
    }

    [Fact]
    public async Task MultiResourceInputResourceExcludedEvenIfOtherInputDependsOnIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B, but both A and B are inputs
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(8080, 8080, "http");
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, b.Resource], executionContext);

        // B should be excluded because it's an input resource
        Assert.DoesNotContain(a.Resource, dependencies);
        Assert.DoesNotContain(b.Resource, dependencies);
        Assert.Empty(dependencies);
    }

    [Fact]
    public async Task MultiResourceTransitiveDependencyThroughInputIsStillIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C, inputs are [A, B]
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(8080, 8080, "http")
            .WithReference(c);
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, b.Resource], executionContext);

        // C should be included (as dependency of B)
        Assert.Contains(c.Resource, dependencies);
        // A and B are inputs, so excluded
        Assert.DoesNotContain(a.Resource, dependencies);
        Assert.DoesNotContain(b.Resource, dependencies);
    }

    [Fact]
    public async Task MultiResourceEmptyInputReturnsEmptySet()
    {
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            Array.Empty<IResource>(), executionContext);

        Assert.Empty(dependencies);
    }

    [Fact]
    public async Task MultiResourceAllInputsHaveNoDependenciesReturnsEmptySet()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var a = builder.AddContainer("a", "alpine");
        var b = builder.AddContainer("b", "alpine");
        var c = builder.AddContainer("c", "alpine");

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, b.Resource, c.Resource], executionContext);

        Assert.Empty(dependencies);
    }

    [Fact]
    public async Task MultiResourceDiamondWithMultipleInputsHandledCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Diamond: A -> B -> D, A -> C -> D
        // Input: [A, C] - should get B, D (C is excluded as input)
        var d = builder.AddContainer("d", "alpine")
            .WithHttpEndpoint(8080, 8080, "http");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(8081, 8081, "http")
            .WaitFor(d);
        var c = builder.AddContainer("c", "alpine")
            .WithHttpEndpoint(8082, 8082, "http")
            .WaitFor(d);
        var a = builder.AddContainer("a", "alpine")
            .WaitFor(b)
            .WaitFor(c);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, c.Resource], executionContext);

        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(d.Resource, dependencies);
        Assert.DoesNotContain(a.Resource, dependencies);
        Assert.DoesNotContain(c.Resource, dependencies);
        Assert.Equal(2, dependencies.Count);
    }

    [Fact]
    public async Task MultiResourceDirectOnlyModeWithMultipleResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C, D -> E -> F
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(8080, 8080, "http")
            .WithReference(c);
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var f = builder.AddRedis("f");
        var e = builder.AddContainer("e", "alpine")
            .WithHttpEndpoint(8081, 8081, "http")
            .WithReference(f);
        var d = builder.AddContainer("d", "alpine")
            .WithReference(e.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, d.Resource], executionContext, new ResourceDependencyDiscoveryOptions
            {
                DiscoveryMode = ResourceDependencyDiscoveryMode.DirectOnly
            });

        // DirectOnly should only include B and E (direct deps), not C and F
        Assert.Contains(b.Resource, dependencies);
        Assert.Contains(e.Resource, dependencies);
        Assert.DoesNotContain(c.Resource, dependencies);
        Assert.DoesNotContain(f.Resource, dependencies);
        Assert.Equal(2, dependencies.Count);
    }

    [Fact]
    public async Task MultiResourceCircularReferenceAmongInputsHandledCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A -> B -> C -> A (circular), plus D as external dependency
        var d = builder.AddContainer("d", "alpine")
            .WithHttpEndpoint(8083, 8083, "http"); ;
        var a = builder.AddContainer("a", "alpine")
            .WithHttpEndpoint(8080, 8080, "http");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(8081, 8081, "http")
            .WithEnvironment("A_URL", a.GetEndpoint("http"));
        var c = builder.AddContainer("c", "alpine")
            .WithHttpEndpoint(8082, 8082, "http")
            .WithEnvironment("B_URL", b.GetEndpoint("http"))
            .WithEnvironment("D_URL", d.GetEndpoint("http"));
        a.WithEnvironment("C_URL", c.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        // All three circular resources as input
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, b.Resource, c.Resource], executionContext);

        // Only D should remain as a dependency (A, B, C are all inputs)
        Assert.Contains(d.Resource, dependencies);
        Assert.DoesNotContain(a.Resource, dependencies);
        Assert.DoesNotContain(b.Resource, dependencies);
        Assert.DoesNotContain(c.Resource, dependencies);
        Assert.Single(dependencies);
    }

    [Fact]
    public async Task MultiResourceParentChildBothAsInputsExcludesBoth()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Simulate parent-child-like relationship using WaitFor
        var parent = builder.AddContainer("parent", "alpine");
        var child = builder.AddContainer("child", "alpine")
            .WaitFor(parent);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        // Both parent and child as inputs
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [parent.Resource, child.Resource], executionContext);

        // Both are inputs, so neither should appear
        Assert.DoesNotContain(parent.Resource, dependencies);
        Assert.DoesNotContain(child.Resource, dependencies);
        Assert.Empty(dependencies);
    }

    [Fact]
    public async Task MultiResourceCombinesDependenciesFromDifferentSourceTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A uses WaitFor(X), B uses WithReference(Y), C uses WithEnvironment(Z endpoint)
        var x = builder.AddContainer("x", "alpine");
        var y = builder.AddContainer("y", "alpine")
            .WithHttpEndpoint(8001, 8001, "http");
        var z = builder.AddContainer("z", "alpine")
            .WithHttpEndpoint(8080, 8080, "http");

        var a = builder.AddContainer("a", "alpine").WaitFor(x);
        var b = builder.AddContainer("b", "alpine")
            .WithEnvironment("Y_URL", y.GetEndpoint("http"));
        var c = builder.AddContainer("c", "alpine")
            .WithEnvironment("Z_URL", z.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await ResourceExtensions.GetDependenciesAsync(
            [a.Resource, b.Resource, c.Resource], executionContext);

        Assert.Contains(x.Resource, dependencies);
        Assert.Contains(y.Resource, dependencies);
        Assert.Contains(z.Resource, dependencies);
        Assert.Equal(3, dependencies.Count);
    }

    [Fact]
    public async Task MultiResourceSingleResourceBehavesLikeSingleResourceMethod()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Chain: A -> B -> C
        var c = builder.AddRedis("c");
        var b = builder.AddContainer("b", "alpine")
            .WithHttpEndpoint(5000, 5000, "http")
            .WithReference(c);
        var a = builder.AddContainer("a", "alpine")
            .WithReference(b.GetEndpoint("http"));

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        // Compare single-resource method with multi-resource method using single input
        var singleDeps = await a.Resource.GetResourceDependenciesAsync(executionContext);
        var multiDeps = await ResourceExtensions.GetDependenciesAsync([a.Resource], executionContext);

        Assert.Equal(singleDeps.Count, multiDeps.Count);
        foreach (var dep in singleDeps)
        {
            Assert.Contains(dep, multiDeps);
        }
    }

    #endregion

    [Fact]
    public async Task ConditionalReferenceExpressionIncludesBothBranchDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var enableTls = builder.AddParameter("enable-tls");
        var tlsSuffix = builder.AddParameter("tls-suffix");

        var conditional = ReferenceExpression.CreateConditional(
            enableTls.Resource,
            bool.TrueString,
            ReferenceExpression.Create($"{tlsSuffix}"),
            ReferenceExpression.Create($",ssl=false"));

        var container = builder.AddContainer("container", "alpine")
            .WithEnvironment("TLS_SUFFIX", conditional);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(enableTls.Resource, dependencies);
        Assert.Contains(tlsSuffix.Resource, dependencies);
    }

    [Fact]
    public async Task ConditionalReferenceExpressionWithEndpointReferencesIncludesAll()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var flag = builder.AddParameter("use-primary");
        var primary = builder.AddContainer("primary", "alpine")
            .WithHttpEndpoint(5000, 5000, "http");
        var secondary = builder.AddContainer("secondary", "alpine")
            .WithHttpEndpoint(5001, 5001, "http");

        var conditional = ReferenceExpression.CreateConditional(
            flag.Resource,
            bool.TrueString,
            ReferenceExpression.Create($"{primary.GetEndpoint("http")}"),
            ReferenceExpression.Create($"{secondary.GetEndpoint("http")}"));

        var container = builder.AddContainer("frontend", "alpine")
            .WithEnvironment("BACKEND_URL", conditional);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(flag.Resource, dependencies);
        Assert.Contains(primary.Resource, dependencies);
        Assert.Contains(secondary.Resource, dependencies);
    }

    [Fact]
    public async Task NestedConditionalReferenceExpressionIncludesAllTransitiveDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var outerFlag = builder.AddParameter("outer-flag");
        var innerFlag = builder.AddParameter("inner-flag");
        var paramA = builder.AddParameter("param-a");
        var paramB = builder.AddParameter("param-b");
        var paramC = builder.AddParameter("param-c");

        var innerConditional = ReferenceExpression.CreateConditional(
            innerFlag.Resource,
            bool.TrueString,
            ReferenceExpression.Create($"{paramA}"),
            ReferenceExpression.Create($"{paramB}"));

        var outerConditional = ReferenceExpression.CreateConditional(
            outerFlag.Resource,
            bool.TrueString,
            innerConditional,
            ReferenceExpression.Create($"{paramC}"));

        var container = builder.AddContainer("container", "alpine")
            .WithEnvironment("VALUE", outerConditional);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        Assert.Contains(outerFlag.Resource, dependencies);
        Assert.Contains(innerFlag.Resource, dependencies);
        Assert.Contains(paramA.Resource, dependencies);
        Assert.Contains(paramB.Resource, dependencies);
        Assert.Contains(paramC.Resource, dependencies);
    }

    [Fact]
    public async Task DuplicateConditionalExpressionDependenciesAreDeduplicatedAndIncluded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var flag = builder.AddParameter("flag");
        var param = builder.AddParameter("value");

        var conditional = ReferenceExpression.CreateConditional(
            flag.Resource,
            bool.TrueString,
            ReferenceExpression.Create($"{param}"),
            ReferenceExpression.Create($"default"));

        var container = builder.AddContainer("container", "alpine")
            .WithEnvironment("VAR1", conditional)
            .WithEnvironment("VAR2", conditional);

        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var dependencies = await container.Resource.GetResourceDependenciesAsync(executionContext);

        // Both env vars reference the same resources — dependencies should be deduplicated
        Assert.Contains(flag.Resource, dependencies);
        Assert.Contains(param.Resource, dependencies);
    }
}
