// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFunctionsTests
{
    [Fact]
    public async Task AddAzureFunctionsProject_Works()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var funcApp = builder.AddAzureFunctionsProject<TestProject>("funcapp");

        var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // Assert that default storage resource is configured
        Assert.Contains(builder.Resources, resource =>
            resource is AzureStorageResource && resource.Name.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName));
        // Assert that custom project resource type is configured
        Assert.Contains(builder.Resources, resource =>
            resource is AzureFunctionsProjectResource && resource.Name == "funcapp");

        var storage = Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        Assert.True(funcApp.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relAnnotations));

        var rel = Assert.Single(relAnnotations);
        Assert.Equal("Reference", rel.Type);
        Assert.Equal(storage, rel.Resource);
    }

    [Fact]
    public async Task AddAzureFunctionsProject_WiresUpHttpEndpointCorrectly_WhenPortArgumentIsProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        // Assert that the EndpointAnnotation is configured correctly
        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetLastAnnotation<EndpointAnnotation>(out var endpointAnnotation));
        Assert.Equal(7071, endpointAnnotation.Port);
        Assert.Equal(7071, endpointAnnotation.TargetPort);
        Assert.False(endpointAnnotation.IsProxied);

        // Check that no `--port` is present in the generated argument
        // list if it's already defined in the launch profile
        using var app = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(functionsResource);

        Assert.Empty(args);
    }

    [Fact]
    public async Task AddAzureFunctionsProject_WiresUpHttpEndpointCorrectly_WhenPortArgumentIsNotProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProjectWithoutPortArgument>("funcapp")
            // Explicit set endpoint values for assertions later
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new(e, "localhost", 1234);
                e.TargetPort = 9876;
            });

        // Assert that the EndpointAnnotation is configured correctly
        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetLastAnnotation<EndpointAnnotation>(out var endpointAnnotation));
        Assert.Null(endpointAnnotation.Port);
        Assert.Equal(9876, endpointAnnotation.TargetPort);
        Assert.True(endpointAnnotation.IsProxied);

        // Check that `--port` is present in the args
        using var app = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(functionsResource);

        Assert.Collection(args,
            arg => Assert.Equal("--port", arg),
            arg => Assert.Equal("9876", arg)
        );
    }

    [Fact]
    public void AddAzureFunctionsProject_WiresUpHttpEndpointCorrectly_WhenMultiplePortArgumentsProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProjectWithMultiplePorts>("funcapp");

        // Assert that the EndpointAnnotation is configured correctly
        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetLastAnnotation<EndpointAnnotation>(out var endpointAnnotation));
        Assert.Equal(7072, endpointAnnotation.Port);
        Assert.Equal(7072, endpointAnnotation.TargetPort);
        Assert.False(endpointAnnotation.IsProxied);
    }

    [Fact]
    public void AddAzureFunctionsProject_WiresUpHttpEndpointCorrectly_WhenPortArgumentIsMalformed()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProjectWithMalformedPort>("funcapp");

        // Assert that the EndpointAnnotation is configured correctly
        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetLastAnnotation<EndpointAnnotation>(out var endpointAnnotation));
        Assert.Null(endpointAnnotation.Port);
        Assert.Null(endpointAnnotation.TargetPort);
        Assert.True(endpointAnnotation.IsProxied);
    }

    [Fact]
    public void AddAzureFunctionsProject_WiresUpHttpEndpointCorrectly_WhenPortArgumentIsPartial()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProjectWithPartialPort>("funcapp");

        // Assert that the EndpointAnnotation is configured correctly
        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetLastAnnotation<EndpointAnnotation>(out var endpointAnnotation));
        Assert.Null(endpointAnnotation.Port);
        Assert.Null(endpointAnnotation.TargetPort);
        Assert.True(endpointAnnotation.IsProxied);
    }

    [Fact]
    public void AddAzureFunctionsProject_GeneratesUniqueDefaultHostStorageResourceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProjectWithMalformedPort>("funcapp");

        // Assert that the default storage resource is unique
        var storageResources = Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        Assert.NotEqual(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName, storageResources.Name);
        Assert.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName, storageResources.Name);
    }

    [Fact]
    [RequiresDocker]
    public async Task AddAzureFunctionsProject_RemoveDefaultHostStorageWhenUseHostStorageIsUsed()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var storage = builder.AddAzureStorage("my-own-storage").RunAsEmulator();
        var funcApp = builder.AddAzureFunctionsProject<TestProjectWithMalformedPort>("funcapp")
            .WithHostStorage(storage);

        using var host = builder.Build();
        await host.StartAsync();

        // Assert that the default storage resource is not present
        var model = host.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.DoesNotContain(model.Resources.OfType<AzureStorageResource>(),
            r => r.Name.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName));
        var storageResource = Assert.Single(model.Resources.OfType<AzureStorageResource>());
        Assert.Equal("my-own-storage", storageResource.Name);

        Assert.True(funcApp.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relAnnotations));
        var rel = Assert.Single(relAnnotations);
        Assert.Equal("Reference", rel.Type);
        Assert.Equal(storage.Resource, rel.Resource);

        await host.StopAsync();
    }

    [Fact]
    [RequiresDocker]
    public async Task AddAzureFunctionsProject_WorksWithMultipleProjects()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProject>("funcapp");
        builder.AddAzureFunctionsProject<TestProject>("funcapp2");

        using var host = builder.Build();
        await host.StartAsync();

        // Assert that the default storage resource is not present
        var model = host.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Single(model.Resources.OfType<AzureStorageResource>(),
            r => r.Name.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName));

        await host.StopAsync();
    }

    [Fact]
    public void AddAzureFunctionsProject_UsesCorrectNameUnderPublish()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        var resource = Assert.Single(builder.Resources.OfType<AzureStorageResource>());

        Assert.NotEqual(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName, resource.Name);
        Assert.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName, resource.Name);
    }

    [Fact]
    public void AddAzureFunctionsProject_WiresUpHttpsEndpointCorrectly_WhenUseHttpsArgumentIsProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProjectWithHttps>("funcapp");

        // Assert that the EndpointAnnotation is configured correctly
        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetLastAnnotation<EndpointAnnotation>(out var endpointAnnotation));
        Assert.Equal(7071, endpointAnnotation.Port);
        Assert.Equal(7071, endpointAnnotation.TargetPort);
        Assert.False(endpointAnnotation.IsProxied);
        Assert.Equal("https", endpointAnnotation.UriScheme);
    }

    [Fact]
    public async Task AddAzureFunctionsProject_ConfiguresEnvironmentVariables_WhenInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var envAnnotations));

        var context = new EnvironmentCallbackContext(builder.ExecutionContext);
        foreach (var envAnnotation in envAnnotations)
        {
            await envAnnotation.Callback(context);
        }

        // Verify ASPNETCORE_URLS is set correctly with the target port
        var aspNetCoreUrls = context.EnvironmentVariables["ASPNETCORE_URLS"];
        Assert.NotNull(aspNetCoreUrls);
        var aspNetCoreUrlsValue = await ((ReferenceExpression)aspNetCoreUrls).GetValueAsync(default);
        Assert.Contains("8080", aspNetCoreUrlsValue);
    }

    [Fact]
    public async Task AddAzureFunctionsProject_WiresUpHttpsEndpointCorrectly_WhenOnlyUseHttpsArgumentIsProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureFunctionsProject<TestProjectWithHttpsNoPort>("funcapp")
            // Explicit set endpoint values for assertions later
            .WithEndpoint("https", e =>
            {
                e.UriScheme = "https";
                e.AllocatedEndpoint = new(e, "localhost", 1234);
                e.TargetPort = 9876;
            });

        // Assert that the EndpointAnnotation is configured correctly
        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetLastAnnotation<EndpointAnnotation>(out var endpointAnnotation));
        Assert.Null(endpointAnnotation.Port);
        Assert.Equal(9876, endpointAnnotation.TargetPort);
        Assert.True(endpointAnnotation.IsProxied);
        Assert.Equal("https", endpointAnnotation.UriScheme);

        // Check that `--port` is present in the args
        using var app = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(functionsResource);

        Assert.Collection(args,
            arg => Assert.Equal("--port", arg),
            arg => Assert.Equal("9876", arg)
        );
    }

    [Fact]
    public async Task AddAzureFunctionsProject_CanGetStorageManifestSuccessfully()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // hardcoded sha256 to make the storage name deterministic
        builder.Configuration["AppHost:Sha256"] = "634f8";
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        var project = builder.AddAzureFunctionsProject<TestProjectWithHttpsNoPort>("funcapp");

        var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var storage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "funcstorage634f8");

        var (storageManifest, _) = await GetManifestWithBicep(storage);

        var expectedRolesManifest =
            """
            {
              "type": "azure.bicep.v0",
              "path": "funcstorage634f8.module.bicep"
            }
            """;
        Assert.Equal(expectedRolesManifest, storageManifest.ToString());
    }

    [Fact]
    public async Task AddAzureFunctionsProject_WorksWithAddAzureContainerAppsInfrastructure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        // hardcoded sha256 to make the storage name deterministic
        builder.Configuration["AppHost:Sha256"] = "634f8";
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        var funcApp = builder.AddAzureFunctionsProject<TestProjectWithHttpsNoPort>("funcapp");

        var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (_, bicep) = await GetManifestWithBicep(funcApp.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget);

        var projRolesStorage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "funcapp-roles-funcstorage634f8");

        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(projRolesStorage);

        await Verify(bicep, "bicep")
              .AppendContentAsFile(rolesManifest.ToString(), "json")
              .AppendContentAsFile(rolesBicep, "bicep");
    }

    [Fact]
    public async Task AddAzureFunctionsProject_WorksWithAddAzureContainerAppsInfrastructure_WithHostStorage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        // hardcoded sha256 to make the storage name deterministic
        var storage = builder.AddAzureStorage("my-own-storage").RunAsEmulator();
        builder.Configuration["AppHost:Sha256"] = "634f8";
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        builder.AddAzureFunctionsProject<TestProjectWithHttpsNoPort>("funcapp")
            .WithHostStorage(storage);

        var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var projRolesStorage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "funcapp-roles-my-own-storage");

        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(projRolesStorage);

        await Verify(rolesManifest.ToString(), "json")
              .AppendContentAsFile(rolesBicep, "bicep");

    }

    [Fact]
    public async Task AddAzureFunctionsProject_WorksWithAddAzureContainerAppsInfrastructure_WithHostStorage_WithRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        // hardcoded sha256 to make the storage name deterministic
        var storage = builder.AddAzureStorage("my-own-storage").RunAsEmulator();
        builder.Configuration["AppHost:Sha256"] = "634f8";
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        builder.AddAzureFunctionsProject<TestProjectWithHttpsNoPort>("funcapp")
            .WithHostStorage(storage)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataOwner);

        var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var projRolesStorage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "funcapp-roles-my-own-storage");

        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(projRolesStorage);

        await Verify(rolesManifest.ToString(), "json")
              .AppendContentAsFile(rolesBicep, "bicep");

    }

    [Fact]
    public async Task MultipleAddAzureFunctionsProject_WorksWithAddAzureContainerAppsInfrastructure_WithHostStorage_WithRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        // hardcoded sha256 to make the storage name deterministic
        var storage = builder.AddAzureStorage("my-own-storage").RunAsEmulator();
        builder.Configuration["AppHost:Sha256"] = "634f8";
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        builder.AddAzureFunctionsProject<TestProjectWithHttpsNoPort>("funcapp")
            .WithHostStorage(storage)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataOwner);

        builder.AddAzureFunctionsProject<TestProjectWithHttpsNoPort>("funcapp2");

        var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var projRolesStorage = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "funcapp-roles-my-own-storage");
        var projRolesStorage2 = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "funcapp2-roles-funcstorage634f8");

        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(projRolesStorage);
        var (rolesManifest2, rolesBicep2) = await GetManifestWithBicep(projRolesStorage2);

        await Verify(rolesManifest.ToString(), "json")
              .AppendContentAsFile(rolesBicep, "bicep")
              .AppendContentAsFile(rolesManifest2.ToString(), "json")
              .AppendContentAsFile(rolesBicep2, "bicep");

    }

    private static Task<(JsonNode ManifestNode, string BicepText)> GetManifestWithBicep(IResource resource) =>
        AzureManifestUtils.GetManifestWithBicep(resource, skipPreparer: true);

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "some-path";

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

    private sealed class TestProjectWithMalformedPort : IProjectMetadata
    {
        public string ProjectPath => "some-path";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--port 70b1",
                    LaunchBrowser = false,
                }
            }
        };
    }

    private sealed class TestProjectWithPartialPort : IProjectMetadata
    {
        public string ProjectPath => "some-path";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--port",
                    LaunchBrowser = false,
                }
            }
        };
    }

    private sealed class TestProjectWithoutPortArgument : IProjectMetadata
    {
        public string ProjectPath => "some-path";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    LaunchBrowser = false,
                }
            }
        };
    }

    private sealed class TestProjectWithMultiplePorts : IProjectMetadata
    {
        public string ProjectPath => "some-path";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--port 7072 --port 7071",
                    LaunchBrowser = false,
                }
            }
        };
    }

    private sealed class TestProjectWithHttps : IProjectMetadata
    {
        public string ProjectPath => "some-path";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--port 7071 --useHttps",
                    LaunchBrowser = false,
                }
            }
        };
    }

    private sealed class TestProjectWithHttpsNoPort : IProjectMetadata
    {
        public string ProjectPath => "some-path";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--useHttps",
                    LaunchBrowser = false,
                }
            }
        };
    }

    [Fact]
    public void AddAzureFunctionsProject_AddsDefaultLaunchProfileAnnotation_WhenConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Set the AppHost default launch profile configuration
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "TestProfile";

        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());

        // Verify that the DefaultLaunchProfileAnnotation is added
        Assert.True(functionsResource.TryGetLastAnnotation<DefaultLaunchProfileAnnotation>(out var annotation));
        Assert.Equal("TestProfile", annotation.LaunchProfileName);
    }

    [Fact]
    public void AddAzureFunctionsProject_AddsDefaultLaunchProfileAnnotation_FromDotnetLaunchProfile()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Set the DOTNET_LAUNCH_PROFILE configuration
        builder.Configuration["DOTNET_LAUNCH_PROFILE"] = "DotnetProfile";

        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());

        // Verify that the DefaultLaunchProfileAnnotation is added
        Assert.True(functionsResource.TryGetLastAnnotation<DefaultLaunchProfileAnnotation>(out var annotation));
        Assert.Equal("DotnetProfile", annotation.LaunchProfileName);
    }

    [Fact]
    public void AddAzureFunctionsProject_DoesNotAddLaunchProfileAnnotation_WhenNoConfigurationSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());

        // Verify that no DefaultLaunchProfileAnnotation is added when no configuration is set
        Assert.False(functionsResource.TryGetLastAnnotation<DefaultLaunchProfileAnnotation>(out _));
    }

    [Fact]
    public void AddAzureFunctionsProject_AppHostConfigurationOverridesDotnetLaunchProfile()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Set both configurations, AppHost should take precedence
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "AppHostProfile";
        builder.Configuration["DOTNET_LAUNCH_PROFILE"] = "DotnetProfile";

        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());

        // Verify that AppHost configuration takes precedence
        Assert.True(functionsResource.TryGetLastAnnotation<DefaultLaunchProfileAnnotation>(out var annotation));
        Assert.Equal("AppHostProfile", annotation.LaunchProfileName);
    }

    [Fact]
    public void AddAzureFunctionsProject_WithProjectPath_Works()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a temporary project file
        var projectPath = Path.Combine(tempDir.Path, "TestFunctions.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var funcApp = builder.AddAzureFunctionsProject("funcapp", projectPath);

        // Assert that default storage resource is configured
        Assert.Contains(builder.Resources, resource =>
            resource is AzureStorageResource && resource.Name.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName));
        // Assert that custom project resource type is configured
        Assert.Contains(builder.Resources, resource =>
            resource is AzureFunctionsProjectResource && resource.Name == "funcapp");

        // Verify that the project metadata annotation is added
        Assert.True(funcApp.Resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata));
        Assert.NotNull(projectMetadata);
        Assert.Contains("TestFunctions.csproj", projectMetadata.ProjectPath);
    }

    [Fact]
    public void AddAzureFunctionsProject_WithProjectPath_NormalizesPath()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a temporary project file
        var projectPath = Path.Combine(tempDir.Path, "MyFunctions.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Use a relative path from the builder's directory
        var relativePath = Path.GetRelativePath(builder.AppHostDirectory, projectPath);
        var funcApp = builder.AddAzureFunctionsProject("funcapp", relativePath);

        // Verify that the project metadata annotation is added with normalized path
        Assert.True(funcApp.Resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata));
        Assert.NotNull(projectMetadata);

        // The path should be normalized to an absolute path
        Assert.True(Path.IsPathRooted(projectMetadata.ProjectPath));
    }

    [Fact]
    public async Task AddAzureFunctionsProject_WithProjectPath_ConfiguresEnvironmentVariables()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a temporary project file
        var projectPath = Path.Combine(tempDir.Path, "TestFunctions.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        builder.AddAzureFunctionsProject("funcapp", projectPath);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());
        Assert.True(functionsResource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var envAnnotations));

        var context = new EnvironmentCallbackContext(builder.ExecutionContext);
        foreach (var envAnnotation in envAnnotations)
        {
            await envAnnotation.Callback(context);
        }

        // Verify common environment variables are set
        Assert.True(context.EnvironmentVariables.ContainsKey("FUNCTIONS_WORKER_RUNTIME"));
        Assert.True(context.EnvironmentVariables.ContainsKey("AzureFunctionsJobHost__telemetryMode"));
    }

    [Fact]
    public void AddAzureFunctionsProject_WithProjectPath_SharesDefaultStorage()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create temporary project files
        var projectPath1 = Path.Combine(tempDir.Path, "Functions1.csproj");
        var projectPath2 = Path.Combine(tempDir.Path, "Functions2.csproj");
        File.WriteAllText(projectPath1, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(projectPath2, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        builder.AddAzureFunctionsProject("funcapp1", projectPath1);
        builder.AddAzureFunctionsProject("funcapp2", projectPath2);

        // Assert that only one default storage resource exists and is shared
        var storageResources = builder.Resources.OfType<AzureStorageResource>()
            .Where(r => r.Name.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName))
            .ToList();
        Assert.Single(storageResources);
    }

    [Fact]
    [RequiresDocker]
    public async Task AddAzureFunctionsProject_WithProjectPath_CanUseCustomHostStorage()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a temporary project file
        var projectPath = Path.Combine(tempDir.Path, "Functions.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var customStorage = builder.AddAzureStorage("my-custom-storage").RunAsEmulator();
        var funcApp = builder.AddAzureFunctionsProject("funcapp", projectPath)
            .WithHostStorage(customStorage);

        using var host = builder.Build();
        await host.StartAsync();

        // Assert that the custom storage is used and default storage is not present
        var model = host.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.DoesNotContain(model.Resources.OfType<AzureStorageResource>(),
            r => r.Name.StartsWith(AzureFunctionsProjectResourceExtensions.DefaultAzureFunctionsHostStorageName));
        var storageResource = Assert.Single(model.Resources.OfType<AzureStorageResource>());
        Assert.Equal("my-custom-storage", storageResource.Name);

        Assert.True(funcApp.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relAnnotations));
        var rel = Assert.Single(relAnnotations);
        Assert.Equal("Reference", rel.Type);
        Assert.Equal(customStorage.Resource, rel.Resource);

        await host.StopAsync();
    }

    [Fact]
    public void AddAzureFunctionsProject_WithProjectPath_AddsAzureFunctionsAnnotation()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a temporary project file
        var projectPath = Path.Combine(tempDir.Path, "Functions.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        builder.AddAzureFunctionsProject("funcapp", projectPath);

        var functionsResource = Assert.Single(builder.Resources.OfType<AzureFunctionsProjectResource>());

        // Verify that AzureFunctionsAnnotation is added
        Assert.True(functionsResource.TryGetLastAnnotation<AzureFunctionsAnnotation>(out _));
    }

    [Fact]
    public void AddAzureFunctionsProject_RegistersFuncCoreToolsInstallationManager()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzureFunctionsProject<TestProject>("funcapp");

        // Verify that FuncCoreToolsInstallationManager is registered as a singleton
        var descriptor = builder.Services.FirstOrDefault(s => s.ServiceType == typeof(FuncCoreToolsInstallationManager));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
