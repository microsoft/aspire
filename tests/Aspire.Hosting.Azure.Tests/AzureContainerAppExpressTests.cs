// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREACAEXPRESS001
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureContainerAppExpressTests
{
    [Fact]
    public void AsExpressValidatesBuilder()
    {
        IResourceBuilder<AzureContainerAppEnvironmentResource> builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(builder.AsExpress);

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AsExpressIsIdempotentAndDoesNotChangeRunResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var resources = builder.Resources.ToArray();

        Assert.Same(environment, environment.AsExpress());
        Assert.Same(environment, environment.AsExpress());
        Assert.Equal(resources, builder.Resources);
    }

    [Fact]
    public async Task AsExpressDefaultsAreEnvironmentLocal()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var express = builder.AddAzureContainerAppEnvironment("express").AsExpress();
        var standard = builder.AddAzureContainerAppEnvironment("standard");

        var (_, expressBicep) = await GetManifestWithBicep(express.Resource);
        var (_, standardBicep) = await GetManifestWithBicep(standard.Resource);

        await Verify(expressBicep, "bicep").AppendContentAsFile(standardBicep, "bicep", "standard");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsExpressPublishesEnvironment(bool existing)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        if (existing)
        {
            environment.AsExisting(builder.AddParameter("existing-name"), builder.AddParameter("existing-resource-group"));
        }

        var (manifest, bicep) = await GetManifestWithBicep(environment.Resource);

        await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsExpressPreservesExplicitDashboardRegardlessOfCallOrder(bool expressFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        if (expressFirst)
        {
            environment.AsExpress().WithDashboard();
        }
        else
        {
            environment.WithDashboard().AsExpress();
        }

        await Verify(environment.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task AsExpressPreservesEnvironmentCustomization()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress()
            .ConfigureInfrastructure(infrastructure =>
            {
                var managedEnvironment = infrastructure.GetProvisionableResources().OfType<ContainerAppManagedEnvironment>().Single();
                managedEnvironment.WorkloadProfiles.Add(new ContainerAppWorkloadProfile
                {
                    Name = "custom",
                    WorkloadProfileType = "Consumption"
                });
            });

        await Verify(environment.Resource.GetBicepTemplateString(), "bicep");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3, null)]
    [InlineData(3, 2)]
    public async Task AsExpressProjectPreservesExplicitReplicaSettings(int? replicas, int? customMinimum)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var project = builder.AddProject("api", "api.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        if (replicas is { } count)
        {
            project.WithReplicas(count);
        }
        if (customMinimum is { } minimum)
        {
            project.PublishAsAzureContainerApp((_, app) => app.Template.Scale.MinReplicas = minimum);
        }

        using var application = builder.Build();
        await ExecuteBeforeStartHooksAsync(application, default);
        var target = Assert.IsType<AzureContainerAppResource>(project.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget);
        var (manifest, bicep) = await GetManifestWithBicep(target, skipPreparer: true);

        await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AsExpressPreservesManualSecretsIdentityAndSupportedCustomization()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var secret = builder.AddParameter("api-key", secret: true);
        var storage = builder.AddAzureStorage("storage");
        var project = builder.AddProject("api", "api.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithEnvironment("API_KEY", secret)
            .WithReference(storage.AddBlobs("blobs"))
            .PublishAsAzureContainerApp((_, app) =>
            {
                app.Template.Volumes.Add(new ContainerAppVolume { Name = "scratch", StorageType = ContainerAppStorageType.EmptyDir });
                var container = app.Template.Containers[0].Value!;
                container.VolumeMounts.Add(new ContainerAppVolumeMount { VolumeName = "scratch", MountPath = "/tmp/scratch" });
                container.Probes.Add(new ContainerAppProbe
                {
                    ProbeType = ContainerAppProbeType.Liveness,
                    TcpSocket = new ContainerAppTcpSocketRequestInfo { Port = 8080 }
                });
                app.Template.Scale.Rules.Add(new ContainerAppScaleRule
                {
                    Name = "http",
                    Http = new ContainerAppHttpScaleRule { Metadata = { ["concurrentRequests"] = "20" } }
                });
            });

        using var application = builder.Build();
        await ExecuteBeforeStartHooksAsync(application, default);
        var target = Assert.IsType<AzureContainerAppResource>(project.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget);
        var (manifest, bicep) = await GetManifestWithBicep(target, skipPreparer: true);

        await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AsExpressPreservesParameterizedSettingsAndDisabledFeatures()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env").WithDashboard(false).AsExpress();
        var container = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints()
            .PublishAsAzureContainerApp((infrastructure, app) =>
            {
                var minimum = new ProvisioningParameter("minimumReplicas", typeof(int));
                var port = new ProvisioningParameter("targetPort", typeof(int));
                infrastructure.Add(minimum);
                infrastructure.Add(port);
                app.Template.Scale.MinReplicas = minimum;
                app.Configuration.Ingress.TargetPort = port;
                app.Configuration.Ingress.ClientCertificateMode = ContainerAppIngressClientCertificateMode.Ignore;
                app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.None;
                app.Configuration.Dapr.IsEnabled = false;
                app.Template.Scale.Rules.Add(new ContainerAppScaleRule
                {
                    Name = "cpu",
                    Custom = new ContainerAppCustomScaleRule { CustomScaleRuleType = "cpu", Metadata = { ["type"] = "Utilization", ["value"] = "80" } }
                });
                app.Template.Scale.Rules.Add(new ContainerAppScaleRule
                {
                    Name = "memory",
                    Custom = new ContainerAppCustomScaleRule { CustomScaleRuleType = "memory", Metadata = { ["type"] = "Utilization", ["value"] = "80" } }
                });
            });
        using var application = builder.Build();
        await ExecuteBeforeStartHooksAsync(application, default);
        var target = Assert.IsType<AzureContainerAppResource>(container.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget);

        await Verify(target.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task AsExpressPreservesKeyVaultSecretReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var container = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints()
            .PublishAsAzureContainerApp((_, app) => app.Configuration.Secrets.Add(new ContainerAppWritableSecret
            {
                Name = "external-secret",
                KeyVaultUri = new Uri("https://example.vault.azure.net/secrets/example"),
                Identity = "/subscriptions/example/resourceGroups/example/providers/Microsoft.ManagedIdentity/userAssignedIdentities/example"
            }));
        using var application = builder.Build();
        await ExecuteBeforeStartHooksAsync(application, default);
        var target = Assert.IsType<AzureContainerAppResource>(container.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget);

        await Verify(target.GetBicepTemplateString(), "bicep");
    }
}
