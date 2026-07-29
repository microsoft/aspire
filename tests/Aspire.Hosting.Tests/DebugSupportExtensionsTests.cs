// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Debug support APIs are experimental.

using System.Text.Json.Serialization;
using Aspire.Hosting.Utils;

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
                             .WithDebugSupport((mode, ct) => Task.FromResult(new ProjectLaunchConfiguration
                             {
                                 Mode = mode,
                                 ProjectPath = "custom-path",
                                 LaunchProfile = "https"
                             }), KnownLaunchConfigurationTypes.Project);

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
                                .WithDebugSupport((mode, ct) => Task.FromResult(new TestGoLaunchConfiguration { Mode = mode, Package = "./cmd/api" }), "go");

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
        executable.WithDebugSupport((mode, ct) => Task.FromResult(ProjectLaunchConfigurationFactory.Create(executable.Resource, mode)), KnownLaunchConfigurationTypes.Project);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executable.Resource.CreateLaunchConfigurationAsync(ExecutableLaunchMode.Debug));

        Assert.Contains("has no project metadata", exception.Message);
    }

    private static string GetProjectPath(IResource resource) => resource.Annotations.OfType<IProjectMetadata>().Last().ProjectPath;

    private sealed class TestGoLaunchConfiguration() : ExecutableLaunchConfiguration("go")
    {
        [JsonPropertyName("package")]
        public string Package { get; set; } = string.Empty;
    }
}
