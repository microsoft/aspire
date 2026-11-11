// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchCoordinatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "aspire", "run", false)]
    [InlineData(true, "", "run", false)]
    [InlineData(true, "ide", "run", false)]
    [InlineData(true, "aspire", "publish", false)]
    [InlineData(true, "aspire", "run", true)]
    public async Task WatchRegistrationRequiresExplicitRunModeAndCliOwnership(
        bool enabled, string owner, string operation, bool registered)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path, output,
            $"AppHost:Operation={operation}", $"AppHost:Run:WatchEnabled={enabled}",
            $"ASPIRE_DOTNET_WATCH_LAUNCH_OWNER={owner}");
        builder.AddDotnetProject("service", Path.Combine(workspace.Path, "Service.csproj"),
            options => options.ExcludeLaunchProfile = true);

        await using var app = builder.Build();

        Assert.Equal(registered, app.Services.GetService<DotnetWatchCoordinator>() is not null);
        Assert.Equal(Array.Empty<string>(), builder.Resources.OfType<ExecutableResource>()
            .Select(resource => resource.Name).Where(name => name.StartsWith("__dotnet-watch-", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task ARemovedRegistrationDoesNotInitializeAWatcher()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path, output,
            "AppHost:Run:WatchEnabled=true", "ASPIRE_DOTNET_WATCH_LAUNCH_OWNER=aspire");
        var resource = builder.AddDotnetProject("service", Path.Combine(workspace.Path, "Service.csproj"),
            options => options.ExcludeLaunchProfile = true);
        Assert.True(builder.Resources.Remove(resource.Resource));
        await using var app = builder.Build();
        var coordinator = app.Services.GetRequiredService<DotnetWatchCoordinator>();

        await coordinator.PrepareAsync(
            app.Services.GetRequiredService<DistributedApplicationModel>(), app.Services, TestContext.Current.CancellationToken);

        Assert.Empty(resource.Resource.Annotations.OfType<ResourceSnapshotProjectionAnnotation>());
        Assert.Equal(Array.Empty<string>(), builder.Resources.OfType<ExecutableResource>()
            .Select(resource => resource.Name).Where(name => name.StartsWith("__dotnet-watch-", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task MissingCliInvocationUsesOrdinaryExecutionWithoutEvaluatingTheMissingProject()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path, output,
            "AppHost:Run:WatchEnabled=true", "ASPIRE_DOTNET_WATCH_LAUNCH_OWNER=aspire");
        var resource = builder.AddDotnetProject("service", Path.Combine(workspace.Path, "Missing.csproj"),
            options => options.ExcludeLaunchProfile = true);
        await using var app = builder.Build();

        await app.Services.GetRequiredService<DotnetWatchCoordinator>().PrepareAsync(
            app.Services.GetRequiredService<DistributedApplicationModel>(), app.Services, TestContext.Current.CancellationToken);

        var projection = Assert.Single(resource.Resource.Annotations.OfType<ResourceSnapshotProjectionAnnotation>());
        var snapshot = projection.Projection("service", new() { ResourceType = "project", Properties = [] });
        Assert.Collection(snapshot.Properties,
            property => Assert.Equal(("watch.mode", (object?)"ordinary"), (property.Name, property.Value)),
            property => Assert.Equal(("watch.reason", (object?)"The exact invoking CLI specification is unavailable."), (property.Name, property.Value)));
        Assert.Equal("dotnet", resource.Resource.Command);
    }

    [Fact]
    public async Task CustomLifecycleOwnershipIsExcludedBeforeProjectEvaluation()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path, output,
            "AppHost:Run:WatchEnabled=true", "ASPIRE_DOTNET_WATCH_LAUNCH_OWNER=aspire",
            "ASPIRE_DOTNET_WATCH_CLI=/exact/aspire", "ASPIRE_DOTNET_WATCH_CLI_ARGUMENTS=[]");
        var resource = builder.AddDotnetProject("service", Path.Combine(workspace.Path, "Missing.csproj"),
            options => options.ExcludeLaunchProfile = true);
        resource.WithAnnotation(new ResourceLifecycleCommandOverrideAnnotation("stop", command => command));
        await using var app = builder.Build();

        await app.Services.GetRequiredService<DotnetWatchCoordinator>().PrepareAsync(
            app.Services.GetRequiredService<DistributedApplicationModel>(), app.Services, TestContext.Current.CancellationToken);

        var projection = Assert.Single(resource.Resource.Annotations.OfType<ResourceSnapshotProjectionAnnotation>());
        var snapshot = projection.Projection("service", new() { ResourceType = "project", Properties = [] });
        Assert.Collection(snapshot.Properties,
            property => Assert.Equal(("watch.mode", (object?)"ordinary"), (property.Name, property.Value)),
            property => Assert.Equal(("watch.reason", (object?)"Custom lifecycle ownership prevents the watcher from guaranteeing cleanup."),
                (property.Name, property.Value)));
        Assert.Equal("dotnet", resource.Resource.Command);
    }
}
