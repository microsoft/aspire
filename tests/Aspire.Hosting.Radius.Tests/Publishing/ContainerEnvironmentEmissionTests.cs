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

        // Standard service-discovery variable plus a cluster FQDN that includes the namespace segment.
        Assert.Contains("services__backend__http__0: {", bicep);
        Assert.Contains("value: 'http://backend.default.svc.cluster.local'", bicep);
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

        Assert.Contains("value: 'http://backend.custom-ns.svc.cluster.local'", bicep);
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
        Assert.Contains("value: 'http://backend.data-ns.svc.cluster.local'", bicep);
        Assert.DoesNotContain("backend.frontend-ns.svc.cluster.local", bicep, StringComparison.Ordinal);
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
}
