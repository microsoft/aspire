// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZUREHEALTH001, ASPIREAZURE001

using Aspire.HealthModels;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using HealthModelPlayground;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureHealthModelTests(ITestOutputHelper output)
{
    [Fact]
    public void RunModeDoesNotReadDefinitionOrProvisionAzure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var model = builder.AddAzureHealthModel("health", "not-created-yet.json");
        Assert.Empty(builder.Resources.OfType<AzureHealthModelResource>());
        Assert.Empty(builder.Resources.OfType<AzureEnvironmentResource>());
        Assert.Equal("health", model.Resource.Name);
    }

    [Fact]
    public async Task PublishPreservesTheModelAndCreatesMonitoringResources()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var definitionFile = Path.Combine(workspace.Path, "healthmodel.json");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        await File.WriteAllTextAsync(definitionFile, HealthModelContract.Serialize(CreateDocument(builder.Environment.ApplicationName)), TestContext.Current.CancellationToken);
        builder.AddContainer("api", "sample-api").WithHealthCheck("api_check");
        var model = builder.AddAzureHealthModel("health", definitionFile);
        using var app = builder.Build();
        Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<AzureEnvironmentResource>());
        var manifest = await ManifestUtils.GetManifest(model.Resource, workspace.Path);
        var bicepPath = Path.Combine(workspace.Path, "health.module.bicep");
        var bicep = await File.ReadAllTextAsync(bicepPath, TestContext.Current.CancellationToken);

        await Verify(manifest.ToJsonString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void MissingDefinitionFailsPublishWithActionableError()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var resource = new AzureHealthModelResource("health", Path.Combine(workspace.Path, "missing.json"));
        var exception = Assert.Throws<DistributedApplicationException>(resource.GetBicepTemplateString);
        Assert.Contains("Dashboard > Health", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidVersionDoesNotGenerateAHealthyFallbackModel()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var path = Path.Combine(workspace.Path, "invalid.json");
        File.WriteAllText(path, HealthModelContract.Serialize(CreateDocument("SampleApp") with { SchemaVersion = 99 }));
        var resource = new AzureHealthModelResource("health", path);

        Assert.Throws<DistributedApplicationException>(resource.GetBicepTemplateString);
    }

    [Fact]
    public Task PrometheusQueryPreservesTriStateAndEscapesLabelValues()
    {
        var query = HealthModelBicepGenerator.CreateQuery("api\"\\name", 1, "readiness\ncheck");
        return Verify(query, "txt");
    }

    [Fact]
    public void OutputReferencesRemainDeferred()
    {
        var resource = new AzureHealthModelResource("health", Path.GetFullPath("definition.json"));
        Assert.Equal("{health.outputs.workspaceId}", resource.WorkspaceId.ValueExpression);
        Assert.Equal("{health.outputs.remoteWriteEndpoint}", resource.RemoteWriteEndpoint.ValueExpression);
        Assert.Equal("{health.outputs.collectorIdentityId}", resource.CollectorIdentityId.ValueExpression);
        Assert.Equal("{health.outputs.collectorClientId}", resource.CollectorClientId.ValueExpression);
    }

    [Fact]
    public void CollectorIsNotAddedInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var metrics = builder.AddContainer("metrics", "test-metrics").WithHttpEndpoint(targetPort: 8080);
        var model = builder.AddAzureHealthModel("health", "not-created.json");
        builder.AddAzureContainerAppsHealthModelCollector("collector", model, metrics.GetEndpoint("http"));

        Assert.Equal(metrics.Resource, Assert.Single(builder.Resources));
    }

    [Fact]
    public async Task CollectorPublishesManagedIdentityAndDeferredIngestionConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var definitionFile = Path.Combine(workspace.Path, "healthmodel.json");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        await File.WriteAllTextAsync(definitionFile, HealthModelContract.Serialize(CreateDocument(builder.Environment.ApplicationName)), TestContext.Current.CancellationToken);
        builder.AddAzureContainerAppEnvironment("env");
        builder.AddContainer("api", "test-api").WithHealthCheck("api_check");
        var metrics = builder.AddContainer("metrics", "test-metrics").WithHttpEndpoint(targetPort: 8080);
        var health = builder.AddAzureHealthModel("health", definitionFile);
        var collector = builder.AddAzureContainerAppsHealthModelCollector("collector", health, metrics.GetEndpoint("http"));
        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        var target = Assert.IsAssignableFrom<AzureProvisioningResource>(collector.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget);
        var manifest = await ManifestUtils.GetManifest(target, workspace.Path);

        await Verify(manifest.ToJsonString(), "json").AppendContentAsFile(target.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void StaleResourceOrHealthCheckBindingFailsExplicitly()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var definitionFile = Path.Combine(workspace.Path, "healthmodel.json");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        File.WriteAllText(definitionFile, HealthModelContract.Serialize(CreateDocument(builder.Environment.ApplicationName)));
        var model = builder.AddAzureHealthModel("health", definitionFile);

        Assert.Throws<DistributedApplicationException>(model.Resource.GetBicepTemplateString);
        builder.AddContainer("api", "test-api").WithHealthCheck("renamed-check");
        Assert.Throws<DistributedApplicationException>(model.Resource.GetBicepTemplateString);
    }

    [Fact]
    public void AddedHealthChecksCannotBeSilentlyOmitted()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddContainer("api", "test-api").WithHealthCheck("api_check").WithHealthCheck("new-readiness");
        var document = CreateDocument(builder.Environment.ApplicationName);

        Assert.Throws<InvalidDataException>(() => HealthModelBindingValidator.Validate(document, builder.Resources, builder.Environment.ApplicationName));
    }

    [Fact]
    public void DefinitionFromAnotherApplicationIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddContainer("api", "test-api").WithHealthCheck("api_check");
        var document = CreateDocument("AnotherApplication");

        Assert.Throws<InvalidDataException>(() => HealthModelBindingValidator.Validate(document, builder.Resources, builder.Environment.ApplicationName));
    }

    [Fact]
    public void PlaygroundDefinitionMatchesTheSharedScenario()
    {
        var document = HealthModelContract.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "healthmodel-playground.json")));
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resources = HealthModelScenario.Resources.ToDictionary(resource => resource.Name,
            resource => builder.AddContainer(resource.Name, "test-resource").WithHealthCheck(resource.HealthCheckName));
        foreach (var resource in HealthModelScenario.Resources)
        {
            if (resource.ParentName is { } parent)
            {
                resources[resource.Name].WithParentRelationship(resources[parent]);
            }
            foreach (var dependency in resource.Dependencies)
            {
                resources[resource.Name].WithReferenceRelationship(resources[dependency]);
            }
        }

        HealthModelBindingValidator.Validate(document, builder.Resources, "HealthModelSandbox.AppHost");
        Assert.Equal(12, document.Entities.Length);
        Assert.Equal(11, document.Relationships.Length);
        Assert.Equal(22, document.Entities.Sum(entity => entity.LocalSignals.Length));
        Assert.All(document.Entities.Where(entity => entity.AspireResourceName is not null),
            entity => Assert.Equal(1, entity.ReplicaIndex));

        var rewired = document with
        {
            Relationships = [.. document.Relationships.Select(relationship =>
                relationship.ChildEntityName == document.Entities.Single(entity => entity.AspireResourceName == "orders-db").Name
                    ? relationship with { ParentEntityName = document.Name }
                    : relationship)]
        };
        Assert.Throws<InvalidDataException>(() => HealthModelBindingValidator.Validate(rewired, builder.Resources, document.ApplicationName));
    }

    private static HealthModelDocument CreateDocument(string applicationName) => new()
    {
        Name = "sample-health",
        ApplicationName = applicationName,
        Entities =
        [
            new()
            {
                Name = "sample-health",
                DisplayName = "AppHost",
                CanvasPosition = new(0, 0)
            },
            new()
            {
                Name = "resource-api",
                DisplayName = "API's ${literal} label",
                AspireResourceName = "api",
                ReplicaIndex = 1,
                CanvasPosition = new(224.25, -180.5),
                Impact = EntityImpact.Limited,
                HealthObjective = 99.95,
                Dependencies = new()
                {
                    AggregationType = DependenciesAggregationType.MaxNotHealthy,
                    Unit = AggregationUnit.Percentage,
                    UnhealthyThreshold = 75.5,
                    DegradedThreshold = 25
                },
                LocalSignals = [new("resource-state", SignalKind.External), new("api_check", SignalKind.External)]
            }
        ],
        Relationships = [new("sample-health", "resource-api")]
    };
}
