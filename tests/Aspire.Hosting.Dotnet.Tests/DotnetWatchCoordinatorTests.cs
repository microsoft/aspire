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
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire.dll", Protocol]);

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
    public async Task RemovedProjectsAreNotWatched()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire.dll", Protocol]);

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
    public async Task WatchWithoutProxyFailsTheRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(["AppHost:Run:WatchEnabled=true"]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.DoesNotContain(builder.Resources, r => r.Name == DotnetWatchCoordinator.ServerResourceName);
        Assert.DoesNotContain(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource is DotnetWatchServerResource);

        using var app = builder.Build();
        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));
        Assert.Contains("aspire run --watch", ex.Message);
        Assert.Contains(DotnetWatchCoordinator.ProxyPathConfigurationKey, ex.Message);
    }

    [Fact]
    public void WatchUnderIdeDebugSessionDefersToTheIde()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire", Protocol, "DEBUG_SESSION_PORT=12345"]);

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
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire", Protocol]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.False(DotnetWatchCoordinator.HasCustomLaunch(service.Resource));

        // A launch tool registered after the watch proxy (for example, the Blazor gateway) owns the launch.
        service.WithLaunchToolArgs(ctx => ctx.Args.Add("gateway"));
        Assert.True(DotnetWatchCoordinator.HasCustomLaunch(service.Resource));

        var otherPath = Path.Combine(workspace.Path, "Other", "Other.csproj");
        CreateProjectFile(otherPath);
        var other = builder.AddDotnetProject("other", Path.GetDirectoryName(otherPath)!);
        other.WithAnnotation(new ProjectLaunchArgsOverrideAnnotation(["run"]));
        Assert.True(DotnetWatchCoordinator.HasCustomLaunch(other.Resource));
    }

    private const string Protocol = $"{DotnetWatchCoordinator.ProxyProtocolConfigurationKey}=2";

    private static string[] WatchArgs(params string[] extra) =>
        ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire", Protocol, .. extra];

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public async Task OlderCliProtocolFailsTheRun(string? protocol)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string[] args = protocol is null
            ? ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire"]
            : ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire", $"{DotnetWatchCoordinator.ProxyProtocolConfigurationKey}={protocol}"];
        using var builder = TestDistributedApplicationBuilder.Create(args);

        builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.DoesNotContain(builder.Resources, r => r is DotnetWatchServerResource);

        using var app = builder.Build();
        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));
        Assert.Contains("too old", ex.Message);
    }

    [Fact]
    public void BuildEnvironmentMovesTheProjectToADedicatedServer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(WatchArgs());
        var otherPath = Path.Combine(workspace.Path, "Other", "Other.csproj");
        CreateProjectFile(otherPath);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);
        var other = builder.AddDotnetProject("other", Path.GetDirectoryName(otherPath)!);
        service.WithBuildEnvironment("MARKER", "1");

        var servers = builder.Resources.OfType<DotnetWatchServerResource>().ToList();
        Assert.Equal(2, servers.Count);
        var shared = Assert.Single(servers, s => s.Name == DotnetWatchCoordinator.ServerResourceName);
        var dedicated = Assert.Single(servers, s => s.Name == "dotnet-watch-service");

        Assert.Contains(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == dedicated);
        Assert.DoesNotContain(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == shared);
        Assert.Contains(other.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == shared);

        // A second call doesn't add yet another server.
        service.WithBuildEnvironment("OTHER", "2");
        Assert.Equal(2, builder.Resources.OfType<DotnetWatchServerResource>().Count());
    }

    [Fact]
    public async Task UnusedSharedServerIsRemovedWhenEveryProjectHasItsOwn()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(WatchArgs());

        builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!).WithBuildEnvironment("MARKER", "1");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(model.Resources.OfType<DotnetWatchServerResource>());
        Assert.Equal("dotnet-watch-service", server.Name);
    }

    [Fact]
    public void ProjectsUnderDifferentGlobalJsonGetSeparateServers()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(WatchArgs());
        var pinnedRoot = Path.Combine(workspace.Path, "pinned");
        var pinnedPath = Path.Combine(pinnedRoot, "Legacy", "Legacy.csproj");
        CreateProjectFile(pinnedPath);
        File.WriteAllText(Path.Combine(pinnedRoot, "global.json"), "{}");
        var samePath = Path.Combine(workspace.Path, "Other", "Other.csproj");
        CreateProjectFile(samePath);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);
        var same = builder.AddDotnetProject("other", Path.GetDirectoryName(samePath)!);
        var legacy = builder.AddDotnetProject("legacy", Path.GetDirectoryName(pinnedPath)!);

        var servers = builder.Resources.OfType<DotnetWatchServerResource>().ToList();
        Assert.Equal(2, servers.Count);
        var serviceServer = service.Resource.Annotations.OfType<WaitAnnotation>().Single(w => w.Resource is DotnetWatchServerResource).Resource;
        Assert.Same(serviceServer, same.Resource.Annotations.OfType<WaitAnnotation>().Single(w => w.Resource is DotnetWatchServerResource).Resource);
        var legacyServer = legacy.Resource.Annotations.OfType<WaitAnnotation>().Single(w => w.Resource is DotnetWatchServerResource).Resource;
        Assert.NotSame(serviceServer, legacyServer);
        Assert.Equal("dotnet-watch-legacy", legacyServer.Name);
    }

    [Fact]
    public void SdkScopeIsTheNearestGlobalJsonDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nested = Directory.CreateDirectory(Path.Combine(workspace.Path, "a", "b", "c")).FullName;
        File.WriteAllText(Path.Combine(workspace.Path, "a", "global.json"), "{}");

        Assert.Equal(Path.Combine(workspace.Path, "a"), DotnetWatchCoordinator.FindSdkScope(nested));
    }

    [Fact]
    public async Task ExplicitWorkingDirectoryIsPassedToTheProxy()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(WatchArgs());
        var workingDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "data")).FullName;

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!)
            .WithWorkingDirectory(workingDirectory);

        var args = await ArgumentEvaluator.GetArgumentListAsync(service.Resource);
        var index = args.IndexOf("--working-directory");
        Assert.True(index > 0, string.Join(' ', args));
        Assert.Equal(workingDirectory, args[index + 1]);
        Assert.True(index < args.IndexOf("--"));
    }

    [Fact]
    public async Task DefaultWorkingDirectoryIsNotPassedToTheProxy()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(WatchArgs());

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.DoesNotContain("--working-directory", await ArgumentEvaluator.GetArgumentListAsync(service.Resource));
    }

    [Theory]
    [InlineData(
        "dotnet watch ⌚ [api (net11.0)] Launched '/usr/share/dotnet/dotnet' with arguments 'run --no-build -e ASPIRE_DOTNET_WATCH_INSTANCE=api-hsgdtzpa -e DOTNET_WATCH=1': process id 4242",
        4242, "api-hsgdtzpa")]
    public void LaunchedLineIdentifiesTheReplica(string line, int expectedProcessId, string expectedInstance)
    {
        Assert.True(DotnetWatchCoordinator.TryParseLaunchedLine(line, out var processId, out var instance));
        Assert.Equal(expectedProcessId, processId);
        Assert.Equal(expectedInstance, instance);
        Assert.False(DotnetWatchCoordinator.TryParseLaunchedLine("Launched 'dotnet' with arguments 'run': process id 1", out _, out _));
    }

    [Theory]
    [InlineData("dotnet watch ⌚ [api (net11.0)] Process id 4242 ran for 1234ms and exited with exit code 3.", 4242, 3)]
    [InlineData("Process id 7 ran for 12 ms and exited with exit code -1.", 7, -1)]
    public void ExitedLineCarriesTheExitCode(string line, int expectedProcessId, int expectedExitCode)
    {
        Assert.True(DotnetWatchCoordinator.TryParseExitedLine(line, out var processId, out var exitCode));
        Assert.Equal(expectedProcessId, processId);
        Assert.Equal(expectedExitCode, exitCode);
    }

    [Fact]
    public void TempVariablesMatchWhatGetTempPathReads()
    {
        Assert.Equal(["TMP", "TEMP"], DotnetWatchCoordinator.GetTempVariables(windows: true));
        Assert.Equal(["TMPDIR"], DotnetWatchCoordinator.GetTempVariables(windows: false));
    }

    internal static string CreateProject(string root)
    {
        var path = Path.Combine(root, "Service", "Service.csproj");
        CreateProjectFile(path);
        return path;
    }

    internal static void CreateProjectFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }
}
