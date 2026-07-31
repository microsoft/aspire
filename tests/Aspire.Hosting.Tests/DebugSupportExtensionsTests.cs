// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Debug support APIs are experimental.
#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class DebugSupportExtensionsTests
{
    [Fact]
    public async Task CreateLaunchConfigurationResolvesTheLaunchProfileForProjectResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http");

        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(await project.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug));

        Assert.Equal(ExecutableLaunchMode.Debug, launchConfiguration.Mode);
        Assert.Equal(GetProjectPath(project.Resource), launchConfiguration.ProjectPath);
        Assert.Equal("http", launchConfiguration.LaunchProfile);
        Assert.False(launchConfiguration.DisableLaunchProfile);
    }

    [Fact]
    public async Task CreateLaunchConfigurationDisablesTheLaunchProfileWhenTheResourceExcludesIt()
    {
        // The producer registered by AddProject never sets DisableLaunchProfile; it is derived from
        // ExcludeLaunchProfileAnnotation when the configuration is finalized. Passing a null launch profile
        // name applies that annotation.
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: null);

        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(await project.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug));

        Assert.True(launchConfiguration.DisableLaunchProfile);
        Assert.Equal(string.Empty, launchConfiguration.LaunchProfile);
    }

    [Fact]
    public void AddProjectRegistersProjectDebugSupportOnce()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http");

        var debugSupport = Assert.Single(project.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());

        Assert.Equal(KnownLaunchConfigurationTypes.Project, debugSupport.LaunchConfigurationType);
    }

    [Fact]
    public async Task CreateLaunchConfigurationReturnsTheProducerOutputForACustomProjectProducer()
    {
        // A resource can replace the project debug support that WithProjectDefaults registers. The producer
        // owns the whole configuration, so its output is returned (and sent) verbatim.
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http")
                             .WithDebugSupport(mode => new ProjectLaunchConfiguration
                             {
                                 Mode = mode,
                                 ProjectPath = "custom-path",
                                 LaunchProfile = "https"
                             }, KnownLaunchConfigurationTypes.Project);

        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(await project.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.NoDebug));

        Assert.Equal(ExecutableLaunchMode.NoDebug, launchConfiguration.Mode);
        Assert.Equal("custom-path", launchConfiguration.ProjectPath);
        Assert.Equal("https", launchConfiguration.LaunchProfile);
    }

    [Fact]
    public async Task CreateLaunchConfigurationReturnsTheProducerOutputForNonProjectLaunchTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport(mode => new TestGoLaunchConfiguration { Mode = mode, Package = "./cmd/api" }, "go");

        var launchConfiguration = Assert.IsType<TestGoLaunchConfiguration>(await executable.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.NoDebug));

        Assert.Equal("go", launchConfiguration.Type);
        Assert.Equal(ExecutableLaunchMode.NoDebug, launchConfiguration.Mode);
        Assert.Equal("./cmd/api", launchConfiguration.Package);
    }

    [Fact]
    public async Task CreateLaunchConfigurationAwaitsAnAsynchronousProducer()
    {
        // The producer is asynchronous so integrations can resolve the configuration from callbacks that are
        // themselves asynchronous (for example build-argument callbacks contributed by other annotations).
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport(async (mode, ct) =>
                                {
                                    await Task.Yield();
                                    return new TestGoLaunchConfiguration { Mode = mode, Package = "./cmd/api" };
                                }, "go");

        var launchConfiguration = Assert.IsType<TestGoLaunchConfiguration>(await executable.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug));

        Assert.Equal(ExecutableLaunchMode.Debug, launchConfiguration.Mode);
        Assert.Equal("./cmd/api", launchConfiguration.Package);
    }

    [Fact]
    public async Task CreateLaunchConfigurationPropagatesTheCancellationTokenToTheProducer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var cts = new CancellationTokenSource();
        CancellationToken observedToken = default;

        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport((mode, ct) =>
                                {
                                    observedToken = ct;
                                    return Task.FromResult(new TestGoLaunchConfiguration { Mode = mode });
                                }, "go");

        await executable.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug, cts.Token);

        Assert.Equal(cts.Token, observedToken);
    }

    [Fact]
    public async Task CreateLaunchConfigurationThrowsWhenTheResourceHasNoDebugSupport()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executable.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug));

        Assert.Contains("does not declare debug launch support", exception.Message);
    }

    [Fact]
    public async Task CreateLaunchConfigurationThrowsWhenTheResourceHasNoProjectMetadata()
    {
        // The producer resolves project metadata when it runs, so a resource that declares "project" debug
        // support without carrying metadata fails with a clear message rather than a sequence error.
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "dotnet", ".");
        executable.WithDebugSupport(mode => ProjectLaunchConfigurationFactory.Create(executable.Resource, mode), KnownLaunchConfigurationTypes.Project);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executable.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug));

        Assert.Contains("has no project metadata", exception.Message);
    }

    [Fact]
    public async Task CreateLaunchConfigurationThrowsWhenTheProducerReturnsNull()
    {
        // TLaunchConfiguration is unconstrained, so a producer for a reference type can legitimately
        // return null. That must fail with a message that names the resource rather than flowing into
        // the non-nullable Task<object> result or writing a null entry into the DCP annotation.
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport(_ => (TestGoLaunchConfiguration)null!, "go");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executable.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug));

        Assert.Contains("returned null", exception.Message);
        Assert.Contains("app", exception.Message);
        Assert.Contains("go", exception.Message);
    }

    [Fact]
    public void SupportsDebuggingReturnsFalseWhenTheResourceHasNoDebugSupport()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".");

        Assert.False(executable.Resource.SupportsDebugging(CreateConfiguration(), out var annotation));
        Assert.Null(annotation);
    }

    [Fact]
    public void SupportsDebuggingReturnsFalseWhenNoDebugSessionIsActive()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport(mode => new TestGoLaunchConfiguration { Mode = mode }, "go");

        Assert.False(executable.Resource.SupportsDebugging(CreateConfiguration(debugSessionPort: null), out _));
    }

    [Fact]
    public void SupportsDebuggingTreatsProjectAsSupportedWhenTheIdeSendsNoCapabilityList()
    {
        // Visual Studio does not send DEBUG_SESSION_INFO at all. It launches every project resource
        // natively, so "project" stays implicitly supported instead of falling back to a plain process.
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http");

        Assert.True(project.Resource.SupportsDebugging(CreateConfiguration(), out var annotation));
        Assert.Equal(KnownLaunchConfigurationTypes.Project, annotation.LaunchConfigurationType);
    }

    [Fact]
    public void SupportsDebuggingReturnsFalseForANonProjectTypeWhenTheIdeSendsNoCapabilityList()
    {
        // The implicit rule is deliberately limited to "project": an IDE that never advertised its
        // capabilities cannot be assumed to know how to launch a "go" resource.
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport(mode => new TestGoLaunchConfiguration { Mode = mode }, "go");

        Assert.False(executable.Resource.SupportsDebugging(CreateConfiguration(), out _));
    }

    [Theory]
    [InlineData(new[] { "go" }, true)]
    [InlineData(new[] { "project", "go" }, true)]
    [InlineData(new[] { "project" }, false)]
    [InlineData(new string[0], false)]
    public void SupportsDebuggingHonorsTheAdvertisedCapabilityList(string[] supportedLaunchConfigurations, bool expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport(mode => new TestGoLaunchConfiguration { Mode = mode }, "go");

        var configuration = CreateConfiguration(debugSessionInfo: CreateDebugSessionInfo(supportedLaunchConfigurations));

        Assert.Equal(expected, executable.Resource.SupportsDebugging(configuration, out _));
    }

    [Fact]
    public void SupportsDebuggingReturnsFalseForProjectWhenTheAdvertisedCapabilityListOmitsIt()
    {
        // "project" gets no special treatment once the IDE advertises a list. The VS Code extension only
        // includes it when the C# extension is installed; treating it as implicit would route the
        // resource to an IDE that cannot launch it and leave it stuck.
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http");

        var configuration = CreateConfiguration(debugSessionInfo: CreateDebugSessionInfo(["go", "python"]));

        Assert.False(project.Resource.SupportsDebugging(configuration, out _));
    }

    [Fact]
    public void SupportsDebuggingReturnsFalseWhenTheResourceForcesProcessExecution()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http")
                             .WithAnnotation(new ForceProcessExecutionAnnotation());

        Assert.False(project.Resource.SupportsDebugging(CreateConfiguration(), out _));
    }

    [Fact]
    public void SupportsDebuggingReturnsFalseForAPersistentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http")
                             .WithPersistentLifetime();

        Assert.False(project.Resource.SupportsDebugging(CreateConfiguration(), out _));
    }

    [Fact]
    public void SupportsDebuggingFallsBackToTheImplicitProjectRuleWhenDebugSessionInfoIsMalformed()
    {
        // Malformed DEBUG_SESSION_INFO is swallowed and treated as "no capability list", so the resource
        // keeps the Visual Studio behavior instead of failing the run outright.
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("proj", launchProfileName: "http");
        var executable = builder.AddExecutable("app", "go", ".")
                                .WithDebugSupport(mode => new TestGoLaunchConfiguration { Mode = mode }, "go");

        var configuration = CreateConfiguration(debugSessionInfo: "{ not json");

        Assert.True(project.Resource.SupportsDebugging(configuration, out _));
        Assert.False(executable.Resource.SupportsDebugging(configuration, out _));
    }

    private static IConfiguration CreateConfiguration(string? debugSessionPort = "12345", string? debugSessionInfo = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DcpExecutor.DebugSessionPortVar] = debugSessionPort,
                [KnownConfigNames.DebugSessionInfo] = debugSessionInfo
            })
            .Build();
    }

    // DEBUG_SESSION_INFO is the JSON the IDE sends, for example:
    //   {"protocols_supported":["2024-03-03"],"supported_launch_configurations":["project","go"]}
    // protocols_supported is required, so it must be present for the payload to deserialize at all.
    private static string CreateDebugSessionInfo(string[] supportedLaunchConfigurations)
    {
        return JsonSerializer.Serialize(new
        {
            protocols_supported = new[] { "2024-03-03" },
            supported_launch_configurations = supportedLaunchConfigurations
        });
    }

    private static string GetProjectPath(IResource resource) => resource.Annotations.OfType<IProjectMetadata>().Last().ProjectPath;

    private sealed class TestGoLaunchConfiguration() : ExecutableLaunchConfiguration("go")
    {
        [JsonPropertyName("package")]
        public string Package { get; set; } = string.Empty;
    }
}
