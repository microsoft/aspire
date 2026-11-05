// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREPROJECTS001

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Tests;

// SPIKE: coverage for `aspire run --watch` routing (docs/plans/project-v2-csharpprogram-watch.md).
public class DotnetWatchCoordinatorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void WithoutWatchNoServerIsAdded()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.DoesNotContain(builder.Resources, r => r.Name == DotnetWatchCoordinator.ServerResourceName);
    }

    [Fact]
    public async Task WatchRoutesLaunchThroughProxyAndWaitsForServer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire.dll"]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(projectPath)!);
        var otherPath = Path.Combine(workspace.Path, "Other", "Other.csproj");
        CreateProjectFile(otherPath);
        builder.AddDotnetProject("other", Path.GetDirectoryName(otherPath)!);

        // One shared server per AppHost.
        var server = Assert.Single(builder.Resources.OfType<DotnetWatchServerResource>());
        Assert.Equal(DotnetWatchCoordinator.ServerResourceName, server.Name);
        Assert.Contains(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == server);

        var args = await ArgumentEvaluator.GetArgumentListAsync(service.Resource);
        var proxyIndex = args.IndexOf(DotnetWatchCoordinator.ProxyCommandName);
        Assert.True(proxyIndex >= 1, string.Join(' ', args));
        Assert.Equal("/opt/aspire/aspire.dll", args[proxyIndex - 1]);
        Assert.Equal("--server", args[proxyIndex + 1]);
        Assert.Equal("--entrypoint", args[proxyIndex + 3]);
        Assert.Equal(projectPath, args[proxyIndex + 4]);
        Assert.Equal("--", args[proxyIndex + 5]);
        Assert.Equal("dotnet", service.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
    }

    [Fact]
    public void FileBasedAppsRunWithoutWatch()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProject(workspace.Path);
        var scriptPath = Path.Combine(workspace.Path, "ticker.cs");
        File.WriteAllText(scriptPath, "Console.WriteLine(1);");
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire.dll"]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(projectPath)!);
        var script = builder.AddDotnetProject("ticker", scriptPath);

        var server = Assert.Single(builder.Resources.OfType<DotnetWatchServerResource>());
        Assert.Contains(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == server);
        Assert.DoesNotContain(script.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == server);
    }

    [Fact]
    public async Task RemovedProjectsAreNotWatched()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire.dll"]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(projectPath)!);
        builder.Resources.Remove(service.Resource);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.DoesNotContain(model.Resources, r => r is DotnetWatchServerResource);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    [Fact]
    public void WatchWithoutProxyFallsBackToNormalRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(["AppHost:Run:WatchEnabled=true"]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.DoesNotContain(builder.Resources, r => r.Name == DotnetWatchCoordinator.ServerResourceName);
        Assert.DoesNotContain(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource is DotnetWatchServerResource);
    }

    [Fact]
    public void WatchUnderIdeDebugSessionFallsBackToNormalRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire", "DEBUG_SESSION_PORT=12345"]);

        builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.DoesNotContain(builder.Resources, r => r.Name == DotnetWatchCoordinator.ServerResourceName);
    }

    [Theory]
    [InlineData("10.0.100", null, DotnetWatchToolResolver.PinnedVersion)]
    [InlineData("10.0.403", null, DotnetWatchToolResolver.PinnedVersion)]
    [InlineData("11.0.100-rc.2.26504.105", null, "11.0.100-rc.2.26504.105")]
    [InlineData("11.0.100-rc.2.26504.105", "11.0.100-rc.3.26522.148", "11.0.100-rc.3.26522.148")]
    public void ToolVersionFollowsSdk(string sdkVersion, string? overrideVersion, string expected)
    {
        Assert.Equal(expected, DotnetWatchToolResolver.SelectToolVersion(sdkVersion, overrideVersion));
    }

    [Fact]
    public void PinnedToolVersionHasPinnedHash()
    {
        // Bumping PinnedVersion without its SHA-512 would silently fall back to the catalog lookup.
        Assert.True(DotnetWatchToolResolver.s_pinnedSha512.TryGetValue(DotnetWatchToolResolver.PinnedVersion, out var hash));
        Assert.Equal(128, hash.Length);
    }

    [Fact]
    public void ResourcesWithCustomLaunchToolAreDetected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire"]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);
        IResourceAnnotation[] watchLaunchTool = [service.Resource.Annotations.Last(a => a.GetType().Name == "LaunchToolArgsCallbackAnnotation")];

        Assert.False(DotnetWatchCoordinator.HasCustomLaunch(service.Resource, watchLaunchTool));

        // A launch tool registered after the watch proxy (for example, the Blazor gateway) owns the launch.
        service.WithLaunchToolArgs(ctx => ctx.Args.Add("gateway"));
        Assert.True(DotnetWatchCoordinator.HasCustomLaunch(service.Resource, watchLaunchTool));

        var otherPath = Path.Combine(workspace.Path, "Other", "Other.csproj");
        CreateProjectFile(otherPath);
        var other = builder.AddDotnetProject("other", Path.GetDirectoryName(otherPath)!);
        IResourceAnnotation[] otherLaunchTool = [other.Resource.Annotations.Last(a => a.GetType().Name == "LaunchToolArgsCallbackAnnotation")];
        other.WithAnnotation(new ProjectLaunchArgsOverrideAnnotation(["run"]));
        Assert.True(DotnetWatchCoordinator.HasCustomLaunch(other.Resource, otherLaunchTool));
    }

    private static string CreateProject(string root)
    {
        var path = Path.Combine(root, "Service", "Service.csproj");
        CreateProjectFile(path);
        return path;
    }

    private static void CreateProjectFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }
}
