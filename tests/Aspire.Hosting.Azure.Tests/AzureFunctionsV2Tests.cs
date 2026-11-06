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
    public void AddDotnetAzureFunction_UsesManagedDotnetResourceAndResolvedPath(bool useDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(options => options.ProjectDirectory = workspace.Path, outputHelper);

        var functions = builder.AddDotnetAzureFunction("functions", useDirectory ? "Functions" : Path.GetRelativePath(workspace.Path, path));

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
    public void AddDotnetAzureFunction_RejectsFileBasedAppsBeforeRegistration(string path)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() => builder.AddDotnetAzureFunction("functions", path));

        Assert.Equal("Azure Functions requires an SDK-style project. File-based C# apps are not supported.", exception.Message);
        Assert.Empty(builder.Resources.OfType<AzureFunctionsResource>());
        Assert.Empty(builder.Resources.OfType<AzureStorageResource>());
    }

    [Fact]
    public void AddDotnetAzureFunction_UsesV2DirectoryAmbiguityValidation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "Other.fsproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() => builder.AddDotnetAzureFunction("functions", Path.GetDirectoryName(path)!));

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
    public void AddDotnetAzureFunction_NegotiatesFunctionsBuildOwnership(string? capability, bool ideLaunch, bool aspireBuild)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        if (capability is not null)
        {
            AzureFunctionsTestHelpers.ConfigureDebugSession(builder, capability.Length == 0 ? [] : [capability]);
        }

        var functions = builder.AddDotnetAzureFunction("functions", path);

        Assert.Equal(ideLaunch, functions.Resource.SupportsDebugging(builder.Configuration, out _));
        Assert.Equal(aspireBuild, functions.Resource.GetProjectMetadata().SuppressBuild);
        var annotation = functions.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().Last();
        Assert.Equal(
            capability == "azure-functions" ? "azure-functions" : "azure-functions-with-external-build.v1",
            annotation.LaunchConfigurationType);
    }

    [Fact]
    public async Task AddDotnetAzureFunction_CapabilitylessVisualStudioUsesCompatibleProjectPayload()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071 --useHttps");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, capabilities: null);
        var functions = builder.AddDotnetAzureFunction("functions", path);
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
    public async Task AddDotnetAzureFunction_LegacyLauncherRetainsLegacyPayloadAndOwnsBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071 --useHttps");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions", "project-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddDotnetAzureFunction_PrefersExternalBuildWhenBothFunctionsCapabilitiesAreAdvertised(bool externalFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        var sink = new TestSink();
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(sink)));
        string[] capabilities = ["azure-functions", "azure-functions-with-external-build.v1", "project", "project-with-external-build.v1"];
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, externalFirst ? capabilities.Reverse().ToArray() : capabilities);
        var functions = builder.AddDotnetAzureFunction("functions", path);
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        Assert.True(functions.Resource.SupportsDebugging(builder.Configuration, out var annotation));
        Assert.Same(Assert.Single(functions.Resource.Annotations.OfType<SupportsDebuggingAnnotation>()), annotation);
        Assert.Equal("azure-functions-with-external-build.v1", annotation.LaunchConfigurationType);
        Assert.True(functions.Resource.GetProjectMetadata().SuppressBuild);
        Assert.Single(functions.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        Assert.Single(functions.Resource.Annotations.OfType<WaitAnnotation>());
        var warnings = sink.Writes.Where(write => write.LoggerName == typeof(AzureFunctionsResource).FullName && write.LogLevel == LogLevel.Warning).ToArray();
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddDotnetAzureFunction_ProducesAuthoritativeLaunchMetadataOnlyAfterSuccessfulBuild(bool buildEnvironment)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithEnvironment("RUNTIME_ONLY", "runtime");
        var evaluations = 0;
        if (buildEnvironment)
        {
            functions.WithBuildEnvironment(context =>
            {
                evaluations++;
                context.EnvironmentVariables["BUILD_FLAVOR"] = "custom";
            });
        }
        using var app = builder.Build();

        var launch = functions.Resource.CreateLaunchConfigurationAsync("Debug", TestContext.Current.CancellationToken);
        Assert.False(launch.IsCompleted);
        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        var build = Assert.Single(functions.Resource.Annotations.OfType<WaitAnnotation>()).Resource;
        await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, Assert.IsAssignableFrom<IResourceWithEnvironment>(build));
        var runtimeEnvironment = await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, functions.Resource);
        Assert.Equal(["RUNTIME_ONLY"], runtimeEnvironment.Keys.Where(key => key is "RUNTIME_ONLY" or "BUILD_FLAVOR"));
        Assert.Equal(buildEnvironment ? 1 : 0, evaluations);
        await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with
        {
            State = KnownResourceStates.Finished,
            ExitCode = null
        });
        Assert.False(launch.IsCompleted);

        await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with { ExitCode = 0 });
        var configuration = builder.Configuration["AppHost:BuildConfiguration"];
        var payload = await launch.WaitAsync(TestContext.Current.CancellationToken);
        await Verify(JsonSerializer.Serialize(payload), "json").UseParameters(buildEnvironment)
            .AddScrubber(text => text
                .Replace(JsonEncodedText.Encode(path).ToString(), "Functions.csproj")
                .Replace(JsonEncodedText.Encode(functions.Resource.GetProjectMetadata().BuildWorkingDirectory!).ToString(), "Functions")
                .Replace($"\"build_configuration\":\"{configuration}\"", "\"build_configuration\":\"Configuration\""));
    }

    [Theory]
    [InlineData("guest", false, "Debug")]
    [InlineData("guest", false, "NoDebug")]
    [InlineData("guest", true, "Debug")]
    [InlineData("guest", true, "NoDebug")]
    [InlineData("override", false, "Debug")]
    [InlineData("override", false, "NoDebug")]
    [InlineData("override", true, "Debug")]
    [InlineData("override", true, "NoDebug")]
    [InlineData("csharp", false, "Debug")]
    [InlineData("csharp", false, "NoDebug")]
    [InlineData("csharp", true, "Debug")]
    [InlineData("csharp", true, "NoDebug")]
    public async Task AddDotnetAzureFunction_UsesAuthoritativeBuildConfigurationForIdeLaunch(string appHost, bool projectFallback, string mode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var csharpBuilder = TestDistributedApplicationBuilder.Create(outputHelper);
        // Exercise the real ATS dispatcher without widening Hosting's internals access.
        var builder = appHost == "guest"
            ? (IDistributedApplicationBuilder)typeof(DistributedApplication)
                .GetMethod("CreateBuilderForPolyglot", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Array.Empty<string>()])!
            : csharpBuilder;
        var assemblyConfiguration = builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        Assert.False(string.IsNullOrEmpty(assemblyConfiguration));
        if (appHost == "override")
        {
            builder.Configuration["AppHost:BuildConfiguration"] = "FunctionsTestConfiguration";
            Assert.NotEqual(assemblyConfiguration, builder.Configuration["AppHost:BuildConfiguration"]);
        }
        var expectedConfiguration = builder.Configuration["AppHost:BuildConfiguration"];
        if (appHost == "guest")
        {
            Assert.Null(expectedConfiguration);
            builder.Configuration["Aspire:Store:Path"] = workspace.Path;
            // The test runner's entry assembly is the guest server stand-in, not an
            // AppHost with runtime paths. Reuse the real test AppHost's runtime assets.
            var runtimeMetadata = csharpBuilder.AppHostAssembly!.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.OrdinalIgnoreCase);
            builder.Configuration["DcpPublisher:CliPath"] = runtimeMetadata["DcpCliPath"];
            builder.Configuration["DcpPublisher:DashboardPath"] = runtimeMetadata["AspireDashboardPath"];
        }
        else if (appHost == "csharp")
        {
            Assert.Equal(assemblyConfiguration, expectedConfiguration);
        }
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder,
            projectFallback ? null : ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path);
        using var app = builder.Build();

        if (!projectFallback)
        {
            await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
            var build = Assert.Single(functions.Resource.Annotations.OfType<WaitAnnotation>()).Resource;
            await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, Assert.IsAssignableFrom<IResourceWithEnvironment>(build));
            var arguments = await ArgumentEvaluator.GetArgumentListAsync(Assert.IsAssignableFrom<IResourceWithArgs>(build), app.Services);
            Assert.Equal("build", arguments[0]);
            Assert.True(Path.IsPathFullyQualified(arguments[1]));
            Assert.Equal(
                expectedConfiguration is null ? [] : new[] { "--configuration", expectedConfiguration },
                arguments.Skip(2));
            await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with
            {
                State = KnownResourceStates.Finished,
                ExitCode = 0
            });
        }

        var launch = await functions.Resource.CreateLaunchConfigurationAsync(mode, TestContext.Current.CancellationToken);
        if (projectFallback)
        {
            var payload = Assert.IsType<ProjectLaunchConfiguration>(launch);
            Assert.Equal(expectedConfiguration, payload.BuildConfiguration);
            Assert.Equal(mode, payload.Mode);
            Assert.False(payload.SuppressBuild);
        }
        else
        {
            var payload = JsonSerializer.SerializeToElement(launch);
            Assert.Equal("azure-functions-with-external-build.v1", payload.GetProperty("type").GetString());
            Assert.Equal(mode, payload.GetProperty("mode").GetString());
            Assert.True(payload.GetProperty("suppress_build").GetBoolean());
            Assert.Equal(expectedConfiguration is not null, payload.TryGetProperty("build_configuration", out var configuration));
            if (expectedConfiguration is not null)
            {
                Assert.Equal(expectedConfiguration, configuration.GetString());
            }
        }
    }

    [Theory]
    [InlineData(nameof(KnownResourceStates.Finished), 1)]
    [InlineData(nameof(KnownResourceStates.FailedToStart), null)]
    public async Task AddDotnetAzureFunction_LaunchMetadataPropagatesBuildFailureAndAllowsRetry(string state, int? exitCode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        var build = Assert.Single(functions.Resource.Annotations.OfType<WaitAnnotation>()).Resource;

        var launch = functions.Resource.CreateLaunchConfigurationAsync("Debug", TestContext.Current.CancellationToken);
        Assert.False(launch.IsCompleted);
        await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with { State = state, ExitCode = exitCode });
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => launch);
        Assert.Contains("coordinated .NET build failed", exception.Message);

        await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with { State = KnownResourceStates.Running, ExitCode = null });
        var retry = functions.Resource.CreateLaunchConfigurationAsync("Debug", TestContext.Current.CancellationToken);
        Assert.False(retry.IsCompleted);
        await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });
        var payload = JsonSerializer.SerializeToElement(await retry.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("azure-functions-with-external-build.v1", payload.GetProperty("type").GetString());
        Assert.True(payload.GetProperty("suppress_build").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddDotnetAzureFunction_LaunchCancellationDoesNotCancelOtherConsumers(bool materializeFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path);
        using var app = builder.Build();
        if (materializeFirst)
        {
            await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        }
        using var cancellation = new CancellationTokenSource();
        var canceled = functions.Resource.CreateLaunchConfigurationAsync("Debug", cancellation.Token);
        var other = functions.Resource.CreateLaunchConfigurationAsync("NoDebug", TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(other.IsCompleted);
        if (!materializeFirst)
        {
            await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        }
        var build = Assert.Single(functions.Resource.Annotations.OfType<WaitAnnotation>()).Resource;
        await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });
        var payload = JsonSerializer.SerializeToElement(await other.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("NoDebug", payload.GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddDotnetAzureFunction_RejectsIncompleteExternalBuildMetadata(bool completionIsPresent)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddDotnetAzureFunction("functions", path);
        functions.Resource.Annotations.Remove(Assert.Single(functions.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>()));
        if (completionIsPresent)
        {
            functions.WithAnnotation(new DotnetProgramBuildCompletionAnnotation((_, _) => Task.CompletedTask));
        }
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            functions.Resource.CreateLaunchConfigurationAsync("Debug", TestContext.Current.CancellationToken));

        Assert.Equal(
            "Azure Functions resource 'functions' requires completed external-build metadata for 'azure-functions-with-external-build.v1'.",
            exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddDotnetAzureFunction_MissingPathUsesProcessFallbackWithoutBuildWaits(bool includeSibling)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder,
            ["azure-functions", "azure-functions-with-external-build.v1", "project", "project-with-external-build.v1"]);
        if (includeSibling)
        {
            builder.AddDotnetProject("sibling", AzureFunctionsTestHelpers.CreateProject(workspace.Path, name: "Sibling"));
        }
        var missingPath = AzureFunctionsTestHelpers.CreateProject(workspace.Path, name: "Missing");
        var functions = builder.AddDotnetAzureFunction("functions", missingPath)
            .WithAnnotation(new ExcludeLaunchProfileAnnotation())
            .WithEndpoint("http", endpoint => endpoint.TargetPort = 9876);
        var selectedDebugSupport = Assert.Single(functions.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        using var app = builder.Build();
        File.Delete(missingPath);

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        Assert.False(functions.Resource.GetProjectMetadata().SuppressBuild);
        Assert.False(functions.Resource.SupportsDebugging(builder.Configuration, out _));
        Assert.Empty(functions.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        Assert.Empty(functions.Resource.Annotations.OfType<WaitAnnotation>());
        var arguments = new List<string> { "run", "--project", missingPath };
        if (builder.Configuration["AppHost:BuildConfiguration"] is { Length: > 0 } configuration)
        {
            arguments.AddRange(["--configuration", configuration]);
        }
        arguments.AddRange(["--no-launch-profile", "--port", "9876"]);
        Assert.Equal(arguments, await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
        Assert.Equal(includeSibling ? 1 : 0,
            builder.Resources.Count(resource => resource.Name == "__dotnet-project-build"));

        // A stale producer must reject metadata after ownership was revoked, rather than emit a success-shaped payload.
        functions.Resource.Annotations.Add(selectedDebugSupport);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            functions.Resource.CreateLaunchConfigurationAsync("Debug", TestContext.Current.CancellationToken));
        Assert.Contains("requires completed external-build metadata", exception.Message);
    }

    [Fact]
    public void RuntimeFixtureCopiesApprovedNuGetConfigurationFromTestOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var path = AzureFunctionsTestHelpers.CreateRuntimeProject(workspace.Path);

        Assert.True(File.Exists(path));
        Assert.Equal(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Functions", "NuGet.config")),
            File.ReadAllText(Path.Combine(workspace.Path, "NuGet.config")));
    }

    [Theory]
    [InlineData("azure-functions")]
    [InlineData("azure-functions-with-external-build.v1")]
    [InlineData(null)]
    public void AddDotnetAzureFunction_ForcedProcessAndPersistentLifetimeDoNotDelegateToIde(string? capability)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, capability is null ? null : [capability]);
        var forced = builder.AddDotnetAzureFunction("forced", path).WithTerminal();
        var persistent = builder.AddDotnetAzureFunction("persistent", path).WithPersistentLifetime();

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
    public async Task AddDotnetAzureFunction_WarnsOnceAtStartupOnlyForActuallySelectedIdeFallback(
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
        var functions = builder.AddDotnetAzureFunction("functions", path).WithBuildEnvironment("BUILD_FLAVOR", "never-log-this-value");
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
    [InlineData("-p 7071", "http", 7071)]
    [InlineData("--port=7071", "http", 7071)]
    [InlineData("-p:7071", "http", 7071)]
    [InlineData("--port 7071 --useHttps", "https", 7071)]
    [InlineData("--port 7071 --useHttps true", "https", 7071)]
    [InlineData("--port 7071 --useHttps=false", "http", 7071)]
    [InlineData("--port 7072 --port 7071", "http", 7072)]
    [InlineData("-p 7072 --port=7071", "http", 7072)]
    [InlineData("--useHttps false --useHttps", "http", null)]
    [InlineData("--port invalid", "http", null)]
    [InlineData("--port", "http", null)]
    [InlineData("--useHttps", "https", null)]
    [InlineData(null, "http", null)]
    public async Task AddDotnetAzureFunction_PreservesFunctionsEndpointsAndProfileArguments(string? arguments, string scheme, int? port)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, arguments);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path);
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

    [Theory]
    [InlineData("--port 7071 --label profile", "http", 7071, "--label profile")]
    [InlineData("-p=7071 --label profile", "http", 7071, "--label profile")]
    [InlineData("--port:7071 --label profile", "http", 7071, "--label profile")]
    [InlineData("-p 7072 --port=7071 --label profile", "http", 7072, "--label profile")]
    [InlineData("--port 7071 --useHttps --cert certificate.pfx --password secret", "https", 7071, "--useHttps --cert certificate.pfx --password secret")]
    public async Task AddDotnetAzureFunction_ReplicasReplaceProfilePortsAndPreserveOptions(
        string arguments, string scheme, int publicPort, string remainingArguments)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, arguments);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(1).WithReplicas(2);
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(scheme, endpoint.Name);
        Assert.Equal(scheme, endpoint.UriScheme);
        Assert.Equal(publicPort, endpoint.Port);
        Assert.Null(endpoint.TargetPort);
        Assert.True(endpoint.IsProxied);
        Assert.Equal(publicPort, AzureFunctionsHostOptions.GetReplicatedProfilePort(functions.Resource));
        Assert.True(functions.Resource.HasAnnotationOfType<ExcludeLaunchProfileAnnotation>());
        endpoint.TargetPort = 9876;
        var expectedArguments = new List<string> { "--port", "9876" };
        expectedArguments.AddRange(CommandLineArgsParser.Parse(remainingArguments));
        Assert.Equal(expectedArguments, await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddDotnetAzureFunction_ReplicasPreserveIdeFallbackProfileSettings(bool projectFallback)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        AzureFunctionsTestHelpers.WriteLaunchSettings(path, new LaunchSettings
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["Functions"] = new()
                {
                    CommandName = "Project",
                    CommandLineArgs = "--port 7071 --useHttps --cert certificate.pfx",
                    EnvironmentVariables = new() { ["PROFILE_SETTING"] = "profile" }
                }
            }
        });
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, projectFallback ? null : ["azure-functions"]);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(2);
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.True(endpoint.IsProxied);
        Assert.Null(endpoint.TargetPort);
        endpoint.TargetPort = 9876;
        Assert.Equal(
            ["--port", "9876", "--useHttps", "--cert", "certificate.pfx"],
            await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
        var environment = await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, functions.Resource);
        Assert.Equal("profile", environment["PROFILE_SETTING"]);
        Assert.Equal("Functions", environment["DOTNET_LAUNCH_PROFILE"]);
        Assert.False(functions.Resource.GetProjectMetadata().SuppressBuild);
        if (projectFallback)
        {
            var launch = Assert.IsType<ProjectLaunchConfiguration>(await functions.Resource.CreateLaunchConfigurationAsync("NoDebug"));
            Assert.True(launch.DisableLaunchProfile);
            Assert.Equal(string.Empty, launch.LaunchProfile);
        }
    }

    [Fact]
    public async Task AddDotnetAzureFunction_FinalSingleReplicaRetainsDirectProfilePort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071 --useHttps");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(2).WithReplicas(1);
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.False(endpoint.IsProxied);
        Assert.Equal(7071, endpoint.Port);
        Assert.Equal(7071, endpoint.TargetPort);
        Assert.Null(AzureFunctionsHostOptions.GetReplicatedProfilePort(functions.Resource));
        Assert.False(functions.Resource.HasAnnotationOfType<ExcludeLaunchProfileAnnotation>());
        Assert.Equal(
            ["--port", "7071", "--useHttps"],
            await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
    }

    [Fact]
    public async Task AddDotnetAzureFunction_ReplicasUseRenamedFunctionsEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071 --useHttps");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(2)
            .WithEndpoint("https", endpoint => endpoint.Name = "functions");
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("functions", endpoint.Name);
        Assert.Equal(7071, endpoint.Port);
        Assert.True(endpoint.IsProxied);
        Assert.Null(endpoint.TargetPort);
        endpoint.TargetPort = 9876;
        Assert.Equal(
            ["--port", "9876", "--useHttps"],
            await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services));
        Assert.Equal(
            ["start"],
            AzureFunctionsHostOptions.NormalizeSdkRunArguments(functions.Resource, "func", ["start", "--port", "7071", "--useHttps"]));
    }

    [Fact]
    public async Task AddDotnetAzureFunction_ReplicasRejectRemovedFunctionsEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(2);
        functions.Resource.Annotations.Remove(Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>()));
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken));

        Assert.Equal(
            "Azure Functions resource 'functions' cannot replicate a fixed-port launch profile after its Functions endpoint has been removed.",
            exception.Message);
    }

    [Fact]
    public async Task AddDotnetAzureFunction_ReplicasDoNotRewriteExplicitTargets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(2)
            .WithEndpoint("http", endpoint => endpoint.TargetPort = 9090);
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.False(endpoint.IsProxied);
        Assert.Equal(7071, endpoint.Port);
        Assert.Equal(9090, endpoint.TargetPort);
        Assert.Null(AzureFunctionsHostOptions.GetReplicatedProfilePort(functions.Resource));
        Assert.False(functions.Resource.HasAnnotationOfType<ExcludeLaunchProfileAnnotation>());
    }

    [Fact]
    public async Task AddDotnetAzureFunction_ReplicasReconcileSdkOptionsAgainstDeclaredProfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(
            workspace.Path, "--port 7071 --useHttps --cert certificate.pfx --password secret");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(2);
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["start", "--label", "sdk"],
            AzureFunctionsHostOptions.NormalizeSdkRunArguments(
                functions.Resource, "func",
                ["start", "-p=7071", "--useHttps", "--cert", "certificate.pfx", "--password", "secret", "--label", "sdk"]));
        var exception = Assert.Throws<DistributedApplicationException>(() =>
            AzureFunctionsHostOptions.NormalizeSdkRunArguments(functions.Resource, "func", ["start", "--port", "7072"]));
        Assert.Equal(
            "Azure Functions SDK RunArguments define endpoint settings that do not match the Aspire endpoint. " +
            "Configure port, HTTPS, certificate, and password options in the launch profile or AppHost instead.",
            exception.Message);
    }

    [Fact]
    public async Task AddDotnetAzureFunction_ReplicatedLaunchMetadataRetainsProfilePort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path, "--port 7071");
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        AzureFunctionsTestHelpers.ConfigureDebugSession(builder, ["azure-functions-with-external-build.v1"]);
        var functions = builder.AddDotnetAzureFunction("functions", path).WithReplicas(2);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        var build = Assert.Single(functions.Resource.Annotations.OfType<WaitAnnotation>()).Resource;
        await AzureFunctionsTestHelpers.GetEnvironmentAsync(builder, Assert.IsAssignableFrom<IResourceWithEnvironment>(build));
        await app.ResourceNotifications.PublishUpdateAsync(build, snapshot => snapshot with
        {
            State = KnownResourceStates.Finished,
            ExitCode = 0
        });

        var payload = await functions.Resource.CreateLaunchConfigurationAsync("NoDebug", TestContext.Current.CancellationToken);

        var configuration = builder.Configuration["AppHost:BuildConfiguration"];
        await Verify(JsonSerializer.Serialize(payload), "json")
            .AddScrubber(text => text
                .Replace(JsonEncodedText.Encode(path).ToString(), "Functions.csproj")
                .Replace(JsonEncodedText.Encode(functions.Resource.GetProjectMetadata().BuildWorkingDirectory!).ToString(), "Functions")
                .Replace($"\"build_configuration\":\"{configuration}\"", "\"build_configuration\":\"Configuration\""));
    }

    [Fact]
    public void AddDotnetAzureFunction_NormalizesEquivalentSdkEndpointArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(
            workspace.Path,
            """--port 7071 --useHttps --cert "certificate.pfx" --password "secret" """);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddDotnetAzureFunction("functions", path);

        var normalized = AzureFunctionsHostOptions.NormalizeSdkRunArguments(
            functions.Resource,
            "cmd",
            ["/C", @"C:\Program Files\Core Tools\func.cmd", "host", "start",
             "-p=7071", "--port", "9090", "--useHttps=true",
             "--cert=certificate.pfx", "--password", "secret", "--label", "sdk"]);

        Assert.Equal(
            ["/C", @"C:\Program Files\Core Tools\func.cmd", "host", "start", "--label", "sdk"],
            normalized);
    }

    [Theory]
    [InlineData("start --port 7072 --label private-sdk-value")]
    [InlineData("start --useHttps false --label private-sdk-value")]
    [InlineData("start --cert private-sdk-value.pfx")]
    [InlineData("start --password private-sdk-value")]
    public void AddDotnetAzureFunction_RejectsConflictingSdkEndpointArgumentsWithoutDisclosingValues(string sdkArguments)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(
            workspace.Path,
            """--port 7071 --useHttps --cert "certificate.pfx" --password "secret" """);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddDotnetAzureFunction("functions", path);

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            AzureFunctionsHostOptions.NormalizeSdkRunArguments(
                functions.Resource,
                "func",
                CommandLineArgsParser.Parse(sdkArguments)));

        Assert.Equal(
            "Azure Functions SDK RunArguments define endpoint settings that do not match the Aspire endpoint. " +
            "Configure port, HTTPS, certificate, and password options in the launch profile or AppHost instead.",
            exception.Message);
    }

    [Fact]
    public async Task AddDotnetAzureFunction_UsesSelectedProfileWithoutKestrelOrDuplicateTelemetry()
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
        var functions = builder.AddDotnetAzureFunction("functions", path)
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
    public async Task AddDotnetAzureFunction_SharesAndCleansUpHostStorageAcrossLegacyV2AndNode(bool customStorage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var legacy = builder.AddAzureFunctionsProject("legacy", path);
        var modern = builder.AddDotnetAzureFunction("modern", path);
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
    public async Task AddDotnetAzureFunction_MapsBindingsAndPreservesTypedBuildEnvironmentChain(bool polyglot)
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
        var functions = builder.AddDotnetAzureFunction("functions", path);
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
    public void AddDotnetAzureFunction_PolyglotReferencesRejectUnsupportedOptions(bool optional, string? name)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var functions = builder.AddDotnetAzureFunction("functions", path);
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
    public async Task AddDotnetAzureFunction_DoesNotChangeOrdinaryDotnetReferenceMapping()
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
    public async Task AddDotnetAzureFunction_MapsCapabilityTypedSourcesWithoutChangingOtherConnections()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        IResourceBuilder<IResourceWithConnectionString> blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");
        var ordinary = builder.AddConnectionString("ordinary");
        var functions = builder.AddDotnetAzureFunction("functions", path).WithBuildEnvironment("BUILD_FLAVOR", "build-only");
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
    public async Task AddDotnetAzureFunction_PublishesSdkProjectWithFunctionsEndpointsAndStorageRoles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, outputPath: workspace.Path);
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        builder.AddAzureContainerAppEnvironment("env");
        var functions = builder.AddDotnetAzureFunction("functions", path);
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

    [Theory]
    [InlineData("default")]
    [InlineData("include")]
    [InlineData("exclude")]
    [InlineData("composed")]
    [InlineData("polyglot-empty")]
    [InlineData("polyglot-http")]
    [InlineData("polyglot-https")]
    [InlineData("port-environment")]
    [InlineData("explicit")]
    [InlineData("explicit-first")]
    public async Task AddDotnetAzureFunction_PublishingHonorsEndpointEnvironmentPolicy(string filter)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper);
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        var functions = builder.AddDotnetAzureFunction("functions", path).WithEnvironment("USER_SETTING", "explicit");
        switch (filter)
        {
            case "include":
                functions.WithEndpointsInEnvironment(endpoint => endpoint.Name == "http");
                break;
            case "exclude":
                functions.WithEndpointsInEnvironment(_ => false);
                break;
            case "composed":
                functions.WithEndpointsInEnvironment(endpoint => endpoint.Name == "http")
                    .WithEndpointsInEnvironment(endpoint => endpoint.UriScheme == "https");
                break;
            case "polyglot-empty":
            case "polyglot-http":
            case "polyglot-https":
                var method = typeof(AzureFunctionsProjectResourceExtensions).GetMethod(
                    "WithFunctionsEndpointsInEnvironmentForPolyglot", BindingFlags.Static | BindingFlags.NonPublic)!;
                string[] names = filter switch
                {
                    "polyglot-http" => ["HTTP"],
                    "polyglot-https" => ["https"],
                    _ => []
                };
                Assert.Same(functions, method.Invoke(null, [functions, names]));
                break;
            case "port-environment":
                var httpEndpoint = Assert.Single(functions.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "http");
                functions.Resource.Annotations.Remove(httpEndpoint);
                functions.WithHttpEndpoint(targetPort: 8080, env: "FUNCTIONS_PORT");
                break;
            case "explicit":
                functions.WithEndpointsInEnvironment(_ => false).WithEnvironment("ASPNETCORE_URLS", "http://+:9090");
                break;
            case "explicit-first":
                functions.Resource.Annotations.Insert(0,
                    new EnvironmentCallbackAnnotation(context => context.EnvironmentVariables["ASPNETCORE_URLS"] = "http://+:9090"));
                break;
        }
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        var manifest = await ManifestUtils.GetManifest(functions.Resource, workspace.Path);
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json").UseParameters(filter);
    }

    [Fact]
    public async Task LegacyFunctionsPublishingRetainsUnconditionalListeningAddress()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper);
        builder.Configuration["AppHost:ProjectNameSha256"] = "634f8";
        var functions = builder.AddAzureFunctionsProject("functions", path).WithEndpointsInEnvironment(_ => false);
        functions.Resource.Annotations.Insert(0,
            new EnvironmentCallbackAnnotation(context => context.EnvironmentVariables["ASPNETCORE_URLS"] = "http://+:9090"));
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);

        var manifest = await ManifestUtils.GetManifest(functions.Resource, workspace.Path);
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task AddDotnetAzureFunction_CannotPublishAsContainerAppJob()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = AzureFunctionsTestHelpers.CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper);
        builder.AddAzureContainerAppEnvironment("env");
        var functions = builder.AddDotnetAzureFunction("functions", path).PublishAsAzureContainerAppJob();
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            GetManifestWithBicep(functions.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget, skipPreparer: true));
    }
}
