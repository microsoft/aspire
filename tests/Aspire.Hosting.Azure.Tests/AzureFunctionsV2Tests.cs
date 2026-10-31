// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZUREFUNCTIONS001 // Mixed-model regression coverage includes Node Functions.
#pragma warning disable ASPIREEXTENSION001 // Verify the Functions IDE launch contract.
#pragma warning disable ASPIREPERSISTENCE001 // Verify persistent resources remain on the process path.
#pragma warning disable ASPIREPROJECTS001 // Verify shared defaults are applied only once.
#pragma warning disable ASPIREDURABLETASK001 // Verify the Functions Durable Task binding adapter.
#pragma warning disable ASPIRECOMMAND001 // Verify Core Tools prerequisite metadata.
#pragma warning disable CS0618 // Mixed-model regression coverage deliberately retains legacy Functions.

using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFunctionsV2Tests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureFunctions_UsesManagedDotnetResourceAndResolvedPath(bool useDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(options => options.ProjectDirectory = workspace.Path, outputHelper);

        var functions = builder.AddAzureFunctions("functions", useDirectory ? "Functions" : Path.GetRelativePath(workspace.Path, path));

        Assert.IsType<AzureFunctionsResource>(functions.Resource);
        Assert.IsAssignableFrom<DotnetProjectResource>(functions.Resource);
        Assert.IsAssignableFrom<IDotnetProgramResource>(functions.Resource);
        Assert.Contains(typeof(IResourceWithCustomWithReference<AzureFunctionsResource>), functions.Resource.GetType().GetInterfaces());
        Assert.Equal(path, functions.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(Path.GetDirectoryName(path), functions.Resource.WorkingDirectory);
        Assert.Single(functions.Resource.Annotations.OfType<IProjectMetadata>());
        Assert.Single(functions.Resource.Annotations.OfType<AzureFunctionsAnnotation>());
        Assert.True(functions.Resource.SupportsDotnetProgramPublishing());
        Assert.Equal("func", Assert.Single(functions.Resource.Annotations.OfType<RequiredCommandAnnotation>()).Command);
        Assert.Throws<InvalidOperationException>(() => functions.WithDotnetBaseDefaults(new ProjectResourceOptions()));
    }

    [Theory]
    [InlineData("Functions.cs")]
    [InlineData("Functions.CS")]
    public void AddAzureFunctions_RejectsFileBasedAppsBeforeRegistration(string path)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() => builder.AddAzureFunctions("functions", path));

        Assert.Equal("Azure Functions requires an SDK-style project. File-based C# apps are not supported.", exception.Message);
        Assert.Empty(builder.Resources.OfType<AzureFunctionsResource>());
        Assert.Empty(builder.Resources.OfType<AzureStorageResource>());
    }

    [Fact]
    public void AddAzureFunctions_UsesV2DirectoryAmbiguityValidation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "Other.fsproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() => builder.AddAzureFunctions("functions", Path.GetDirectoryName(path)!));

        Assert.Contains("exactly one .csproj, .fsproj, or .vbproj", exception.Message);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("azure-functions", true, false)]
    [InlineData("azure-functions-with-external-build.v1", true, true)]
    [InlineData("project-with-external-build.v1", false, true)]
    [InlineData("project", false, true)]
    [InlineData("python", false, true)]
    [InlineData("", false, true)]
    public void AddAzureFunctions_NegotiatesFunctionsBuildOwnership(string? capability, bool ideLaunch, bool aspireBuild)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        if (capability is not null)
        {
            AzureFunctionsTestHelpers.ConfigureDebugSession(builder, capability.Length == 0 ? [] : [capability]);
        }

        var functions = builder.AddAzureFunctions("functions", path);

        Assert.Equal(ideLaunch, functions.Resource.SupportsDebugging(builder.Configuration, out _));
        Assert.Equal(aspireBuild, functions.Resource.GetProjectMetadata().SuppressBuild);
        var annotation = functions.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().Last();
        Assert.Equal(
            capability == "azure-functions" ? "azure-functions" : "azure-functions-with-external-build.v1",
            annotation.LaunchConfigurationType);
    }

    [Fact]
    public async Task AddAzureFunctions_CapabilitylessVisualStudioUsesCompatibleProjectPayload()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071 --useHttps");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, capabilities: null);
        var functions = builder.AddAzureFunctions("functions", path);
        using var app = builder.Build();

        Assert.True(functions.Resource.SupportsDebugging(builder.Configuration, out var annotation));
        Assert.Equal("project", annotation.LaunchConfigurationType);
        Assert.False(functions.Resource.GetProjectMetadata().SuppressBuild);
        var launch = Assert.IsType<ProjectLaunchConfiguration>(await functions.Resource.CreateLaunchConfigurationAsync("NoDebug"));
        Assert.Equal(path, launch.ProjectPath);
        Assert.Equal("Functions", launch.LaunchProfile);
        Assert.False(launch.SuppressBuild);
        Assert.False(launch.DisableLaunchProfile);
        Assert.Empty(await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
    }

    [Fact]
    public async Task AddAzureFunctions_LegacyLauncherRetainsLegacyPayloadAndOwnsBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071 --useHttps");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions", "project-with-external-build.v1"]);
        var functions = builder.AddAzureFunctions("functions", path);
        using var app = builder.Build();

        var launch = await functions.Resource.CreateLaunchConfigurationAsync("Debug");

        Assert.False(functions.Resource.GetProjectMetadata().SuppressBuild);
        await Verify(JsonSerializer.Serialize(launch), "json")
            .AddScrubber(text => text.Replace(JsonEncodedText.Encode(path).ToString(), "Functions.csproj"));
        Assert.Equal(
            ["--port", "7071", "--useHttps"],
            await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
    }

    [Theory]
    [InlineData("azure-functions")]
    [InlineData("azure-functions-with-external-build.v1")]
    [InlineData(null)]
    public void AddAzureFunctions_ForcedProcessAndPersistentLifetimeDoNotDelegateToIde(string? capability)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, capability is null ? null : [capability]);
        var forced = builder.AddAzureFunctions("forced", path).WithTerminal();
        var persistent = builder.AddAzureFunctions("persistent", path).WithPersistentLifetime();

        Assert.False(forced.Resource.SupportsDebugging(builder.Configuration, out _));
        Assert.False(persistent.Resource.SupportsDebugging(builder.Configuration, out _));
    }

    [Theory]
    [InlineData("azure-functions", false, true)]
    [InlineData(null, false, true)]
    [InlineData("azure-functions-with-external-build.v1", false, false)]
    [InlineData("project-with-external-build.v1", false, false)]
    [InlineData("azure-functions", true, false)]
    [InlineData("no-ide", false, false)]
    public async Task AddAzureFunctions_WarnsOnceAtStartupOnlyForActuallySelectedIdeFallback(
        string? capability, bool persistent, bool expectedWarning)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        var sink = new TestSink();
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(sink)));
        if (capability != "no-ide")
        {
            AzureFunctionsTestHelpers.ConfigureDebugSession(builder, capability is null ? null : [capability]);
        }
        var functions = builder.AddAzureFunctions("functions", path).WithBuildEnvironment("BUILD_FLAVOR", "never-log-this-value");
        if (persistent)
        {
            functions.WithPersistentLifetime();
        }
        using var app = builder.Build();
        var registrationLogs = sink.Writes.Where(write => write.LoggerName == typeof(AzureFunctionsResource).FullName).ToArray();
        Assert.Empty(registrationLogs);

        await ExecuteBeforeStartHooksAsync(app, default);
        await ExecuteBeforeStartHooksAsync(app, default);

        var warnings = sink.Writes.Where(write => write.LoggerName == typeof(AzureFunctionsResource).FullName && write.LogLevel == LogLevel.Warning).ToArray();
        if (expectedWarning)
        {
            Assert.Equal(
                "Azure Functions resource 'functions' is using an IDE-built fallback because the IDE does not support 'azure-functions-with-external-build.v1'. " +
                "The IDE may use different build configuration or build environment settings. Upgrade the IDE integration or run outside the IDE to use Aspire-owned builds.",
                Assert.Single(warnings).Message);
        }
        else
        {
            Assert.Empty(warnings);
        }
    }

    [Theory]
    [InlineData("--port 7071", "http", 7071)]
    [InlineData("--port 7071 --useHttps", "https", 7071)]
    [InlineData("--port 7072 --port 7071", "http", 7072)]
    [InlineData("--port invalid", "http", null)]
    [InlineData("--port", "http", null)]
    [InlineData("--useHttps", "https", null)]
    [InlineData(null, "http", null)]
    public async Task AddAzureFunctions_PreservesFunctionsEndpointsAndProfileArguments(string? arguments, string scheme, int? port)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, arguments);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddAzureFunctions("functions", path);
        var endpoint = Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(scheme, endpoint.Name);
        Assert.Equal(scheme, endpoint.UriScheme);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(port, endpoint.TargetPort);
        Assert.Equal(port is null, endpoint.IsProxied);
        functions.WithEndpoint(scheme, endpoint => endpoint.TargetPort ??= 9876);
        using var app = builder.Build();

        var actualArguments = await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services);
        var expectedArguments = port is null ? new List<string> { "--port", "9876" } : [];
        expectedArguments.AddRange(CommandLineArgsParser.Parse(arguments ?? string.Empty));

        Assert.Equal(expectedArguments, actualArguments);
    }

    [Fact]
    public async Task AddAzureFunctions_UsesSelectedProfileWithoutKestrelOrDuplicateTelemetry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        AzureFunctionsTestHelpers.WriteLaunchSettings(path, new LaunchSettings
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["other"] = new() { CommandName = "Project", CommandLineArgs = "--port 7071" },
                ["selected"] = new()
                {
                    CommandName = "Project",
                    ApplicationUrl = "https://localhost:7999;http://localhost:7998",
                    CommandLineArgs = """--useHttps --cert "certificate with spaces.pfx" --password "test value" """,
                    EnvironmentVariables = new() { ["PROFILE_SETTING"] = "profile", ["OVERRIDE"] = "profile" }
                }
            }
        });
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["AppHost:DefaultLaunchProfileName"] = "selected";
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddAzureFunctions("functions", path)
            .WithEnvironment("OVERRIDE", "apphost")
            .WithEndpoint("https", endpoint => endpoint.TargetPort = 9876);
        using var app = builder.Build();

        var environment = await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, functions.Resource);

        Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("profile", environment["PROFILE_SETTING"]);
        Assert.Equal("apphost", environment["OVERRIDE"]);
        Assert.Equal("dotnet-isolated", environment["FUNCTIONS_WORKER_RUNTIME"]);
        Assert.Equal("OpenTelemetry", environment["AzureFunctionsJobHost__telemetryMode"]);
        Assert.Equal("in_memory", environment[KnownOtelConfigNames.DotnetExperimentalOtlpRetry]);
        var webSettings = environment.Keys.Where(key => key.StartsWith("Kestrel__", StringComparison.Ordinal) || key is "ASPNETCORE_URLS" or "ASPNETCORE_HTTP_PORTS" or "ASPNETCORE_HTTPS_PORTS").ToArray();
        Assert.Empty(webSettings);
        Assert.Equal(
            ["--port", "9876", "--useHttps", "--cert", "certificate with spaces.pfx", "--password", "test value"],
            await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAzureFunctions_SharesAndCleansUpHostStorageAcrossLegacyV2AndNode(bool customStorage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var legacy = builder.AddAzureFunctionsProject("legacy", path);
        var modern = builder.AddAzureFunctions("modern", path);
        var node = builder.AddAzureFunctionsApp("node", workspace.Path, AzureFunctionsLanguage.TypeScript);
        var implicitStorage = Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        Assert.Same(implicitStorage, legacy.Resource.HostStorage);
        Assert.Same(implicitStorage, modern.Resource.HostStorage);
        Assert.Same(implicitStorage, node.Resource.HostStorage);
        AzureStorageResource selectedStorage = implicitStorage;
        if (customStorage)
        {
            var storage = builder.AddAzureStorage("host").RunAsEmulator();
            legacy.WithHostStorage(storage);
            modern.WithHostStorage(storage);
            node.WithHostStorage(storage);
            selectedStorage = storage.Resource;
        }
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Same(selectedStorage, Assert.Single(builder.Resources.OfType<AzureStorageResource>()));
        foreach (var resource in new IResource[] { legacy.Resource, modern.Resource, node.Resource })
        {
            var relationship = Assert.Single(resource.Annotations.OfType<ResourceRelationshipAnnotation>(), annotation => annotation.Type == "Reference");
            Assert.Equal("Reference", relationship.Type);
            Assert.Same(selectedStorage, relationship.Resource);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAzureFunctions_MapsBindingsAndPreservesTypedBuildEnvironmentChain(bool polyglot)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");
        var queues = storage.AddQueues("queues");
        var serviceBus = builder.AddAzureServiceBus("bus");
        var eventHubs = builder.AddAzureEventHubs("events");
        var cosmos = builder.AddAzureCosmosDB("cosmos");
        var hub = builder.AddDurableTaskScheduler("scheduler").RunAsEmulator().AddTaskHub("hub");
        var functions = builder.AddAzureFunctions("functions", path);
        Assert.Same(functions, functions.WithBuildEnvironment("BUILD_FLAVOR", "build-only").WithHostStorage(storage));
        Assert.Same(functions, functions.WithBuildEnvironment(_ => { }).WithBuildEnvironment(_ => Task.CompletedTask));
        if (polyglot)
        {
            AzureFunctionsTestHelpers.InvokeWithReference(functions, blobs, "input");
            AzureFunctionsTestHelpers.InvokeWithReference(functions, queues);
            AzureFunctionsTestHelpers.InvokeWithReference(functions, serviceBus);
            AzureFunctionsTestHelpers.InvokeWithReference(functions, eventHubs);
            AzureFunctionsTestHelpers.InvokeWithReference(functions, cosmos);
            AzureFunctionsTestHelpers.InvokeWithReference(functions, hub);
        }
        else
        {
            Assert.Same(functions, functions.WithReference(blobs, "input").WithReference(queues)
                .WithReference(serviceBus).WithReference(eventHubs).WithReference(cosmos).WithReference(hub));
        }
        using var app = builder.Build();

        var environment = await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, functions.Resource);
        var bindingKeys = environment.Keys.Where(key =>
            key.StartsWith("Aspire__", StringComparison.Ordinal) ||
            key.StartsWith("input", StringComparison.Ordinal) ||
            key.StartsWith("queues", StringComparison.Ordinal) ||
            key.StartsWith("bus", StringComparison.Ordinal) ||
            key.StartsWith("events", StringComparison.Ordinal) ||
            key.StartsWith("cosmos", StringComparison.Ordinal) ||
            key is "DURABLE_TASK_SCHEDULER_CONNECTION_STRING" or "TASKHUB_NAME")
            .Order(StringComparer.Ordinal).ToArray();

        await Verify(bindingKeys).UseParameters(polyglot);
        Assert.Equal(hub.Resource.TaskHubName.ValueExpression, Assert.IsType<ReferenceExpression>(environment["TASKHUB_NAME"]).ValueExpression);
        Assert.Equal(hub.Resource.Parent.ConnectionStringExpression.ValueExpression, Assert.IsType<ReferenceExpression>(environment["DURABLE_TASK_SCHEDULER_CONNECTION_STRING"]).ValueExpression);
        var ordinarySettings = environment.Keys.Where(key => key.StartsWith("ConnectionStrings__", StringComparison.Ordinal) || key == "BUILD_FLAVOR").ToArray();
        Assert.Empty(ordinarySettings);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "named")]
    public void AddAzureFunctions_PolyglotReferencesRejectUnsupportedOptions(bool optional, string? name)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddAzureFunctions("functions", path);
        var blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");

        var exception = Assert.Throws<TargetInvocationException>(() =>
            AzureFunctionsTestHelpers.InvokeWithReference(functions, blobs, optional: optional, name: name));

        var error = exception.GetBaseException();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(optional
            ? "Optional references are not supported for Azure Functions resources."
            : "Named service references are not supported for Azure Functions resources.", error.Message);
    }

    [Fact]
    public async Task AddAzureFunctions_DoesNotChangeOrdinaryDotnetReferenceMapping()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");
        var project = builder.AddDotnetProject("ordinary", path);
        AzureFunctionsTestHelpers.InvokeWithReference(project, blobs, connectionName: "input");
        using var app = builder.Build();

        var environment = await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, project.Resource);
        Assert.Single(environment.Keys, key => key == "ConnectionStrings__input");
        var bindingSettings = environment.Keys.Where(key => key.StartsWith("input__", StringComparison.Ordinal)).ToArray();
        Assert.Empty(bindingSettings);
    }

    [Fact]
    public async Task AddAzureFunctions_MapsCapabilityTypedSourcesWithoutChangingOtherConnections()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        IResourceBuilder<IResourceWithConnectionString> blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");
        var ordinary = builder.AddConnectionString("ordinary");
        var functions = builder.AddAzureFunctions("functions", path).WithBuildEnvironment("BUILD_FLAVOR", "build-only");
        Assert.Same(functions, functions.WithReference(blobs, "input").WithReference(ordinary));
        Assert.Equal("Optional references are not supported for Azure Functions resources.",
            Assert.Throws<InvalidOperationException>(() => functions.WithReference(blobs, optional: true)).Message);
        using var app = builder.Build();

        var environment = await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, functions.Resource);

        Assert.Equal(
            ["Aspire__Azure__Storage__Blobs__input__ServiceUri", "ConnectionStrings__ordinary", "input__blobServiceUri", "input__queueServiceUri"],
            environment.Keys.Where(key => key.Contains("input", StringComparison.Ordinal) || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AddAzureFunctions_PublishesSdkProjectWithFunctionsEndpointsAndStorageRoles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, outputPath: workspace.Path);
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        builder.AddAzureContainerAppEnvironment("env");
        var functions = builder.AddAzureFunctions("functions", path);
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.True(functions.Resource.SupportsDotnetProgramPublishing());
        Assert.False(functions.Resource.GetProjectMetadata().SuppressBuild);
        Assert.Empty(functions.Resource.Annotations.OfType<RequiredCommandAnnotation>());
        Assert.Empty(functions.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        Assert.Equal([8080, 8080], functions.Resource.Annotations.OfType<EndpointAnnotation>().Select(endpoint => endpoint.TargetPort));
        var manifest = await ManifestUtils.GetManifest(functions.Resource, workspace.Path);
        var (_, bicep) = await GetManifestWithBicep(functions.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget, skipPreparer: true);
        var roles = Assert.Single(builder.Resources.OfType<AzureProvisioningResource>(), resource => resource.Name == "functions-roles-funcstorage634f8");
        var (roleManifest, roleBicep) = await GetManifestWithBicep(roles, skipPreparer: true);

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json")
            .AppendContentAsFile(bicep, "bicep")
            .AppendContentAsFile(roleManifest.ToString(), "json")
            .AppendContentAsFile(roleBicep, "bicep");
    }

    [Fact]
    public async Task AddAzureFunctions_CannotPublishAsContainerAppJob()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper);
        builder.AddAzureContainerAppEnvironment("env");
        var functions = builder.AddAzureFunctions("functions", path).PublishAsAzureContainerAppJob();
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            GetManifestWithBicep(functions.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget, skipPreparer: true));
    }
}
