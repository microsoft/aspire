// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Tests for https://github.com/microsoft/aspire/issues/18935.
///
/// Backing resources (caches, databases, queues) are provisioned by a Radius <em>recipe</em>, not as
/// a <c>Radius.Compute/containers</c> workload. The recipe owns the Kubernetes Service name and the
/// credentials, so the container recipe's <c>{name}-{name}</c> ClusterIP rule - and Aspire's own
/// generated password parameter - are both wrong for them.
///
/// Every consumer-visible value is therefore projected from the backing resource's own Radius
/// construct: <c>properties.host</c>/<c>properties.port</c> for the address, and either
/// <c>listSecrets()</c> (legacy <c>Applications.*</c> types) or the schema properties Aspire itself
/// supplies (<c>Radius.*</c> UDTs) for credentials. Because the substitution happens at the value
/// level, connection strings, URIs and splatted connection properties all compose correctly without
/// this package duplicating any connection-string format.
/// </summary>
public class BackingResourceProjectionTests
{
    private static string GenerateBicep(Action<IDistributedApplicationBuilder> configure, string environmentName = "myenv")
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment(environmentName);
        configure(builder);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        return context.GenerateBicep(model);
    }

    /// <summary>
    /// The full blast radius of the bug in one snapshot: a container referencing Redis, a Postgres
    /// database, MongoDB, RabbitMQ and SQL Server. A single <c>WithReference</c> emits both
    /// <c>ConnectionStrings__x</c> and the splatted <c>X_HOST</c>/<c>X_PORT</c>/<c>X_PASSWORD</c>/
    /// <c>X_URI</c> properties, so all of them must be projected, not just the connection string.
    /// Also pins the required schema properties the UDT types take.
    /// </summary>
    [Fact]
    public Task AllBackingResourceTypes_ProjectRecipeOutputs()
    {
        var bicep = GenerateBicep(b =>
        {
            var cache = b.AddRedis("cache");
            var pgdb = b.AddPostgres("pg").AddDatabase("pgdb");
            var mongo = b.AddMongoDB("mongo");
            var rabbit = b.AddRabbitMQ("rabbit", userName: b.AddParameter("rabbituser"));
            var sql = b.AddSqlServer("sqlserver");

            b.AddContainer("api", "myapp/api", "latest")
                .WithHttpEndpoint(targetPort: 8080)
                .WithReference(cache)
                .WithReference(pgdb)
                .WithReference(mongo)
                .WithReference(rabbit)
                .WithReference(sql);
        });

        // The snapshot is the assertion: it pins the whole emitted document, so an endpoint-derived
        // `{name}-{name}` Kubernetes FQDN reappearing for any backing resource shows up as a diff.
        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// A directly-referenced endpoint property (rather than a whole connection string) goes through
    /// the same projection. <c>Port</c> is included because it never evaluates the lazy host
    /// expression, so guarding only the host would let a wrong value through.
    /// </summary>
    [Fact]
    public Task DirectEndpointPropertyReference_ProjectsRecipeOutputs()
    {
        var bicep = GenerateBicep(b =>
        {
            var cache = b.AddRedis("cache");
            b.AddContainer("api", "myapp/api", "latest")
                .WithHttpEndpoint(targetPort: 8080)
                .WithEnvironment("CUSTOM_HOST", cache.GetEndpoint("tcp").Property(EndpointProperty.Host))
                .WithEnvironment("CUSTOM_PORT", cache.GetEndpoint("tcp").Property(EndpointProperty.Port))
                .WithEnvironment("CUSTOM_URL", cache.GetEndpoint("tcp").Property(EndpointProperty.Url));
        });

        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// Container-to-container service discovery still uses the <c>{name}-{name}</c> ClusterIP rule
    /// the container recipe creates. Only backing resources are re-routed.
    /// </summary>
    [Fact]
    public void ContainerToContainerServiceDiscovery_IsUnchanged()
    {
        var bicep = GenerateBicep(b =>
        {
            var backend = b.AddContainer("backend", "myapp/backend", "latest")
                .WithHttpEndpoint(targetPort: 8080);

            b.AddContainer("api", "myapp/api", "latest")
                .WithHttpEndpoint(targetPort: 8080)
                .WithEnvironment("BACKEND_HOST", backend.GetEndpoint("http").Property(EndpointProperty.Host));
        });

        Assert.Contains("backend-backend.default.svc.cluster.local", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Aspire's generated password is meaningless for a legacy backing resource - the recipe
    /// generates its own, or (Redis) provisions none at all - so the parameter must not be emitted
    /// as the value a consumer reads.
    /// </summary>
    /// <remarks>
    /// Redis is deliberately the subject because it is the strongest form of the guarantee: its
    /// recipe deploys an unauthenticated server, so the correct emitted credential is empty and any
    /// `cache_password` in the document would be a leak of a run-mode-only value. Read the snapshot
    /// with that in mind - the empty `password=` in `ConnectionStrings__cache` is the *projected*
    /// value, not a redaction, and this file therefore says nothing about the `listSecrets()` path.
    /// That path is pinned for the types that do have a recipe-generated credential by
    /// <see cref="AllBackingResourceTypes_ProjectRecipeOutputs"/> (Mongo, SQL Server) and
    /// asserted directly - so an upstream change to a connection-string format cannot silently
    /// erase it - by <c>BackingResourceValueResolutionTests.UriFormattedValues_AreEscapedInTheEmittedBicep</c>
    /// and <c>BackingResourceValueResolutionTests.RedisPasswordIsNotProjected_BecauseTheRecipeDeploysAnUnauthenticatedServer</c>.
    /// </remarks>
    [Fact]
    public Task UnauthenticatedBackingResource_DoesNotLeakAspireGeneratedPasswordParameter()
    {
        var bicep = GenerateBicep(b =>
        {
            var cache = b.AddRedis("cache");
            b.AddContainer("api", "myapp/api", "latest").WithReference(cache);
        });

        // Snapshotted rather than asserted with DoesNotContain: the guarantee is about the whole
        // document (no `param cache_password` anywhere, and every consumer value reading
        // listSecrets()), which a substring absence check cannot express.
        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// A backing resource deployed by a different Radius environment has no construct in this
    /// document to project from, and another deployment's recipe outputs are not reachable from
    /// this Bicep. That must fail loudly rather than emit an unresolvable address - and in
    /// particular must not be swallowed by the publish-time "skip unresolvable endpoint" path.
    /// </summary>
    [Fact]
    public void BackingResourceInDifferentEnvironment_FailsWithActionableMessage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var envA = builder.AddRadiusEnvironment("enva");
        var envB = builder.AddRadiusEnvironment("envb");

        var cache = builder.AddRedis("cache").WithComputeEnvironment(envB);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithComputeEnvironment(envA)
            .WithReference(cache);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnvA = model.Resources.OfType<RadiusEnvironmentResource>().First(e => e.Name == "enva");
        RadiusTestHelper.AttachDeploymentTargets(radiusEnvA, model);
        var context = new RadiusBicepPublishingContext(radiusEnvA);

        // The concrete type matters: the publisher's env-var loop skips values that raise
        // RadiusUnresolvableValueException, so this must not be one of those.
        var ex = Assert.Throws<RadiusBackingResourceEndpointException>(() => context.GenerateBicep(model));
        Assert.Equal("cache", ex.Resource.Name);
        Assert.Contains("same Radius environment", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ASPIRERADIUS069", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Radius recipe provisions a single database, so a server resource with two databases cannot
    /// be satisfied. Emitting one would leave consumers of the other pointed at a database that
    /// does not exist.
    /// </summary>
    [Fact]
    public void MultipleDatabasesOnOneBackingResource_FailsWithActionableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(b =>
        {
            var pg = b.AddPostgres("pg");
            var first = pg.AddDatabase("first");
            var second = pg.AddDatabase("second");
            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(first)
                .WithReference(second);
        }));

        Assert.Contains("single database", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ASPIRERADIUS072", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each Radius recipe manages its own credential, so a password parameter shared by two backing
    /// resources has no single correct substitution. Silently picking one would hand a consumer the
    /// other resource's password.
    /// </summary>
    [Fact]
    public void SharedPasswordParameterAcrossBackingResources_FailsWithActionableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GenerateBicep(b =>
        {
            var shared = b.AddParameter("shared", secret: true);
            var sqlA = b.AddSqlServer("sqla", password: shared);
            var sqlB = b.AddSqlServer("sqlb", password: shared);
            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(sqlA)
                .WithReference(sqlB);
        }));

        Assert.Contains("its own parameter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ASPIRERADIUS070", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Projected values name a backing resource by Bicep identifier, so a
    /// <c>ConfigureRadiusInfrastructure</c> callback that renames the resource must not leave the
    /// environment variables pointing at a symbol that no longer exists.
    /// </summary>
    [Fact]
    public Task CallbackRenamingBackingResource_RewiresProjectedEnvironmentValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.ResourceTypeInstances.Single().BicepIdentifier = "renamed_cache";
            });

        var cache = builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest").WithReference(cache);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        // Snapshotted so the assertion covers every rewritten value, not just the two that were
        // spot-checked; a value left pointing at the old `cache` symbol would show as a diff.
        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// Removing a backing resource that a container's environment reads from leaves those values
    /// pointing at a symbol that no longer exists, which must fail rather than emit invalid Bicep.
    /// </summary>
    [Fact]
    public void CallbackRemovingBackingResource_FailsWithActionableMessage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts => opts.ResourceTypeInstances.Clear());

        var cache = builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest").WithReference(cache);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("removed or replaced that resource", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ASPIRERADIUS074", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A callback that both renames a backing resource and sets the environment value itself must
    /// keep the callback's value - <c>ConfigureRadiusInfrastructure</c> is last-write-wins.
    /// </summary>
    [Fact]
    public void CallbackOverridingProjectedValue_IsNotRebuilt()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .ConfigureRadiusInfrastructure(opts =>
            {
                opts.ResourceTypeInstances.Single().BicepIdentifier = "renamed_cache";
                opts.Containers.Single().Env["CACHE_HOST"] = new ContainerEnvVarConstruct { Value = "explicit-host" };
            });

        var cache = builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest").WithReference(cache);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        Assert.Contains("'explicit-host'", bicep, StringComparison.Ordinal);
        // Values the callback did not touch are still repaired.
        Assert.Contains("renamed_cache.properties.port", bicep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two UDT resources may legitimately share a password parameter: the same parameter is passed
    /// into both recipes, so every consumer reads back the value Aspire supplied. Only sharing that
    /// involves a recipe-generated (<c>listSecrets()</c>) credential is ambiguous.
    /// </summary>
    [Fact]
    public void SharedPasswordParameterAcrossUdtResources_IsAllowed()
    {
        var bicep = GenerateBicep(b =>
        {
            var shared = b.AddParameter("shared", secret: true);
            var pgA = b.AddPostgres("pga", password: shared);
            var pgB = b.AddPostgres("pgb", password: shared);
            b.AddContainer("api", "myapp/api", "latest")
                .WithReference(pgA)
                .WithReference(pgB);
        });

        Assert.Contains("pga.properties.host", bicep, StringComparison.Ordinal);
        Assert.Contains("pgb.properties.host", bicep, StringComparison.Ordinal);
    }
}
