// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Covers the container <c>env</c>/<c>ports</c> emission and compute-to-compute service
/// discovery introduced in the async publish build path.
/// </summary>
public class ContainerEnvironmentEmissionTests
{
    private static string Generate(DistributedApplicationModel model, RadiusEnvironmentResource radiusEnv)
    {
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        return new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);
    }

    [Fact]
    public void WithEnvironment_Literal_EmittedAsEnvMapEntry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("PLAIN", "hello");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        var bicep = Generate(model, radiusEnv);

        // The Radius container v2 schema expresses env as a map of name -> { value: <expr> }.
        Assert.Contains("env: {", bicep);
        Assert.Contains("PLAIN: {", bicep);
        Assert.Contains("value: 'hello'", bicep);
    }

    [Fact]
    public void ComputeToCompute_Reference_EmitsServiceDiscoveryWithNamespacedFqdn()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        var backend = builder.AddContainer("backend", "myapp/backend", "latest")
            .WithHttpEndpoint(targetPort: 8080, name: "http");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(backend.GetEndpoint("http"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        var bicep = Generate(model, radiusEnv);

        // Standard service-discovery variable plus a cluster FQDN that includes the namespace
        // segment. The host is the recipe Service name ({name}-{name}) and the URL carries the
        // container port (the recipe Service listens on the container port, not port 80).
        Assert.Contains("services__backend__http__0: {", bicep);
        Assert.Contains("value: 'http://backend-backend.default.svc.cluster.local:8080'", bicep);
    }

    [Fact]
    public void ComputeToCompute_Reference_UsesConfiguredNamespaceInFqdn()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv").WithNamespace("custom-ns");
        var backend = builder.AddContainer("backend", "myapp/backend", "latest")
            .WithHttpEndpoint(targetPort: 8080, name: "http");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(backend.GetEndpoint("http"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        var bicep = Generate(model, radiusEnv);

        Assert.Contains("value: 'http://backend-backend.custom-ns.svc.cluster.local:8080'", bicep);
    }

    [Fact]
    public void ComputeToCompute_CrossEnvironmentReference_UsesTargetEnvironmentNamespace()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var frontendEnv = builder.AddRadiusEnvironment("frontend-env").WithNamespace("frontend-ns");
        var dataEnv = builder.AddRadiusEnvironment("data-env").WithNamespace("data-ns");

        // backend deploys to data-env (data-ns); api deploys to frontend-env (frontend-ns) and
        // references backend across environments. The emitted service-discovery FQDN for backend
        // must use the TARGET environment's namespace (data-ns), not the referencing environment's.
        var backend = builder.AddContainer("backend", "myapp/backend", "latest")
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithComputeEnvironment(dataEnv);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(backend.GetEndpoint("http"))
            .WithComputeEnvironment(frontendEnv);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var frontend = model.Resources.OfType<RadiusEnvironmentResource>().First(e => e.Name == "frontend-env");

        var bicep = Generate(model, frontend);

        // The api container lives in frontend-ns but must reach backend in data-ns.
        Assert.Contains("value: 'http://backend-backend.data-ns.svc.cluster.local:8080'", bicep);
        Assert.DoesNotContain("backend-backend.frontend-ns.svc.cluster.local", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointAnnotations_EmittedAsContainerPorts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 5000, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        var bicep = Generate(model, radiusEnv);

        // ports is a map of name -> { containerPort, protocol }.
        Assert.Contains("ports: {", bicep);
        Assert.Contains("http: {", bicep);
        Assert.Contains("containerPort: 5000", bicep);
        Assert.Contains("protocol: 'TCP'", bicep);
    }

    [Fact]
    public void EndpointsResolvingToSamePort_EmitSingleDeduplicatedContainerPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        // Two endpoints that resolve to the same container port/protocol. The recipe would reject a
        // Service with two identical (port, protocol) entries, so ResolvePorts must dedup them,
        // matching the Kubernetes publisher. See: https://github.com/microsoft/aspire/issues/14029
        builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithHttpEndpoint(targetPort: 8080, name: "http2");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        var bicep = Generate(model, radiusEnv);

        // Exactly one container port entry survives the (port, protocol) dedup: the first endpoint.
        Assert.Contains("http: {", bicep);
        Assert.DoesNotContain("http2: {", bicep);
        var occurrences = bicep.Split("containerPort: 8080").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void SecretParameter_EnvVar_EmittedAsSecureParam()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        var secret = builder.AddParameter("apikey", "s3cr3t", secret: true);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("API_KEY", secret);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        var bicep = Generate(model, radiusEnv);

        // A secret-sourced env var is routed to a top-level Bicep param declared `@secure()`
        // (so its value is never logged or written to the artifact) and referenced by the env
        // entry — the literal 's3cr3t' must never appear in the emitted Bicep.
        Assert.Matches(@"(?im)^\s*@secure\(\)\s*$\r?\n\s*param\s+apikey\s+string\s*$", bicep);
        Assert.DoesNotContain("s3cr3t", bicep);
    }

    [Fact]
    public void NonSecretParameter_EnvVar_EmittedAsPlainParam()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        var plain = builder.AddParameter("region", "westus", secret: false);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("REGION", plain);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        var bicep = Generate(model, radiusEnv);

        // A non-secret parameter is emitted as a plain `param` with no `@secure()` decorator.
        Assert.Contains("param region string", bicep);
        Assert.DoesNotMatch(@"(?im)^\s*@secure\(\)\s*$\r?\n\s*param\s+region\s+string\s*$", bicep);
    }
}
