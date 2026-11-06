// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREPROJECTS001

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
            ["AppHost:Run:WatchEnabled=true"]);

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(projectPath)!);
        var otherPath = Path.Combine(workspace.Path, "Other", "Other.csproj");
        CreateProjectFile(otherPath);
        builder.AddDotnetProject("other", Path.GetDirectoryName(otherPath)!);

        // One shared server per AppHost.
        var server = Assert.Single(builder.Resources.OfType<DotnetWatchServerResource>());
        Assert.Equal(DotnetWatchCoordinator.ServerResourceName, server.Name);
        Assert.Contains(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == server);

        var args = await ArgumentEvaluator.GetArgumentListAsync(service.Resource);
        var proxyIndex = args.IndexOf(DotnetWatchHostProtocol.ProxyCommand);
        Assert.True(proxyIndex >= 2, string.Join(' ', args));

        // No CLI involved: the project's own `dotnet` runs the watch host shipped with Aspire.Hosting.Dotnet.
        Assert.Equal("exec", args[proxyIndex - 2]);
        Assert.Equal(DotnetWatchHostProtocol.AssemblyFileName, Path.GetFileName(args[proxyIndex - 1]));
        Assert.True(File.Exists(args[proxyIndex - 1]), args[proxyIndex - 1]);
        Assert.Equal("--server", args[proxyIndex + 1]);
        Assert.Equal("--entrypoint", args[proxyIndex + 3]);
        Assert.Equal(projectPath, args[proxyIndex + 4]);
        Assert.Equal("--launch-gate", args[proxyIndex + 5]);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "aspw-"), args[proxyIndex + 6]);
        Assert.Matches(@"launched0-\d+-[0-9A-F]{16}$", args[proxyIndex + 6]);
        Assert.Equal("--", args[proxyIndex + 7]);

        // Projects never share a launch gate.
        var otherArgs = await ArgumentEvaluator.GetArgumentListAsync(builder.Resources.Single(r => r.Name == "other"));
        Assert.NotEqual(args[proxyIndex + 6], otherArgs[otherArgs.IndexOf("--launch-gate") + 1]);
        Assert.Equal("dotnet", service.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
    }

    [Fact]
    public async Task RemovedProjectsAreNotWatched()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true"]);

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
    public void WatchHostIsFoundFromAppHostMetadata()
    {
        // The test assembly carries the same 'aspiredotnetwatchhostdir' metadata that AppHosts get from the package.
        var directory = DotnetWatchToolResolver.GetWatchHostDirectory(typeof(DotnetWatchCoordinatorTests).Assembly);
        Assert.NotNull(directory);
        Assert.True(File.Exists(DotnetWatchToolResolver.ResolveWatchHostPath(directory)));
    }

    [Fact]
    public void MissingWatchHostFailsWithFix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var missing = Assert.Throws<DistributedApplicationException>(() => DotnetWatchToolResolver.ResolveWatchHostPath(workspace.Path));
        Assert.Contains(DotnetWatchHostProtocol.AssemblyFileName, missing.Message);
        Assert.Contains("--watch", missing.Message);

        var noMetadata = Assert.Throws<DistributedApplicationException>(() => DotnetWatchToolResolver.ResolveWatchHostPath(null));
        Assert.Contains(DotnetWatchToolResolver.WatchHostDirectoryMetadataKey, noMetadata.Message);
    }

    [Fact]
    public void PackageLayoutLocatesWatchHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var lib = Directory.CreateDirectory(Path.Combine(workspace.Path, "lib", "net10.0")).FullName;
        var host = Directory.CreateDirectory(Path.Combine(workspace.Path, "tools", DotnetWatchToolResolver.WatchHostPackageFolder)).FullName;

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(host),
            Path.TrimEndingDirectorySeparator(DotnetWatchToolResolver.GetPackageToolsDirectory(Path.Combine(lib, "Aspire.Hosting.Dotnet.dll"), DotnetWatchToolResolver.WatchHostPackageFolder)!));
    }

    [Fact]
    public void WatchUnderIdeDebugSessionDefersToTheIde()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", "DEBUG_SESSION_PORT=12345"]);

        builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        Assert.DoesNotContain(builder.Resources, r => r.Name == DotnetWatchCoordinator.ServerResourceName);
    }

    [Theory]
    [InlineData("10.0.100", 10)]
    [InlineData("10.0.403", 10)]
    [InlineData("11.0.100-rc.2.26504.105", 11)]
    public void SdkMajorIsParsed(string sdkVersion, int expected)
    {
        Assert.Equal(expected, DotnetWatchToolResolver.GetSdkMajor(sdkVersion));
    }

    [Theory]
    [InlineData("10.0.100")]
    [InlineData("10.0.403")]
    public void ToolIsResolvedForNet10Sdks(string sdkVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolsDir = CreateToolsLayout(workspace.Path);

        var dll = DotnetWatchToolResolver.ResolveToolPath(null, toolsDir, sdkVersion, NullLogger.Instance);

        Assert.Equal(Path.Combine(toolsDir, DotnetWatchToolResolver.ToolDllName), dll);
    }

    [Theory]
    [InlineData("9.0.300")]
    [InlineData("11.0.100-rc.2.26504.105")]
    public void UnsupportedSdkMajorFailsExplicitly(string sdkVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolsDir = CreateToolsLayout(workspace.Path);

        var ex = Assert.Throws<DistributedApplicationException>(
            () => DotnetWatchToolResolver.ResolveToolPath(null, toolsDir, sdkVersion, NullLogger.Instance));

        Assert.Contains("supports .NET 10 SDKs only", ex.Message);
        Assert.Contains($"select SDK {sdkVersion}", ex.Message);
        Assert.Contains("global.json", ex.Message);
    }

    [Fact]
    public void MissingToolFailsWithFix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var ex = Assert.Throws<DistributedApplicationException>(
            () => DotnetWatchToolResolver.ResolveToolPath(null, Path.Combine(workspace.Path, "missing"), "10.0.100", NullLogger.Instance));

        Assert.Contains("hot reload tool is missing", ex.Message);
        Assert.Contains("rebuild the AppHost", ex.Message);
    }

    [Fact]
    public void AppHostWithoutToolMetadataFailsWithFix()
    {
        var ex = Assert.Throws<DistributedApplicationException>(
            () => DotnetWatchToolResolver.ResolveToolPath(null, DotnetWatchToolResolver.GetToolsDirectory(typeof(object).Assembly), "10.0.100", NullLogger.Instance));

        Assert.Contains(DotnetWatchToolResolver.ToolsDirectoryMetadataKey, ex.Message);
        Assert.Contains("Reference Aspire.Hosting.Dotnet", ex.Message);
    }

    [Fact]
    public void PackageToolsDirectoryIsFoundNextToTheLoadedAssembly()
    {
        // Prebuilt polyglot AppHost servers load lib/<tfm>/Aspire.Hosting.Dotnet.dll from the restored package.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var package = Path.Combine(workspace.Path, "aspire.hosting.dotnet", "13.4.0");
        var dll = Path.Combine(Directory.CreateDirectory(Path.Combine(package, "lib", "net10.0")).FullName, "Aspire.Hosting.Dotnet.dll");

        Assert.Null(DotnetWatchToolResolver.GetPackageToolsDirectory(dll));
        Assert.Null(DotnetWatchToolResolver.GetPackageToolsDirectory(null));

        Directory.CreateDirectory(Path.Combine(package, "tools", "dotnet-watch"));
        Assert.Equal(Path.Combine(package, "tools", "dotnet-watch") + Path.DirectorySeparatorChar, DotnetWatchToolResolver.GetPackageToolsDirectory(dll));
    }

    [Fact]
    public void ExplicitToolPathWins()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolsDir = CreateToolsLayout(workspace.Path);

        // The override bypasses the SDK check: it's how a matching tool is tried against another SDK.
        Assert.Equal(
            Path.Combine(toolsDir, DotnetWatchToolResolver.ToolDllName),
            DotnetWatchToolResolver.ResolveToolPath(toolsDir, toolsDirectory: null, "11.0.100", NullLogger.Instance));
        Assert.Throws<DistributedApplicationException>(
            () => DotnetWatchToolResolver.ResolveToolPath(Path.Combine(workspace.Path, "nope"), toolsDir, "10.0.100", NullLogger.Instance));
    }

    [Fact]
    public void ShippedToolTargetsNet10()
    {
        // The test assembly carries the same metadata AppHosts get, pointing at the layout Aspire.Hosting.Dotnet builds.
        var toolsDir = DotnetWatchToolResolver.GetToolsDirectory(typeof(DotnetWatchCoordinatorTests).Assembly);
        Assert.NotNull(toolsDir);

        var dll = DotnetWatchToolResolver.ResolveToolPath(null, toolsDir, "10.0.100", NullLogger.Instance);
        var runtimeConfig = Path.ChangeExtension(dll, ".runtimeconfig.json");
        Assert.True(File.Exists(runtimeConfig), runtimeConfig);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(runtimeConfig));
        Assert.Equal("net10.0", json.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString());
    }

    private static string CreateToolsLayout(string root)
    {
        var toolsDir = Directory.CreateDirectory(Path.Combine(root, "dotnet-watch")).FullName;
        File.WriteAllText(Path.Combine(toolsDir, DotnetWatchToolResolver.ToolDllName), "");
        return toolsDir;
    }

    [Fact]
    public void ResourcesWithCustomLaunchToolAreDetected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true"]);

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

    private static string[] WatchArgs(params string[] extra) =>
        ["AppHost:Run:WatchEnabled=true", .. extra];

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
    public void FileBasedAppGetsADedicatedServer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(WatchArgs());
        var scriptPath = Path.Combine(workspace.Path, "ticker.cs");
        File.WriteAllText(scriptPath, "Console.WriteLine(1);");

        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);
        var ticker = builder.AddDotnetProject("ticker", scriptPath);

        var servers = builder.Resources.OfType<DotnetWatchServerResource>().ToList();
        var shared = Assert.Single(servers, s => s.Name == DotnetWatchCoordinator.ServerResourceName);
        var dedicated = Assert.Single(servers, s => s.Name == "dotnet-watch-ticker");
        Assert.Contains(ticker.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == dedicated);
        Assert.DoesNotContain(ticker.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == shared);
        Assert.Contains(service.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == shared);
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

    [Theory]
    [InlineData("Switch: /src/Scripts/ticker.cs.csproj", "/src/Scripts/ticker.cs")]
    [InlineData(@"Switch: C:\src\My Scripts\ticker.cs.csproj", @"C:\src\My Scripts\ticker.cs")]
    [InlineData("Switch: /src/Api/Api.csproj", null)]
    [InlineData("Building /src/Scripts/ticker.cs.csproj ...", null)]
    public void VirtualProjectBuildFailureNamesTheEntryPoint(string line, string? expected)
    {
        Assert.Equal(expected is not null, DotnetWatchCoordinator.TryParseVirtualProjectBuildFailure(line, out var entryPoint));
        Assert.Equal(expected, entryPoint);
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
