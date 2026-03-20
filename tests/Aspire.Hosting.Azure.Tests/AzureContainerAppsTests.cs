// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREACADOMAINS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECOMPUTE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREAZURE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREACANAMING001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureContainerAppsTests
{
    [Fact]
    public async Task AddContainerAppsInfrastructureAddsDeploymentTargetWithContainerAppToContainerResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddDockerfileWithAppsInfrastructureAddsDeploymentTargetWithContainerAppToContainerResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var directory = Directory.CreateTempSubdirectory(".aspire-test");

        // Contents of the Dockerfile are not important for this test
        File.WriteAllText(Path.Combine(directory.FullName, "Dockerfile"), "");

        builder.AddDockerfile("api", directory.FullName);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddContainerAppEnvironmentAddsDeploymentTargetWithContainerAppToProjectResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.IsType<IComputeResource>(Assert.Single(model.GetProjectResources()), exactMatch: false);

        var target = container.GetDeploymentTargetAnnotation();

        Assert.NotNull(target);
        Assert.Same(env.Resource, target.ComputeEnvironment);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddExecutableResourceWithPublishAsDockerFileWithAppsInfrastructureAddsDeploymentTargetWithContainerAppToContainerResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var infra = builder.AddAzureContainerAppEnvironment("infra");

        var env = builder.AddParameter("env");

        builder.AddExecutable("api", "node.exe", Environment.CurrentDirectory)
               .PublishAsDockerFile()
               .PublishAsAzureContainerApp((infra, app) =>
               {
                   app.Template.Containers[0].Value!.Env.Add(new ContainerAppEnvironmentVariable()
                   {
                       Name = "Hello",
                       Value = env.AsProvisioningParameter(infra)
                   });
               });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.IsType<IComputeResource>(Assert.Single(model.GetContainerResources()), exactMatch: false);

        var target = container.GetDeploymentTargetAnnotation();

        Assert.NotNull(target);
        Assert.Same(infra.Resource, target.ComputeEnvironment);

        var resource = target.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task CanTweakContainerAppEnvironmentUsingPublishAsContainerAppOnExecutable()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");

        builder.AddExecutable("api", "node.exe", Environment.CurrentDirectory)
               .PublishAsDockerFile();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        var target = container.GetDeploymentTargetAnnotation();

        Assert.Same(env.Resource, target?.ComputeEnvironment);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddContainerAppsInfrastructureWithParameterReference()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var value = builder.AddParameter("value");
        var minReplicas = builder.AddParameter("minReplicas");

        builder.AddContainer("api", "myimage")
               .PublishAsAzureContainerApp((module, c) =>
               {
                   var val = new ContainerAppEnvironmentVariable()
                   {
                       Name = "Parameter",
                       Value = value.AsProvisioningParameter(module)
                   };

                   c.Template.Containers[0].Value!.Env.Add(val);
                   c.Template.Scale.MinReplicas = minReplicas.AsProvisioningParameter(module);
               });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddContainerAppsEntrypointAndArgs()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
               .WithEntrypoint("/bin/sh")
               .WithArgs("my", "args with space");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ProjectWithManyReferenceTypes()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var db = builder.AddAzureCosmosDB("mydb");
        db.AddCosmosDatabase("cosmosdb", databaseName: "db");

        var pgContainer = builder.AddPostgres("pgc");

        // Postgres uses secret outputs + a literal connection string
        var pgdb = builder.AddAzurePostgresFlexibleServer("pg").WithPasswordAuthentication().AddDatabase("db");

        var rawCs = builder.AddConnectionString("cs");

        var blob = builder.AddAzureStorage("storage").AddBlobs("blobs");

        // Secret parameters (_ isn't supported and will be replaced by -)
        var secretValue = builder.AddParameter("value0", "x", secret: true);

        // Normal parameters
        var value = builder.AddParameter("value1", "y");

        var project = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithHttpsEndpoint()
            .WithHttpEndpoint(name: "internal")
            .WithReference(db)
            .WithReference(blob)
            .WithReference(pgdb)
            .WithEnvironment("SecretVal", secretValue)
            .WithEnvironment("secret_value_1", secretValue)
            .WithEnvironment("Value", value)
            .WithEnvironment("CS", rawCs)
            .WithEnvironment("DATABASE_URL", pgContainer.Resource.UriExpression);

        project.WithEnvironment(context =>
        {
            var httpEp = project.GetEndpoint("http");
            var httpsEp = project.GetEndpoint("https");
            var internalEp = project.GetEndpoint("internal");

            context.EnvironmentVariables["HTTP_EP"] = project.GetEndpoint("http");
            context.EnvironmentVariables["HTTPS_EP"] = project.GetEndpoint("https");
            context.EnvironmentVariables["INTERNAL_EP"] = project.GetEndpoint("internal");
            context.EnvironmentVariables["TARGET_PORT"] = httpEp.Property(EndpointProperty.TargetPort);
            context.EnvironmentVariables["PORT"] = httpEp.Property(EndpointProperty.Port);
            context.EnvironmentVariables["HOST"] = httpEp.Property(EndpointProperty.Host);
            context.EnvironmentVariables["HOSTANDPORT"] = httpEp.Property(EndpointProperty.HostAndPort);
            context.EnvironmentVariables["SCHEME"] = httpEp.Property(EndpointProperty.Scheme);
            context.EnvironmentVariables["INTERNAL_HOSTANDPORT"] = internalEp.Property(EndpointProperty.HostAndPort);
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var proj = Assert.Single(model.GetProjectResources());
        var identityName = $"{proj.Name}-identity";
        var projIdentity = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == identityName);

        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);
        var (identityManifest, identityBicep) = await GetManifestWithBicep(projIdentity);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(identityManifest.ToString(), "json")
              .AppendContentAsFile(identityBicep, "bicep");
    }

    [Fact]
    public async Task ProjectWithManyReferenceTypesAndContainerAppEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("cae");

        var db = builder.AddAzureCosmosDB("mydb");
        db.AddCosmosDatabase("cosmosdb", databaseName: "db");

        // Postgres uses secret outputs + a literal connection string
        var pgdb = builder.AddAzurePostgresFlexibleServer("pg").WithPasswordAuthentication().AddDatabase("db");

        var rawCs = builder.AddConnectionString("cs");

        var blob = builder.AddAzureStorage("storage").AddBlobs("blobs");

        // Secret parameters (_ isn't supported and will be replaced by -)
        var secretValue = builder.AddParameter("value0", "x", secret: true);

        // Normal parameters
        var value = builder.AddParameter("value1", "y");

        var project = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithHttpsEndpoint()
            .WithHttpEndpoint(name: "internal")
            .WithReference(db)
            .WithReference(blob)
            .WithReference(pgdb)
            .WithEnvironment("SecretVal", secretValue)
            .WithEnvironment("secret_value_1", secretValue)
            .WithEnvironment("Value", value)
            .WithEnvironment("CS", rawCs);

        project.WithEnvironment(context =>
        {
            var httpEp = project.GetEndpoint("http");
            var httpsEp = project.GetEndpoint("https");
            var internalEp = project.GetEndpoint("internal");

            context.EnvironmentVariables["HTTP_EP"] = project.GetEndpoint("http");
            context.EnvironmentVariables["HTTPS_EP"] = project.GetEndpoint("https");
            context.EnvironmentVariables["INTERNAL_EP"] = project.GetEndpoint("internal");
            context.EnvironmentVariables["TARGET_PORT"] = httpEp.Property(EndpointProperty.TargetPort);
            context.EnvironmentVariables["PORT"] = httpEp.Property(EndpointProperty.Port);
            context.EnvironmentVariables["HOST"] = httpEp.Property(EndpointProperty.Host);
            context.EnvironmentVariables["HOSTANDPORT"] = httpEp.Property(EndpointProperty.HostAndPort);
            context.EnvironmentVariables["SCHEME"] = httpEp.Property(EndpointProperty.Scheme);
            context.EnvironmentVariables["INTERNAL_HOSTANDPORT"] = internalEp.Property(EndpointProperty.HostAndPort);
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var proj = Assert.Single(model.GetProjectResources());
        var identityName = $"{proj.Name}-identity";
        var projIdentity = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == identityName);

        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);
        var (identityManifest, identityBicep) = await GetManifestWithBicep(projIdentity);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(identityManifest.ToString(), "json")
              .AppendContentAsFile(identityBicep, "bicep");
    }

    [Fact]
    public async Task AzureContainerAppsBicepGenerationIsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var secret = builder.AddParameter("secret", secret: true);
        var kv = builder.AddAzureKeyVault("kv");
        var existingKv = builder.AddAzureKeyVault("existingKv").PublishAsExisting("existingKvName", "existingRgName");

        builder.AddContainer("api", "myimage")
               .WithEnvironment("TOP_SECRET", secret)
               .WithEnvironment("TOP_SECRET2", kv.GetSecret("secret"))
               .WithEnvironment("EXISTING_TOP_SECRET", existingKv.GetSecret("secret"));

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        _ = await GetManifestWithBicep(resource);
        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AzureContainerAppsMapsPortsForBaitAndSwitchResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddExecutable("api", "node", ".")
            .PublishAsDockerFile()
            .WithHttpEndpoint(env: "PORT");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task PublishAsContainerAppInfluencesContainerAppDefinition()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");
        builder.AddContainer("api", "myimage")
            .PublishAsAzureContainerApp((module, c) =>
            {
                Assert.Contains(c, module.GetProvisionableResources());

                c.Template.Scale.MinReplicas = 0;
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ConfigureCustomDomainMutatesIngress()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var customDomain = builder.AddParameter("customDomain");
        var certificateName = builder.AddParameter("certificateName");

        builder.AddAzureContainerAppEnvironment("env");
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 1111)
            .PublishAsAzureContainerApp((module, c) =>
            {
                c.ConfigureCustomDomain(customDomain, certificateName);
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureBicepResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ConfigureDuplicateCustomDomainMutatesIngress()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var customDomain = builder.AddParameter("customDomain");
        var initialCertificateName = builder.AddParameter("initialCertificateName");
        var expectedCertificateName = builder.AddParameter("expectedCertificateName");

        builder.AddAzureContainerAppEnvironment("env");
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 1111)
            .PublishAsAzureContainerApp((module, c) =>
            {
                c.ConfigureCustomDomain(customDomain, initialCertificateName);
                c.ConfigureCustomDomain(customDomain, expectedCertificateName);
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureBicepResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ConfigureMultipleCustomDomainsMutatesIngress()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var customDomain1 = builder.AddParameter("customDomain1");
        var certificateName1 = builder.AddParameter("certificateName1");

        var customDomain2 = builder.AddParameter("customDomain2");
        var certificateName2 = builder.AddParameter("certificateName2");

        builder.AddAzureContainerAppEnvironment("env");
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 1111)
            .PublishAsAzureContainerApp((module, c) =>
            {
                c.ConfigureCustomDomain(customDomain1, certificateName1);
                c.ConfigureCustomDomain(customDomain2, certificateName2);
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureBicepResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task VolumesAndBindMountsAreTranslation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithVolume("vol1", "/path1")
            .WithVolume("vol2", "/path2")
            .WithBindMount("bind1", "/path3");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task MultipleVolumesHaveUniqueNamesInBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("my-ace");

        builder.AddContainer("druid", "apache/druid", "34.0.0")
               .WithHttpEndpoint(targetPort: 8081)
               .WithVolume("druid_shared", "/opt/shared")
               .WithVolume("coordinator_var", "/opt/druid/var")
               .WithBindMount("bind_mount", "/opt/bind");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // The bicep should contain unique parameter names for the storage resources
        Assert.Contains("my_ace_outputs_volumes_druid_0", bicep);
        Assert.Contains("my_ace_outputs_volumes_druid_1", bicep);
        Assert.Contains("my_ace_outputs_bindmounts_druid_0", bicep);

        // Also verify the container app environment resource output
        var containerAppEnvResource = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());
        var (envManifest, envBicep) = await GetManifestWithBicep(containerAppEnvResource);

        await Verify(manifest.ToString())
              .AppendContentAsFile(bicep)
              .AppendContentAsFile(envManifest.ToString())
              .AppendContentAsFile(envBicep);
    }

    [Fact]
    public async Task KeyVaultReferenceHandling()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var db = builder.AddAzureCosmosDB("mydb").WithAccessKeyAuthentication();
        db.AddCosmosDatabase("db");

        var kvName = builder.AddParameter("kvName");
        var sharedRg = builder.AddParameter("sharedRg");

        var existingKv = builder.AddAzureKeyVault("existingKv")
                                .PublishAsExisting(kvName, sharedRg);

        builder.AddContainer("api", "image")
            .WithReference(db)
            .WithEnvironment("SECRET_VALUE", existingKv.GetSecret("secret"));

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task SecretOutputsThrowNotSupportedExceptionWithContainerAppEnvironmentResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("cae");

        var resource = builder.AddAzureInfrastructure("resourceWithSecret", infra =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            var kvNameParam = new ProvisioningParameter(AzureBicepResource.KnownParameters.KeyVaultName, typeof(string));
#pragma warning restore CS0618 // Type or member is obsolete
            infra.Add(kvNameParam);

            var kv = KeyVaultService.FromExisting("kv");
            kv.Name = kvNameParam;
            infra.Add(kv);

            var secret = new KeyVaultSecret("kvs")
            {
                Name = "myconnection",
                Properties = new()
                {
                    Value = "top secret"
                },
                Parent = kv,
            };

            infra.Add(secret);
        });

        var container = builder.AddContainer("api", "image")
            .WithEnvironment(context =>
            {
#pragma warning disable CS0618 // Type or member is obsolete
                context.EnvironmentVariables["secret0"] = resource.GetSecretOutput("myconnection");
#pragma warning restore CS0618 // Type or member is obsolete
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var target = container.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureBicepResource;

        Assert.NotNull(target);

        var ex = Assert.Throws<NotSupportedException>(() => target.GetBicepTemplateFile());

        Assert.Equal("Automatic Key vault generation is not supported in this environment. Please create a key vault resource directly.", ex.Message);
    }

    [Fact]
    public async Task CanCustomizeWithProvisioningBuildOptions()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.Services.Configure<AzureProvisioningOptions>(options => options.ProvisioningBuildOptions.InfrastructureResolvers.Insert(0, new MyResourceNamePropertyResolver()));
        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api1", "myimage");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, "bicep");
    }

    private sealed class MyResourceNamePropertyResolver : DynamicResourceNamePropertyResolver
    {
        public override void ResolveProperties(ProvisionableConstruct construct, ProvisioningBuildOptions options)
        {
            if (construct is ContainerApp app)
            {
                app.Name = app.Name.Value + "-my";
            }

            base.ResolveProperties(construct, options);
        }
    }

    [Fact]
    public async Task ExternalEndpointBecomesIngress()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task FirstHttpEndpointBecomesIngress()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(name: "one", targetPort: 8080)
            .WithHttpEndpoint(name: "two", targetPort: 8081);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task EndpointWithHttp2SetsTransportToH2()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint()
            .WithEndpoint("http", e => e.Transport = "http2")
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ProjectUsesTheTargetPortAsADefaultPortForFirstHttpEndpoint()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddProject<Project>("api", launchProfileName: null)
               .WithHttpEndpoint()
               .WithHttpsEndpoint();

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var project = Assert.Single(model.GetProjectResources());

        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task RoleAssignmentsWithAsExisting()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var storageName = builder.AddParameter("storageName");
        var storageRG = builder.AddParameter("storageRG");

        var storage = builder.AddAzureStorage("storage")
            .PublishAsExisting(storageName, storageRG);
        var blobs = storage.AddBlobs("blobs");

        builder.AddProject<Project>("api", launchProfileName: null)
               .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataReader);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var project = Assert.Single(model.GetProjectResources());
        var projIdentity = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-identity");
        var projRolesStorage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-roles-storage");

        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);
        var (identityManifest, identityBicep) = await GetManifestWithBicep(projIdentity);
        var (rolesStorageManifest, rolesStorageBicep) = await GetManifestWithBicep(projRolesStorage);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(rolesStorageManifest.ToString(), "json")
              .AppendContentAsFile(rolesStorageBicep, "bicep")
              .AppendContentAsFile(identityManifest.ToString(), "json")
              .AppendContentAsFile(identityBicep, "bicep");
    }

    [Fact]
    public async Task RoleAssignmentsWithAsExistingCosmosDB()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var cosmosName = builder.AddParameter("cosmosName");
        var cosmosRG = builder.AddParameter("cosmosRG");

        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .PublishAsExisting(cosmosName, cosmosRG);

        builder.AddProject<Project>("api", launchProfileName: null)
               .WithReference(cosmos);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var project = Assert.Single(model.GetProjectResources());
        var projIdentity = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-identity");
        var projRolesStorage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-roles-cosmos");

        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);
        var (identityManifest, identityBicep) = await GetManifestWithBicep(projIdentity);
        var (rolesCosmosManifest, rolesCosmosBicep) = await GetManifestWithBicep(projRolesStorage);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(rolesCosmosManifest.ToString(), "json")
              .AppendContentAsFile(rolesCosmosBicep, "bicep")
              .AppendContentAsFile(identityManifest.ToString(), "json")
              .AppendContentAsFile(identityBicep, "bicep");
    }

    [Fact]
    public async Task RoleAssignmentsWithAsExistingRedis()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var redis = builder.AddAzureManagedRedis("redis")
            .PublishAsExisting("myredis", "myRG");

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(redis);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var project = Assert.Single(model.GetProjectResources());
        var projIdentity = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-identity");
        var projRolesStorage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-roles-redis");

        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);
        var (identityManifest, identityBicep) = await GetManifestWithBicep(projIdentity);
        var (rolesRedisManifest, rolesRedisBicep) = await GetManifestWithBicep(projRolesStorage);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(rolesRedisManifest.ToString(), "json")
              .AppendContentAsFile(rolesRedisBicep, "bicep")
              .AppendContentAsFile(identityManifest.ToString(), "json")
              .AppendContentAsFile(identityBicep, "bicep");
    }

    [Fact]
    public async Task NonHttpSchemeWithTcpTransportIsAllowed()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithEndpoint(scheme: "redis", targetPort: 6379);

        using var app = builder.Build();

        // Custom schemes that use TCP transport should not throw
        await ExecuteBeforeStartHooksAsync(app, default);
    }

    [Fact]
    public async Task UnsupportedTransportThrows()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithEndpoint(scheme: "foo", targetPort: 443, name: "foo")
            .WithEndpoint("foo", e => e.Transport = "quic");

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Equal("The endpoint(s) 'foo' specify an unsupported transport. The supported transports are 'http', 'http2', and 'tcp'.", ex.Message);
    }

    [Fact]
    public async Task MultipleExternalEndpointsAreNotSupported()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(name: "ep1")
            .WithHttpEndpoint(name: "ep2")
            .WithExternalHttpEndpoints();

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Equal("Multiple external endpoints are not supported", ex.Message);
    }

    [Fact]
    public async Task ExternalNonHttpEndpointsAreNotSupported()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithEndpoint("ep1", e => e.IsExternal = true);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Equal("External non-HTTP(s) endpoints are not supported", ex.Message);
    }

    [Fact]
    public async Task HttpAndTcpEndpointsCannotHaveTheSameTargetPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 80)
            .WithEndpoint(targetPort: 80);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Equal("HTTP(s) and TCP endpoints cannot be mixed", ex.Message);
    }

    [Fact]
    public async Task DefaultHttpIngressUsesPort80EvenWithDifferentDevPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        // Dev port 8081 should be ignored in ACA, mapped to port 80
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(port: 8081, targetPort: 8080);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task DefaultHttpsIngressUsesPort443EvenWithDifferentDevPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        // Dev port 8081 should be ignored in ACA, mapped to port 443
        builder.AddContainer("api", "myimage")
            .WithHttpsEndpoint(port: 8081, targetPort: 8443);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task CanPreserveHttpSchemeUsingWithHttpsUpgrade()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env")
            .WithHttpsUpgrade(false);  // Preserve HTTP scheme, don't upgrade to HTTPS

        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(port: 8080, targetPort: 80);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddContainerAppEnvironmentDoesNotAddEnvironmentResourceInRunMode()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddAzureContainerAppEnvironment("env");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Empty(model.Resources.OfType<AzureContainerAppEnvironmentResource>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddContainerAppEnvironmentAddsEnvironmentResource(bool useAzdNaming)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");

        if (useAzdNaming)
        {
            env.WithAzdResourceNaming();
        }

        var pg = builder.AddAzurePostgresFlexibleServer("pg")
                        .WithPasswordAuthentication()
                        .AddDatabase("db");

        builder.AddContainer("cache", "redis")
               .WithVolume("App.da-ta", "/data")
               .WithReference(pg);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(environment);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AddContainerAppEnvironmentWithCompactNamingPreservesUniqueString()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Use a deliberately long name (15 chars) that would cause collisions without compact naming
        var env = builder.AddAzureContainerAppEnvironment("my-long-env-name");
        env.WithCompactResourceNaming();

        var pg = builder.AddAzurePostgresFlexibleServer("pg")
                        .WithPasswordAuthentication()
                        .AddDatabase("db");

        builder.AddContainer("cache", "redis")
               .WithVolume("App.da-ta", "/data")
               .WithReference(pg);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        var manifest = await GetManifestWithBicep(environment);

        await Verify(manifest.BicepText, "bicep");
    }

    [Fact]
    public async Task CompactNamingMultipleVolumesHaveUniqueNames()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("my-ace");
        env.WithCompactResourceNaming();

        builder.AddContainer("druid", "apache/druid", "34.0.0")
               .WithHttpEndpoint(targetPort: 8081)
               .WithVolume("druid_shared", "/opt/shared")
               .WithVolume("coordinator_var", "/opt/druid/var")
               .WithBindMount("./config", "/opt/druid/conf");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        var manifest = await GetManifestWithBicep(environment);

        await Verify(manifest.BicepText, "bicep");
    }

    // see https://github.com/microsoft/aspire/issues/8381 for more information on this scenario
    // Azure SqlServer needs an admin when it is first provisioned. To supply this, we use the
    // principalId from the Azure Container App Environment.
    [Fact]
    public async Task AddContainerAppEnvironmentWorksWithSqlServer()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var sql = builder.AddAzureSqlServer("sql");
        var db = sql.AddDatabase("db").WithDefaultAzureSku();

        builder.AddContainer("cache", "redis")
               .WithReference(db);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(sql.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ContainerAppEnvironmentWithCustomRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create a custom registry
        var registry = builder.AddAzureContainerRegistry("customregistry");

        // Create a container app environment and associate it with the custom registry
        builder.AddAzureContainerAppEnvironment("env")
            .WithAzureContainerRegistry(registry);

        // Add a container that will use the environment
        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify environment resource exists
        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        // Verify project resource exists
        var project = Assert.Single(model.GetProjectResources());

        // Get the bicep for the environment
        var (envManifest, envBicep) = await GetManifestWithBicep(environment);

        // Verify container has correct deployment target
        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var projectResource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(projectResource);

        // Get the bicep for the container
        var (containerManifest, containerBicep) = await GetManifestWithBicep(projectResource);

        // Verify the Azure Container Registry resource manifest and bicep
        var containerRegistry = Assert.Single(model.Resources.OfType<AzureContainerRegistryResource>());
        var (registryManifest, registryBicep) = await GetManifestWithBicep(containerRegistry);

        await Verify(envManifest.ToString(), "json")
              .AppendContentAsFile(envBicep, "bicep")
              .AppendContentAsFile(containerManifest.ToString(), "json")
              .AppendContentAsFile(containerBicep, "bicep")
              .AppendContentAsFile(registryManifest.ToString(), "json")
              .AppendContentAsFile(registryBicep, "bicep");
    }

    [Fact]
    public async Task ContainerAppEnvironmentWithCustomWorkspace()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create a custom Log Analytics Workspace
        var workspace = builder.AddAzureLogAnalyticsWorkspace("customworkspace");

        // Create a container app environment and associate it with the custom workspace
        builder.AddAzureContainerAppEnvironment("env")
            .WithAzureLogAnalyticsWorkspace(workspace);

        // Add a container that will use the environment
        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint();

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify environment resource exists
        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        // Verify project resource exists
        var project = Assert.Single(model.GetProjectResources());

        // Get the bicep for the environment
        var (envManifest, envBicep) = await GetManifestWithBicep(environment);

        // Verify container has correct deployment target
        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var projectResource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(projectResource);

        // Get the bicep for the container
        var (containerManifest, containerBicep) = await GetManifestWithBicep(projectResource);

        // Verify the Azure Log Analytics Workspace resource manifest and bicep
        var logAnalyticsWorkspace = Assert.Single(model.Resources.OfType<AzureLogAnalyticsWorkspaceResource>());
        var (workspaceManifest, workspaceBicep) = await GetManifestWithBicep(logAnalyticsWorkspace);

        await Verify(envManifest.ToString(), "json")
              .AppendContentAsFile(envBicep, "bicep")
              .AppendContentAsFile(containerManifest.ToString(), "json")
              .AppendContentAsFile(containerBicep, "bicep")
              .AppendContentAsFile(workspaceManifest.ToString(), "json")
              .AppendContentAsFile(workspaceBicep, "bicep");
    }

    [Fact]
    public async Task CanReferenceContainerAppEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");

        var azResource = builder.AddAzureInfrastructure("infra", infra =>
        {
            var managedEnvironment = (ContainerAppManagedEnvironment)env.Resource.AddAsExistingResource(infra);

            infra.Add(new ProvisioningOutput("id", typeof(string))
            {
                Value = managedEnvironment.Id
            });
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(azResource.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ContainerAppEnvironmentWithDashboardEnabled()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env")
               .WithDashboard(true);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerAppEnvResource = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(containerAppEnvResource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ContainerAppEnvironmentWithDashboardDisabled()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env")
               .WithDashboard(false);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerAppEnvResource = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        var (manifest, bicep) = await GetManifestWithBicep(containerAppEnvResource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task UnknownManifestExpressionProviderIsHandledWithAllocateParameter()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var customProvider = new CustomManifestExpressionProvider();

        builder.AddContainer("api", "myimage")
               .WithEnvironment(context =>
               {
                   context.EnvironmentVariables["CUSTOM_VALUE"] = customProvider;
               })
               .PublishAsAzureContainerApp((_, _) => { });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureBicepResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void AzureContainerAppEnvironmentImplementsIAzureComputeEnvironmentResource()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var env = builder.AddAzureContainerAppEnvironment("env");

        Assert.IsAssignableFrom<IAzureComputeEnvironmentResource>(env.Resource);
        Assert.IsAssignableFrom<IComputeEnvironmentResource>(env.Resource);
    }

    private static Task<(JsonNode ManifestNode, string BicepText)> GetManifestWithBicep(IResource resource) =>
        AzureManifestUtils.GetManifestWithBicep(resource, skipPreparer: true);

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }

    [Fact]
    public async Task ContainerAppWithUppercaseName_ShouldUseLowercaseInManifest()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        // This is the problematic case - uppercase name "WebFrontEnd"
        builder.AddContainer("WebFrontEnd", "myimage");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    private sealed class CustomManifestExpressionProvider : IManifestExpressionProvider
    {
        public string ValueExpression => "{customValue}";
    }

    [Fact]
    public void FailForNewContainerAppVersions()
    {
        var containerApp = new ContainerApp("app");

        // In order to set autoConfigureDataProtection, we need to use a preview API ContainerApp version.
        // This test fails on new default versions for ContainerApp so we check if autoConfigureDataProtection exists on the new Azure.Provisioning version.
        // Also, we need to ensure the new default version isn't newer than the preview version used to set autoConfigureDataProtection because
        // callers will get new APIs that may not work with the preview version we are using.
        Assert.True(containerApp.ResourceVersion == "2025-07-01", "When we get a new ResourceVersion for ContainerApps, ensure the version used by ContainerAppContext.CreateContainerApp() still works correctly.");
    }

    [Fact]
    public async Task PublishAsAzureContainerApp_ThrowsIfNoEnvironment()
    {
        static async Task RunTest(Action<IDistributedApplicationBuilder> action)
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
            // Do not add AzureContainerAppEnvironment

            action(builder);

            using var app = builder.Build();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

            Assert.Contains("there are no 'AzureContainerAppEnvironmentResource' resources", ex.Message);
        }

        await RunTest(builder =>
            builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
                .PublishAsAzureContainerApp((_, _) => { }));

        await RunTest(builder =>
            builder.AddProject<Projects.ServiceA>("ServiceA", launchProfileName: null)
                .PublishAsAzureContainerAppJob());

        await RunTest(builder =>
            builder.AddContainer("api", "myimage")
                .PublishAsAzureContainerApp((_, _) => { }));

        await RunTest(builder =>
            builder.AddContainer("api", "myimage")
                .PublishAsAzureContainerAppJob());

        await RunTest(builder =>
            builder.AddExecutable("exe", "path/to/executable", ".")
                .PublishAsDockerFile()
                .PublishAsAzureContainerApp((_, _) => { }));

        await RunTest(builder =>
            builder.AddExecutable("exe", "path/to/executable", ".")
                .PublishAsDockerFile()
                .PublishAsAzureContainerAppJob());
    }

    [Fact]
    public async Task MultipleAzureContainerAppEnvironmentsSupported()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path, step: "publish-manifest");

        var env1 = builder.AddAzureContainerAppEnvironment("env1");
        var env2 = builder.AddAzureContainerAppEnvironment("env2");

        builder.AddContainer("api1", "myimage")
            .WithComputeEnvironment(env1);

        builder.AddContainer("api2", "myimage")
            .WithComputeEnvironment(env2);

        using var app = builder.Build();

        // Publishing will stop the app when it is done
        await app.RunAsync();

        await VerifyFile(Path.Combine(tempDir.Path, "aspire-manifest.json"));
    }

    [Fact]
    public async Task PublishAsContainerAppJobInfluencesContainerAppDefinition()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");
        builder.AddContainer("api", "myimage")
            .PublishAsAzureContainerAppJob((infra, j) =>
            {
                Assert.Contains(j, infra.GetProvisionableResources());

                j.Configuration.TriggerType = ContainerAppJobTriggerType.Schedule;
                j.Configuration.ScheduleTriggerConfig.CronExpression = "*/5 * * * *";
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var container = Assert.Single(model.GetContainerResources());
        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task PublishAsContainerAppJob_WorksForProjectResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");
        builder.AddProject<Project>("job", launchProfileName: null)
            .PublishAsAzureContainerAppJob();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var project = Assert.Single(model.GetProjectResources());
        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task PublishAsContainerAppJob_ThrowsIfBothCustomizationsAreApplied()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        builder.AddProject<Project>("job", launchProfileName: null)
            .PublishAsAzureContainerApp((infra, app) => { })
            .PublishAsAzureContainerAppJob();

        using var app = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ExecuteBeforeStartHooksAsync(app, default));
    }

    [Fact]
    public async Task PublishAsContainerAppJob_ThrowsForAzureFunctions()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        builder.AddAzureFunctionsProject<TestFunctionsProject>("funcjob")
            .PublishAsAzureContainerAppJob();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var funcjob = model.Resources.Single(r => r.Name == "funcjob");
        funcjob.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await GetManifestWithBicep(resource));
    }

    private sealed class TestFunctionsProject : IProjectMetadata
    {
        public string ProjectPath => "functions-project";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--port 7071",
                    LaunchBrowser = false,
                }
            }
        };
    }

    [Fact]
    public async Task CanMixContainerAppsAndJobsInSameManifest()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("web", "nginx:latest")
            .PublishAsAzureContainerApp((infra, app) => { });

        builder.AddContainer("batch", "image:latest")
            .PublishAsAzureContainerAppJob();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containers = model.GetContainerResources().ToArray();
        Assert.Equal(2, containers.Length);

        var batch = containers.First(c => c.Name == "batch");
        var web = containers.First(c => c.Name == "web");

        var batchTarget = batch.Annotations.OfType<DeploymentTargetAnnotation>().FirstOrDefault();
        var webTarget = web.Annotations.OfType<DeploymentTargetAnnotation>().FirstOrDefault();

        var batchResource = batchTarget?.DeploymentTarget as AzureProvisioningResource;
        var webResource = webTarget?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(batchResource);
        Assert.NotNull(webResource);

        var (batchManifest, batchBicep) = await GetManifestWithBicep(batchResource);
        var (webManifest, webBicep) = await GetManifestWithBicep(webResource);

        Assert.Contains("Microsoft.App/jobs", batchBicep);
        Assert.Contains("Microsoft.App/containerApps", webBicep);
    }

    [Fact]
    public async Task PublishAsScheduledAzureContainerAppJobConfiguresScheduleTrigger()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        const string cronExpression = "0 0 * * *"; // Run every day at midnight

        builder.AddContainer("scheduled-job", "myimage")
            .PublishAsScheduledAzureContainerAppJob(cronExpression, (_, j) =>
            {
                j.Tags["metadata"] = "metadata-value";
            });

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // Verify the bicep contains job configuration
        Assert.Contains("Microsoft.App/jobs", bicep);
        Assert.Contains("Schedule", bicep);
        Assert.Contains(cronExpression, bicep);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task PublishAsAzureContainerAppJobParameterlessConfiguresManualTrigger()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        builder.AddContainer("manual-job", "myimage")
            .PublishAsAzureContainerAppJob();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ResourceWithProbes_HttpEndpoint()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder
            .AddContainer("api", "myimage")
            .WithHttpEndpoint()
            .WithHttpProbe(ProbeType.Readiness, "/ready")
            .WithHttpProbe(ProbeType.Liveness, "/health");

        builder
            .AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint()
            .WithHttpProbe(ProbeType.Readiness, "/ready", initialDelaySeconds: 60)
            .WithHttpProbe(ProbeType.Liveness, "/health");
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());
        var containerProvisioningResource = container.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(containerProvisioningResource);

        var project = Assert.Single(model.GetProjectResources());
        var projectProvisioningResource = project.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(projectProvisioningResource);

        var (_, containerBicep) = await GetManifestWithBicep(containerProvisioningResource);
        var (_, projectBicep) = await GetManifestWithBicep(projectProvisioningResource);

        await Verify(containerBicep, "bicep")
              .AppendContentAsFile(projectBicep, "bicep");
    }

    [Fact]
    public async Task ResourceWithProbes_HttpEndpoint_TargetPort()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder
            .AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 1111)
            .WithHttpProbe(ProbeType.Liveness, "/health");

        builder
            .AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 1111)
            .WithHttpProbe(ProbeType.Liveness, "/health");
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());
        var containerProvisioningResource = container.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(containerProvisioningResource);

        var project = Assert.Single(model.GetProjectResources());
        var projectProvisioningResource = project.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(projectProvisioningResource);

        var (_, containerBicep) = await GetManifestWithBicep(containerProvisioningResource);
        var (_, projectBicep) = await GetManifestWithBicep(projectProvisioningResource);

        await Verify(containerBicep, "bicep")
              .AppendContentAsFile(projectBicep, "bicep");
    }

    [Fact]
    public async Task ResourceWithProbes_HttpsEndpoint_TargetPort_MatchIngress()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder
            .AddContainer("api", "myimage")
            .WithHttpsEndpoint(targetPort: 1111)
            .WithHttpProbe(ProbeType.Liveness, "/health");

        builder
            .AddProject<Project>("project1", launchProfileName: null)
            .WithHttpsEndpoint(targetPort: 1111)
            .WithHttpProbe(ProbeType.Liveness, "/health");
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());
        var containerProvisioningResource = container.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(containerProvisioningResource);

        var project = Assert.Single(model.GetProjectResources());
        var projectProvisioningResource = project.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(projectProvisioningResource);

        var (_, containerBicep) = await GetManifestWithBicep(containerProvisioningResource);
        var (_, projectBicep) = await GetManifestWithBicep(projectProvisioningResource);

        await Verify(containerBicep, "bicep")
              .AppendContentAsFile(projectBicep, "bicep");
    }

    [Fact]
    public async Task BuildOnlyContainerResource_DoesNotGetDeployed()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        // Add a normal container resource
        builder.AddContainer("api", "myimage");

        // Add a build-only container resource
        builder.AddExecutable("build-only", "exe", ".")
            .PublishAsDockerFile(c =>
            {
                c.WithDockerfileBuilder(".", dockerfileContext =>
                {
                    var dockerBuilder = dockerfileContext.Builder
                        .From("scratch");
                });

                var dockerFileAnnotation = c.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
                dockerFileAnnotation.HasEntrypoint = false;
            });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = model.Resources.Single(r => r.Name == "api");
        var containerProvisioningResource = container.GetDeploymentTargetAnnotation()?.DeploymentTarget as AzureProvisioningResource;
        Assert.NotNull(containerProvisioningResource);

        var buildOnly = model.Resources.Single(r => r.Name == "build-only");
        Assert.Null(buildOnly.GetDeploymentTargetAnnotation());
    }

    [Fact]
    public async Task BindMountNamesWithHyphensAreNormalized()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        using var tempDirectory = new TestTempDirectory();

        // Contents of the Dockerfile are not important for this test
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Dockerfile"), "FROM alpine");

        builder.AddDockerfile("with-bind-mount", tempDirectory.Path)
            .WithBindMount(tempDirectory.Path, "/app/data");

        using var app = builder.Build();

        // This should not throw an exception about invalid Bicep identifier
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Assert.Single(model.GetContainerResources());

        container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);

        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task GetHostAddressExpression()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");

        var project = builder
            .AddProject<Project>("project1", launchProfileName: null)
            .WithHttpEndpoint();

        var endpointReferenceEx = env.Resource.GetHostAddressExpression(project.GetEndpoint("http"));
        Assert.NotNull(endpointReferenceEx);

        Assert.Equal("project1.internal.{0}", endpointReferenceEx.Format);
        var provider = Assert.Single(endpointReferenceEx.ValueProviders);
        var output = Assert.IsType<BicepOutputReference>(provider);
        Assert.Equal(env.Resource, output.Resource);
        Assert.Equal("AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN", output.Name);
    }

    [Fact]
    public async Task ContainerAppProvisionDependsOnTargetPushStep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");
        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var projectResource = Assert.Single(model.GetProjectResources());

        projectResource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var containerAppResource = target?.DeploymentTarget as AzureContainerAppResource;
        Assert.NotNull(containerAppResource);

        var configAnnotations = containerAppResource.Annotations.OfType<PipelineConfigurationAnnotation>().ToList();
        Assert.NotEmpty(configAnnotations);
    }

    [Fact]
    public async Task EnvironmentCreatesDefaultAcrWhenNoExplicitRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var acrResources = model.Resources.OfType<AzureContainerRegistryResource>().ToList();
        Assert.Single(acrResources);

        var defaultAcr = acrResources[0];
        Assert.Contains("acr", defaultAcr.Name);
    }

    [Fact]
    public async Task DefaultAcrNotAddedToModelWhenExplicitRegistryExists()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var customRegistry = builder.AddAzureContainerRegistry("customregistry");
        builder.AddAzureContainerAppEnvironment("env")
            .WithAzureContainerRegistry(customRegistry);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var acrResources = model.Resources.OfType<AzureContainerRegistryResource>().ToList();
        Assert.Single(acrResources);
        Assert.Equal("customregistry", acrResources[0].Name);
    }

    [Fact]
    public async Task EnvironmentDelegatesToAssociatedRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var customRegistry = builder.AddAzureContainerRegistry("customregistry");
        var env = builder.AddAzureContainerAppEnvironment("env")
            .WithAzureContainerRegistry(customRegistry);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var containerRegistryInterface = env.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistryInterface);
        Assert.NotNull(containerRegistryInterface.Endpoint);
        Assert.NotNull(containerRegistryInterface.Name);
    }

    [Fact]
    public async Task DefaultContainerRegistryUsesAzdNamingWhenEnvironmentDoes()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env")
            .WithAzdResourceNaming();

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint();

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var acrResources = model.Resources.OfType<AzureContainerRegistryResource>().ToList();
        Assert.Single(acrResources);

        var defaultAcr = acrResources[0];
        var (manifest, bicep) = await GetManifestWithBicep(defaultAcr);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task MultipleComputeEnvironmentsOnlyProcessTargetedResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var aca = builder.AddAzureContainerAppEnvironment("aca");
        var appService = builder.AddAzureAppServiceEnvironment("appservice");

        // Project targeted to ACA
        var webappaca = builder.AddProject<Project>("webappaca", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(aca);

        // Project targeted to App Service
        var webappservice = builder.AddProject<Project>("webappservice", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(appService);

        // Container targeted to ACA with port 80 - this works for ACA
        var containerForAca = builder.AddContainer("containeraca", "redis")
            .WithHttpEndpoint(port: 80, targetPort: 6379, name: "http")
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(aca);

        // Container targeted to App Service with custom port 8123.
        // Before the fix, ACA would try to process this and throw an error about port 80 requirement.
        // After the fix, ACA skips it because it's targeted to a different environment.
        // Note: We use AddContainer here to test the filtering, even though App Service doesn't support
        // regular containers (only Dockerfiles). The key is that ACA should NOT try to validate it.
        var containerForAppService = builder.AddContainer("containerappservice", "redis")
            .WithHttpEndpoint(port: 8123, targetPort: 6379, name: "http")
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(appService);

        using var app = builder.Build();

        // This should not throw an exception about port 80 requirement from ACA
        // because containerForAppService is targeted to AppService, and ACA should skip it
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify webappaca has a deployment target for ACA
        var webappAcaResource = model.Resources.First(r => r.Name == "webappaca");
        var webappAcaTarget = webappAcaResource.GetDeploymentTargetAnnotation(aca.Resource);
        Assert.NotNull(webappAcaTarget);
        Assert.Same(aca.Resource, webappAcaTarget.ComputeEnvironment);

        // Verify webappservice has a deployment target for AppService
        var webappServiceResource = model.Resources.First(r => r.Name == "webappservice");
        var webappServiceTarget = webappServiceResource.GetDeploymentTargetAnnotation(appService.Resource);
        Assert.NotNull(webappServiceTarget);
        Assert.Same(appService.Resource, webappServiceTarget.ComputeEnvironment);

        // Verify containerForAca has a deployment target for ACA
        var containerAcaResource = model.Resources.First(r => r.Name == "containeraca");
        var containerAcaTarget = containerAcaResource.GetDeploymentTargetAnnotation(aca.Resource);
        Assert.NotNull(containerAcaTarget);
        Assert.Same(aca.Resource, containerAcaTarget.ComputeEnvironment);

        // Verify containerForAppService does NOT have a deployment target from ACA
        // (It won't have one from AppService either because AppService doesn't support regular containers,
        // but the important thing is that ACA didn't try to process it and throw an error)
        var containerAppServiceResource = model.Resources.First(r => r.Name == "containerappservice");
        var containerAppServiceAcaTarget = containerAppServiceResource.GetDeploymentTargetAnnotation(aca.Resource);
        Assert.Null(containerAppServiceAcaTarget);

        // Verify resources do NOT have deployment targets for other environments
        Assert.Null(webappAcaResource.GetDeploymentTargetAnnotation(appService.Resource));
        Assert.Null(webappServiceResource.GetDeploymentTargetAnnotation(aca.Resource));
        Assert.Null(containerAcaResource.GetDeploymentTargetAnnotation(appService.Resource));
    }

    [Fact]
    public async Task RedisWithConditionalConnectionString()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var redis = builder.AddRedis("cache");

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(redis);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var proj = Assert.Single(model.GetProjectResources());
        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task RedisWithTlsEnabledConditionalConnectionString()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var redis = builder.AddRedis("cache");
        redis.WithEndpoint("tcp", e => e.TlsEnabled = true);

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(redis);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var proj = Assert.Single(model.GetProjectResources());
        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ConditionalExpressionWithParameterCondition()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var featureFlag = builder.AddParameter("enable-feature");

        var project = builder.AddProject<Project>("api", launchProfileName: null);

        project.WithEnvironment(context =>
        {
            var conditional = ReferenceExpression.CreateConditional(
                featureFlag.Resource,
                bool.TrueString,
                ReferenceExpression.Create($"enabled"),
                ReferenceExpression.Create($"disabled"));

            context.EnvironmentVariables["FEATURE_MODE"] = conditional;
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var proj = Assert.Single(model.GetProjectResources());
        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ConditionalBranchWithParameterReference()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var featureFlag = builder.AddParameter("enable-feature");
        var connectionPrefix = builder.AddParameter("connection-prefix");

        var project = builder.AddProject<Project>("api", launchProfileName: null);

        project.WithEnvironment(context =>
        {
            var conditional = ReferenceExpression.CreateConditional(
                featureFlag.Resource,
                bool.TrueString,
                ReferenceExpression.Create($"prefix-{connectionPrefix.Resource}-enabled"),
                ReferenceExpression.Create($"disabled"));

            context.EnvironmentVariables["FEATURE_CONNECTION"] = conditional;
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var proj = Assert.Single(model.GetProjectResources());
        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task NestedConditionalExpressions()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var outerFlag = builder.AddParameter("outer-flag");
        var innerFlag = builder.AddParameter("inner-flag");

        var project = builder.AddProject<Project>("api", launchProfileName: null);

        project.WithEnvironment(context =>
        {
            var innerConditional = ReferenceExpression.CreateConditional(
                innerFlag.Resource,
                bool.TrueString,
                ReferenceExpression.Create($"inner-true"),
                ReferenceExpression.Create($"inner-false"));

            var outerConditional = ReferenceExpression.CreateConditional(
                outerFlag.Resource,
                bool.TrueString,
                innerConditional,
                ReferenceExpression.Create($"outer-false"));

            context.EnvironmentVariables["NESTED_FEATURE"] = outerConditional;
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var proj = Assert.Single(model.GetProjectResources());
        proj.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var resource = target?.DeploymentTarget as AzureProvisioningResource;

        Assert.NotNull(resource);

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }
}
