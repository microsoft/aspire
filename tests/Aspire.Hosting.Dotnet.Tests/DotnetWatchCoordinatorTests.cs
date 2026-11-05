// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

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
