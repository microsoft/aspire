// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001

using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ProjectRebuilderResourceTests
{
    [Fact]
    public async Task AddProjectRebuilderUsesConfiguredBuildConfiguration()
    {
        using var builder = CreateBuilder();
        var versionProvider = UseDotnetSdkVersion(builder, "11.0.100-rc.1");
        var project = builder.AddProject<Projects.ServiceA>("servicea", options => options.ExcludeLaunchProfile = true);
        var launchDefaults = Assert.Single(project.Resource.Annotations.OfType<ProjectLaunchDefaultsAnnotation>());
        launchDefaults.BuildConfiguration = "Release";

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        await using var app = builder.Build();
        await EventingTestHelpers.SubscribeEventingSubscribersAsync(
            app,
            TestContext.Current.CancellationToken);
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(rebuilder, app.Services),
            TestContext.Current.CancellationToken);
        // Repeated start events must not add another switch or repeat the SDK probe.
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(rebuilder, app.Services),
            TestContext.Current.CancellationToken);
        var args = await ArgumentEvaluator.GetArgumentListAsync(rebuilder, app.Services);

        Assert.Equal(
            [
                "build",
                project.Resource.GetProjectMetadata().ProjectPath,
                "-mt",
                "--configuration",
                "Release"
            ],
            args);
        Assert.Equal(1, versionProvider.CallCount);
        Assert.Equal(rebuilder.WorkingDirectory, Assert.Single(versionProvider.WorkingDirectories));
    }

    [Fact]
    public async Task AddProjectRebuilderOmitsConfigurationWhenAppHostConfigurationIsUnavailable()
    {
        using var builder = CreateBuilder();
        UseDotnetSdkVersion(builder, "11.0.100-preview.7.25380.108");
        var project = builder.AddProject<Projects.ServiceA>("servicea", options => options.ExcludeLaunchProfile = true);
        var launchDefaults = Assert.Single(project.Resource.Annotations.OfType<ProjectLaunchDefaultsAnnotation>());
        launchDefaults.BuildConfiguration = null;

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        await using var app = builder.Build();
        await EventingTestHelpers.SubscribeEventingSubscribersAsync(
            app,
            TestContext.Current.CancellationToken);
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(rebuilder, app.Services),
            TestContext.Current.CancellationToken);
        var args = await ArgumentEvaluator.GetArgumentListAsync(rebuilder, app.Services);

        Assert.Equal(
            [
                "build",
                project.Resource.GetProjectMetadata().ProjectPath
            ],
            args);
    }

    private static IDistributedApplicationTestingBuilder CreateBuilder()
    {
        return TestDistributedApplicationBuilder.Create();
    }

    private static TestDotnetSdkVersionProvider UseDotnetSdkVersion(
        IDistributedApplicationBuilder builder,
        string? version)
    {
        var provider = new TestDotnetSdkVersionProvider(version);
        builder.Services.AddSingleton<IDotnetSdkVersionProvider>(provider);
        return provider;
    }
}
