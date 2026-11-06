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
        var dll = CreatePackageLayout(workspace.Path);
        var host = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dll)))!, "tools", DotnetWatchToolResolver.WatchHostPackageFolder);

        Assert.Equal(
            host + Path.DirectorySeparatorChar,
            DotnetWatchToolResolver.GetPayloadDirectory(appHostAssembly: null, DotnetWatchToolResolver.WatchHostDirectoryMetadataKey, DotnetWatchToolResolver.WatchHostPackageFolder, dll));
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
    public void PackageLoadedAssemblyUsesOnlyThePackagePayload()
    {
        // Prebuilt polyglot AppHost servers load lib/<tfm>/Aspire.Hosting.Dotnet.dll from the restored package. That
        // package's tools/ folder is the only source, even if it's missing (the resolver then reports that path) and
        // even if the AppHost assembly carries metadata.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dll = CreatePackageLayout(workspace.Path);
        var expected = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dll)))!, "tools", "dotnet-watch") + Path.DirectorySeparatorChar;

        Assert.Equal(expected, DotnetWatchToolResolver.GetPayloadDirectory(null, DotnetWatchToolResolver.ToolsDirectoryMetadataKey, "dotnet-watch", dll));
        Assert.Equal(expected, DotnetWatchToolResolver.GetPayloadDirectory(
            typeof(DotnetWatchCoordinatorTests).Assembly, DotnetWatchToolResolver.ToolsDirectoryMetadataKey, "dotnet-watch", dll));

        var ex = Assert.Throws<DistributedApplicationException>(() => DotnetWatchToolResolver.ResolveToolPath(null, expected, "10.0.100", NullLogger.Instance));
        Assert.Contains(Path.Combine(expected, DotnetWatchToolResolver.ToolDllName), ex.Message);
    }

    [Fact]
    public void AssemblyOutsideAPackageUsesOnlyAppHostMetadata()
    {
        // A lib/<tfm>/ layout without the package's nuspec isn't a restored package, so nothing next to it is probed.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var lib = Directory.CreateDirectory(Path.Combine(workspace.Path, "lib", "net10.0")).FullName;
        Directory.CreateDirectory(Path.Combine(workspace.Path, "tools", "dotnet-watch"));
        var dll = Path.Combine(lib, "Aspire.Hosting.Dotnet.dll");

        Assert.Null(DotnetWatchToolResolver.GetPackageRoot(dll));
        Assert.Null(DotnetWatchToolResolver.GetPackageRoot(null));
        Assert.Null(DotnetWatchToolResolver.GetPayloadDirectory(typeof(object).Assembly, DotnetWatchToolResolver.ToolsDirectoryMetadataKey, "dotnet-watch", dll));

        var fromMetadata = DotnetWatchToolResolver.GetPayloadDirectory(
            typeof(DotnetWatchCoordinatorTests).Assembly, DotnetWatchToolResolver.ToolsDirectoryMetadataKey, "dotnet-watch", dll);
        Assert.NotNull(fromMetadata);
        Assert.DoesNotContain(workspace.Path, fromMetadata);
    }

    private static string CreatePackageLayout(string root)
    {
        var package = Path.Combine(root, "aspire.hosting.dotnet", "13.4.0");
        var lib = Directory.CreateDirectory(Path.Combine(package, "lib", "net10.0")).FullName;
        File.WriteAllText(Path.Combine(package, DotnetWatchToolResolver.PackageNuspecFileName), "<package />");
        return Path.Combine(lib, "Aspire.Hosting.Dotnet.dll");
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
    public void FileBasedAppBuildsWithStartupHooksAndMetadataUpdates()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scriptPath = Path.Combine(workspace.Path, "ticker.cs");
        File.WriteAllText(scriptPath, "Console.WriteLine(1);");

        var properties = DotnetWatchCoordinator.GetBuildProperties([new DotnetProjectMetadata(scriptPath, null, "ticker")]);

        Assert.Equal("true", properties["StartupHookSupport"]);
        Assert.Equal("true", properties["MetadataUpdaterSupport"]);
        Assert.False(properties.ContainsKey("Configuration"));
    }

    [Fact]
    public void ProjectBuildPropertiesFollowTheBuildConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProject(workspace.Path);

        Assert.Empty(DotnetWatchCoordinator.GetBuildProperties([new DotnetProjectMetadata(projectPath, null, "service")]));
        Assert.Empty(DotnetWatchCoordinator.GetBuildProperties([new DotnetProjectMetadata(projectPath, "Debug", "service")]));

        var release = DotnetWatchCoordinator.GetBuildProperties([new DotnetProjectMetadata(projectPath, "Release", "service")]);
        Assert.Equal("Release", release["Configuration"]);
        Assert.Equal("false", release["Optimize"]);
        Assert.False(release.ContainsKey("StartupHookSupport"));
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
