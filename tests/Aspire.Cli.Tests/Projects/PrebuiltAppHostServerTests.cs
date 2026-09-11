// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class PrebuiltAppHostServerTests(ITestOutputHelper outputHelper)
{
    private const string NuGetOrgSource = "https://api.nuget.org/v3/index.json";
    private static readonly byte[] s_sourceIdentityKey = new byte[NuGetSourceIdentity.KeySizeInBytes];

    [Fact]
    public async Task WriteIfChangedAsync_LeavesAnIdenticalFileAlone()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "IntegrationRestore.csproj");
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project />", CancellationToken.None);
        var firstWrite = File.GetLastWriteTimeUtc(path);

        // MSBuild rebuilds when an input timestamp moves, so an identical rewrite must not touch the file.
        File.SetLastWriteTimeUtc(path, firstWrite.AddMinutes(-5));
        var backdated = File.GetLastWriteTimeUtc(path);
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project />", CancellationToken.None);

        Assert.Equal(backdated, File.GetLastWriteTimeUtc(path));
        Assert.Equal("<Project />", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteIfChangedAsync_WritesWhenContentDiffers()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "IntegrationRestore.csproj");
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project />", CancellationToken.None);
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />", CancellationToken.None);

        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteIfChangedAsync_RewritesAFileItCannotRead()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Read permission is removed with POSIX file modes.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "IntegrationRestore.csproj");
        await File.WriteAllTextAsync(path, "<Project />");

        // A generated file the CLI owns but momentarily cannot read -- an antivirus scanner holding it
        // open on Windows, a mode the user changed on POSIX -- must be rewritten, not turned into a
        // launch failure. The write below is what surfaces a genuinely unrecoverable error.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserWrite);
        }

        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />", CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void GetIntegrationBuildFailureMessage_ExplainsAPackageDowngrade()
    {
        var output = new OutputCollector();
        // Localized MSBuild output: the error code is the only stable part to match on.
        output.AppendOutput("IntegrationRestore.csproj : error NU1605: Advertencia como error: Degradación del paquete detectada: Aspire.Hosting de 13.6.0-dev a 13.5.0.");

        var message = PrebuiltAppHostServer.GetIntegrationBuildFailureMessage(output);

        Assert.Contains("aspire.config.json", message, StringComparison.Ordinal);
        Assert.Contains(VersionHelper.GetDefaultTemplateVersion(), message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetIntegrationBuildFailureMessage_FallsBackForOtherFailures()
    {
        var output = new OutputCollector();
        output.AppendOutput("Program.cs(3,1): error CS0103: The name 'Foo' does not exist in the current context");

        Assert.Equal(ErrorStrings.IntegrationBuildFailed, PrebuiltAppHostServer.GetIntegrationBuildFailureMessage(output));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_ProducesAspireHostingAndProjectReferences()
    {
        var projectRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(projectRefs, "13.5.0", "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var projectElements = doc.Descendants("ProjectReference").ToList();
        Assert.Single(projectElements);
        Assert.Equal("/path/to/MyIntegration.csproj", projectElements[0].Attribute("Include")?.Value);
        Assert.Equal("false", projectElements[0].Element("IsAspireProjectResource")?.Value);
        Assert.Equal("true", projectElements[0].Element("ReferenceOutputAssembly")?.Value);
        Assert.Null(projectElements[0].Element("Private"));

        var packageReference = Assert.Single(doc.Descendants("PackageReference"));
        Assert.Equal("Aspire.Hosting", packageReference.Attribute("Include")?.Value);
        Assert.Equal("13.5.0", packageReference.Attribute("Version")?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_UsesOnlyAspireHostingPackageReference()
    {
        var projectRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(projectRefs, "13.5.0", "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var packageReference = Assert.Single(doc.Descendants("PackageReference"));
        Assert.Equal("Aspire.Hosting", packageReference.Attribute("Include")?.Value);
        Assert.Single(doc.Descendants("ProjectReference"));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithSourceOverridePinsExactAspireHostingVersion()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(
            [],
            "13.5.0",
            "/tmp/libs",
            useExactPackageVersions: true);
        var doc = XDocument.Parse(xml);

        var packageReference = Assert.Single(doc.Descendants("PackageReference"));
        Assert.Equal("[13.5.0]", packageReference.Attribute("Version")?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DoesNotSetOutDir()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/custom/output/path");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Null(doc.Descendants(ns + "OutDir").FirstOrDefault());
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DoesNotSetEarlyOutputPathProperties()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/custom/output/path");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Null(doc.Descendants(ns + "BaseOutputPath").FirstOrDefault());
        Assert.Null(doc.Descendants(ns + "BaseIntermediateOutputPath").FirstOrDefault());
        Assert.Null(doc.Descendants(ns + "MSBuildProjectExtensionsPath").FirstOrDefault());
    }

    [Fact]
    public void CreateClosureDirectoryBuildProps_SetsEarlyOutputPathProperties()
    {
        var doc = IntegrationClosureBuilder.CreateClosureDirectoryBuildProps(
            "/custom/output/path",
            "/custom/intermediate/path",
            "/custom/policy/path",
            "/custom/packages/path");

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal(
            Path.Combine("/custom/output/path", "bin") + Path.DirectorySeparatorChar,
            doc.Descendants(ns + "BaseOutputPath").FirstOrDefault()?.Value);
        Assert.Equal(
            "/custom/intermediate/path" + Path.DirectorySeparatorChar,
            doc.Descendants(ns + "BaseIntermediateOutputPath").FirstOrDefault()?.Value);
        Assert.Equal("$(BaseIntermediateOutputPath)", doc.Descendants(ns + "MSBuildProjectExtensionsPath").FirstOrDefault()?.Value);
        Assert.Equal("/custom/policy/path", doc.Descendants(ns + "RestoreRootConfigDirectory").FirstOrDefault()?.Value);
        Assert.Equal(string.Empty, doc.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value);
        Assert.Equal("/custom/packages/path", doc.Descendants(ns + "RestorePackagesPath").FirstOrDefault()?.Value);
    }

    [Fact]
    public async Task CreateClosureProjectFile_BuildEmitsClosureContract()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var integrationDirectory = workspace.CreateDirectory("MyIntegration");
        var integrationProjectPath = Path.Combine(integrationDirectory.FullName, "MyIntegration.csproj");
        await File.WriteAllTextAsync(integrationProjectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var projectDirectory = workspace.CreateDirectory("generated-project");
        var restoreDirectory = workspace.CreateDirectory("integration-restore");
        var projectPath = Path.Combine(projectDirectory.FullName, "GeneratedClosure.csproj");
        var nuGetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(nuGetConfigPath, """
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);
        var projectFile = IntegrationClosureBuilder.CreateClosureProjectFile(
            restoreDirectory.FullName,
            additionalSources: null);
        projectFile.ProjectReferences.Add(new CSharpProjectReference(
            integrationProjectPath,
            IsAspireProjectResource: false,
            ReferenceOutputAssembly: true));

        await File.WriteAllTextAsync(projectPath, projectFile.ToXDocument().ToString());
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory.FullName, "Directory.Build.props"),
            IntegrationClosureBuilder.CreateClosureDirectoryBuildProps(
                restoreDirectory.FullName,
                Path.Combine(restoreDirectory.FullName, "obj"),
                workspace.WorkspaceRoot.FullName,
                globalPackagesFolder: null).ToString());

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet build.");
        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(process.ExitCode == 0, $"dotnet build failed:{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        Assert.True(File.Exists(Path.Combine(restoreDirectory.FullName, "obj", IntegrationClosureBuilder.ProjectAssetsFileName)));
        Assert.True(File.Exists(Path.Combine(restoreDirectory.FullName, "bin", "Debug", "net10.0", "GeneratedClosure.dll")));
        Assert.False(Directory.Exists(Path.Combine(projectDirectory.FullName, "obj")));
        Assert.False(Directory.Exists(Path.Combine(projectDirectory.FullName, "bin")));

        var sourcePaths = await File.ReadAllLinesAsync(
            Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureSourcesFileName));
        Assert.Equal(["MyIntegration.dll", "MyIntegration.pdb"], sourcePaths.Select(Path.GetFileName));
        Assert.All(sourcePaths, path => Assert.True(File.Exists(path)));
        Assert.Equal(
            ["|||", "|||"],
            await File.ReadAllLinesAsync(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureMetadataFileName)));
        Assert.Equal(
            ["MyIntegration.dll", "MyIntegration.pdb"],
            await File.ReadAllLinesAsync(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureTargetsFileName)));
        Assert.Equal(
            ["MyIntegration"],
            await File.ReadAllLinesAsync(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ProjectRefAssemblyNamesFileName)));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WritesClosureManifestFiles()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/tmp/work");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ClosureMetadataFileName), doc.Descendants(ns + "AspireClosureMetadataFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ClosureSourcesFileName), doc.Descendants(ns + "AspireClosureSourcesFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ClosureTargetsFileName), doc.Descendants(ns + "AspireClosureTargetsFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ProjectRefAssemblyNamesFileName), doc.Descendants(ns + "AspireProjectRefAssemblyNamesFile").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WritesClosureManifestTarget()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/tmp/work");
        var doc = XDocument.Parse(xml);

        var target = doc.Descendants("Target")
            .FirstOrDefault(element => element.Attribute("Name")?.Value == "_WriteAspireClosureManifest");

        Assert.NotNull(target);
        Assert.Equal("Build", target.Attribute("AfterTargets")?.Value);
        Assert.Equal("ResolveLockFileCopyLocalFiles", target.Attribute("DependsOnTargets")?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_HasCopyLocalLockFileAssemblies()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var copyLocal = doc.Descendants(ns + "CopyLocalLockFileAssemblies").FirstOrDefault()?.Value;
        Assert.Equal("true", copyLocal);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DisablesAnalyzersAndDocGen()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal("false", doc.Descendants(ns + "EnableDefaultItems").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "EnableNETAnalyzers").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "GenerateDocumentationFile").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "IsPackable").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "IsPublishable").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "ProduceReferenceAssembly").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_TargetsNet10()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal("net10.0", doc.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithAdditionalSources_SetsRestoreAdditionalProjectSources()
    {
        var sources = new[] { "/local/packages", "https://my-feed/v3/index.json" };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(
            [],
            "13.5.0",
            "/tmp/libs",
            sources);
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreConfigFile = doc.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").Single().Value;
        Assert.Null(restoreConfigFile);
        Assert.Equal("/local/packages;https://my-feed/v3/index.json", restoreSources);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithEmptyAdditionalSources_OverridesInheritedEnvironmentValue()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], "13.5.0", "/tmp/libs", Enumerable.Empty<string>());
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").Single();
        Assert.Equal(string.Empty, restoreSources.Value);
    }

    [Fact]
    public void Constructor_UsesWorkspaceAspireDirectoryForWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var server = CreatePrebuiltAppHostServer(workspace, appPath: appHostDirectory.FullName);

        var workingDirectory = Assert.IsType<string>(
            typeof(PrebuiltAppHostServer)
                .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(server));

        var normalizedWorkspaceRoot = PathNormalizer.ResolveToFilesystemPath(workspace.WorkspaceRoot.FullName);
        var rootDirectory = Path.Combine(normalizedWorkspaceRoot, ".aspire", "integrations", "apphosts");
        var isUnderRoot = workingDirectory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase);
        var parentDirectory = Path.GetDirectoryName(workingDirectory);
        var isDirectChildOfRoot = parentDirectory is not null &&
                                   string.Equals(parentDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase);
        var isSafeToDelete = isUnderRoot && isDirectChildOfRoot && !string.Equals(workingDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase);

        try
        {
            Assert.True(isSafeToDelete);
        }
        finally
        {
            if (isSafeToDelete && Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_WithoutRequestedChannelAndCurrentCliVersion_UsesIdentityLocalPackageSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string identityChannel = "pr-12345";
        const string identityVersion = "13.4.0-pr.17141.gf142085f";
        var packageSource = workspace.CreateDirectory("hive-packages");
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var channel = PackageChannel.CreateExplicitChannel(
            name: identityChannel,
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSource.FullName)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };
        var executionContext = CreateContextWithIdentityChannel(identityChannel, identityVersion);
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService, executionContext);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                identityVersion,
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", identityVersion)]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Equal([packageSource.FullName, NuGetOrgSource], Assert.IsType<string[]>(configuredSources));
            Assert.Contains($"Aspire.Hosting.Redis,[{identityVersion}]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithoutRequestedChannelAndDifferentSdkVersion_IgnoresIdentityLocalPackageSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string identityChannel = "pr-12345";
        const string identityVersion = "13.4.0-pr.17141.gf142085f";
        const string requestedVersion = "13.3.0";
        var packageSource = workspace.CreateDirectory("hive-packages");
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var channel = PackageChannel.CreateExplicitChannel(
            name: identityChannel,
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSource.FullName)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };
        var executionContext = CreateContextWithIdentityChannel(identityChannel, identityVersion);
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService, executionContext);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                requestedVersion,
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", requestedVersion)]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Empty(Assert.IsType<string[]>(configuredSources));
            Assert.DoesNotContain(packageSource.FullName, restoreArgs!);
            Assert.Contains($"Aspire.Hosting.Redis,{requestedVersion}", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithExplicitStableChannelAndCurrentCliVersion_IgnoresIdentityLocalPackageSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string identityChannel = "pr-12345";
        const string identityVersion = "13.4.0-pr.17141.gf142085f";
        var packageSource = workspace.CreateDirectory("hive-packages");
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var stableChannel = PackageChannel.CreateExplicitChannel(
            name: PackageChannelNames.Stable,
            quality: PackageChannelQuality.Stable,
            mappings: [new PackageMapping("Aspire*", NuGetOrgSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var identityPackageChannel = PackageChannel.CreateExplicitChannel(
            name: identityChannel,
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSource.FullName)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                [stableChannel, identityPackageChannel])
        };
        var executionContext = CreateContextWithIdentityChannel(identityChannel, identityVersion);
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService, executionContext);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                identityVersion,
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", identityVersion)],
                requestedChannel: PackageChannelNames.Stable);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Empty(Assert.IsType<string[]>(configuredSources));
            Assert.DoesNotContain(packageSource.FullName, restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public void Constructor_UsesDistinctWorkingDirectoriesForMultipleAppHostsInSameWorkspace()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstAppHost = workspace.CreateDirectory(Path.Combine("apps", "api"));
        var secondAppHost = workspace.CreateDirectory(Path.Combine("apps", "web"));

        PrebuiltAppHostServer CreateServer(string appHostDirectory) =>
            CreatePrebuiltAppHostServer(workspace, appPath: appHostDirectory);

        var firstServer = CreateServer(firstAppHost.FullName);
        var secondServer = CreateServer(secondAppHost.FullName);

        var workingDirectoryField = typeof(PrebuiltAppHostServer)
            .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var firstWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(firstServer));
        var secondWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(secondServer));

        var normalizedWorkspaceRoot = PathNormalizer.ResolveToFilesystemPath(workspace.WorkspaceRoot.FullName);
        var appHostsRoot = Path.Combine(normalizedWorkspaceRoot, ".aspire", "integrations", "apphosts");

        try
        {
            Assert.StartsWith(appHostsRoot, firstWorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(appHostsRoot, secondWorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(firstWorkingDirectory, secondWorkingDirectory);
        }
        finally
        {
            foreach (var dir in new[] { firstWorkingDirectory, secondWorkingDirectory })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void GetAppHostIntegrationCacheDirectory_NormalizesSymlinkedAppHostDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix symlink canonicalization is covered by this test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real-apphost");
        var linkDirectoryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "linked-apphost");
        TestSymlinkHelper.TryCreateSymlink(linkDirectoryPath, realDirectory.FullName);

        var realCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(realDirectory);
        var linkCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(new DirectoryInfo(linkDirectoryPath));

        Assert.Equal(realCacheDirectory.FullName, linkCacheDirectory.FullName);
    }

    [Fact]
    public async Task GetAppHostIntegrationCacheDirectory_PreservesLexicalWorkspaceForExternalSymlinkTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix symlink canonicalization is covered by this test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var lexicalWorkspace = workspace.WorkspaceRoot.CreateSubdirectory("workspace");
        await File.WriteAllTextAsync(Path.Combine(lexicalWorkspace.FullName, AspireConfigFile.FileName), "{}");
        var externalAppHost = workspace.WorkspaceRoot.CreateSubdirectory("external-apphost");
        var linkedAppHostPath = Path.Combine(lexicalWorkspace.FullName, "apphost");
        TestSymlinkHelper.TryCreateSymlink(linkedAppHostPath, externalAppHost.FullName);

        var externalCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(externalAppHost);
        var linkedCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(new DirectoryInfo(linkedAppHostPath));

        Assert.StartsWith(
            PathNormalizer.ResolveToFilesystemPath(
                Path.Combine(lexicalWorkspace.FullName, ".aspire", "integrations", "apphosts")) + Path.DirectorySeparatorChar,
            linkedCacheDirectory.FullName,
            StringComparisons.FileSystemPath);
        Assert.Equal(externalCacheDirectory.Name, linkedCacheDirectory.Name);
    }

    // The local pseudo-channel has no package-source mappings, so it does not require an overlay.
    // Every other explicit channel emits its mapping policy regardless of the running CLI identity.

    [Fact]
    public async Task CreateRestoreOverlay_LocalIdentity_LocalRequested_ReturnsNull()
    {
        // A local pseudo-channel does not define a package-source mapping policy.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await CreateRestoreOverlayAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_LocalIdentity_PrRequested_EmitsOverlay()
    {
        // The project-requested channel determines the overlay independently of the CLI identity.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StableIdentity_StableRequested_UsesAmbientPolicy()
    {
        // Stable uses the same ambient NuGet source policy as an omitted channel.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("stable");
        var server = CreateServerWithExplicitChannel(workspace, "stable", executionContext);

        var result = await CreateRestoreOverlayAsync(server, "stable");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StableIdentity_LocalRequested_ReturnsNull()
    {
        // Overlay selection depends on the project-requested channel, not the CLI identity.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("stable");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await CreateRestoreOverlayAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_DailyIdentity_DailyRequested_EmitsOverlay()
    {
        // An explicit daily channel supplies its package-source mapping overlay.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        var server = CreateServerWithExplicitChannel(workspace, "daily", executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "daily");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_PrIdentity_DifferentPrRequested_EmitsOverlay()
    {
        // The requested PR channel supplies the overlay independently of the running PR build.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("pr-67890");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_LocalIdentity_StagingRequested_EmitsOverlayWithGlobalPackagesFolder()
    {
        // The policy overlay is temporary, so the source-specific package cache must use a stable
        // absolute path that survives overlay cleanup and remains valid for the package manifest.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, channelSource)
        };
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: mappings,
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var server = CreateServerWithChannel(workspace, stagingChannel, executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "staging");

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        var gpf = doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gpf);
        var gpfValue = gpf.Attribute("value")?.Value;
        Assert.False(string.IsNullOrEmpty(gpfValue));
        Assert.True(Path.IsPathFullyQualified(gpfValue), $"globalPackagesFolder value must be an absolute path. Got: {gpfValue}");
        // The overlay directory is recursively deleted on dispose; the cache must live elsewhere
        // so manifest paths produced by BundleNuGetService remain valid for the AppHost's lifetime.
        var overlayDirectory = result.ConfigFile.Directory!.FullName;
        Assert.False(
            gpfValue!.StartsWith(overlayDirectory, StringComparison.Ordinal),
            $"globalPackagesFolder must not be under the policy overlay directory '{overlayDirectory}'. Got: {gpfValue}");
        // The cache subdirectory must be keyed by the resolved feed URL so two different staging
        // feeds (e.g. two darc builds or an overrideStagingFeed setting) get distinct caches.
        var expectedCacheKey = CliPathHelper.ComputeStagingCacheIdentityKey(result.CacheIdentity);
        Assert.NotNull(expectedCacheKey);
        var expectedCachePath = Path.Combine(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory),
            expectedCacheKey);
        Assert.Equal(expectedCachePath, gpfValue);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StagingRequested_FromRealPackagingService_EmitsStableGlobalPackagesFolderOutsideOverlayDirectory()
    {
        // A staging channel synthesized by the real packaging service must select an absolute
        // package cache outside the temporary overlay directory. Package manifests retain paths
        // into this cache after the overlay is disposed.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        const string overrideStagingFeed = "https://pkgs.dev.azure.com/dnceng/internal/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json";
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workspace,
            identityChannel: PackageChannelNames.Staging);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = overrideStagingFeed
            })
            .Build();
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            configuration,
            NullLogger<PackagingService>.Instance);

        var server = CreateServerWithPackagingService(workspace, packagingService, executionContext);

        using var result = await CreateRestoreOverlayAsync(server, PackageChannelNames.Staging);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        var gpf = doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gpf);
        var gpfValue = gpf.Attribute("value")?.Value;
        Assert.False(string.IsNullOrEmpty(gpfValue));
        Assert.True(Path.IsPathFullyQualified(gpfValue), $"globalPackagesFolder value must be an absolute path. Got: {gpfValue}");
        var overlayDirectory = result.ConfigFile.Directory!.FullName;
        Assert.False(
            gpfValue!.StartsWith(overlayDirectory, StringComparison.Ordinal),
            $"globalPackagesFolder must not be under the policy overlay directory '{overlayDirectory}'. Got: {gpfValue}");
        // The cache key is derived from the resolved staging feed URL so the same CLI talking to
        // a different overrideStagingFeed gets a different cache bucket.
        var expectedCacheKey = CliPathHelper.ComputeStagingCacheIdentityKey(result.CacheIdentity);
        Assert.NotNull(expectedCacheKey);
        var expectedCachePath = Path.Combine(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory),
            expectedCacheKey);
        Assert.Equal(expectedCachePath, gpfValue);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-99")]
    public async Task CreateRestoreOverlay_LocalRequested_ReturnsNull_RegardlessOfIdentity(string identity)
    {
        // The project-requested local channel never emits a mapping overlay, independently of the
        // running CLI identity.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel(identity);
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await CreateRestoreOverlayAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public void ComposePackageSourceMappings_WithoutAmbientMappings_PreservesAmbientEligibility()
    {
        var mappings = PrebuiltAppHostServer.ComposePackageSourceMappings(
            [new PackageMapping("Aspire*", "https://example.com/staging")],
            ambientMappings: [],
            ambientSources:
            [
                CreateNuGetSourceInfo("private", "https://example.com/private", isEnabled: true),
                CreateNuGetSourceInfo("mirror", "https://example.com/mirror", isEnabled: true),
                CreateNuGetSourceInfo("disabled", "https://example.com/disabled", isEnabled: false)
            ],
            selectedSources:
            [
                new NuGetConfigSource(
                    "aspire-0",
                    "https://example.com/staging",
                    IsAmbient: false,
                    IsEnabled: true)
            ]);

        Assert.Equal(["*"], GetPatterns(mappings, "private"));
        Assert.Equal(["*"], GetPatterns(mappings, "mirror"));
        Assert.Empty(GetPatterns(mappings, "disabled"));
        Assert.Equal(["Aspire*"], GetPatterns(mappings, "aspire-0"));
    }

    [Fact]
    public void ComposePackageSourceMappings_ReplacesCompetingAspireMappingsAndPreservesUnrelatedPolicy()
    {
        var mappings = PrebuiltAppHostServer.ComposePackageSourceMappings(
            [new PackageMapping("Aspire*", "https://example.com/staging")],
            ambientMappings:
            [
                new NuGetPackageSourceMappingInfo(
                    "mirror",
                    ["*", "Aspire*", "Aspire.Hosting.*", "Aspire.Hosting.Redis", "Contoso.*"]),
                new NuGetPackageSourceMappingInfo("private", ["Microsoft.*"])
            ],
            ambientSources: [],
            selectedSources:
            [
                new NuGetConfigSource(
                    "staging",
                    "https://example.com/staging",
                    IsAmbient: true,
                    IsEnabled: true)
            ]);

        Assert.Equal(["*", "Contoso.*"], GetPatterns(mappings, "mirror"));
        Assert.Equal(["Microsoft.*"], GetPatterns(mappings, "private"));
        Assert.Equal(["Aspire*"], GetPatterns(mappings, "staging"));
    }

    [Fact]
    public void ComposePackageSourceMappings_WithExactSourceOverride_PreservesAmbientDependencyMappings()
    {
        var mappings = PrebuiltAppHostServer.ComposePackageSourceMappings(
            [
                new PackageMapping("Aspire.Hosting.Redis", "https://example.com/override"),
                new PackageMapping("*", "https://example.com/override"),
                new PackageMapping("*", NuGetOrgSource)
            ],
            ambientMappings:
            [
                new NuGetPackageSourceMappingInfo("channel", ["Aspire*"]),
                new NuGetPackageSourceMappingInfo("private", ["Contoso.*"])
            ],
            ambientSources: [],
            selectedSources:
            [
                new NuGetConfigSource(
                    "override",
                    "https://example.com/override",
                    IsAmbient: false,
                    IsEnabled: true),
                new NuGetConfigSource(
                    "nuget.org",
                    NuGetOrgSource,
                    IsAmbient: false,
                    IsEnabled: true)
            ]);

        Assert.Equal(["Aspire.Hosting.Redis", "*"], GetPatterns(mappings, "override"));
        Assert.Equal(["Aspire*"], GetPatterns(mappings, "channel"));
        Assert.Equal(["Contoso.*"], GetPatterns(mappings, "private"));
        Assert.Empty(GetPatterns(mappings, "nuget.org"));
    }

    [Fact]
    public void ComposePackageSourceMappings_WithAmbientMappings_DoesNotAddChannelFallbackMapping()
    {
        var mappings = PrebuiltAppHostServer.ComposePackageSourceMappings(
            [
                new PackageMapping("Aspire*", "https://example.com/staging"),
                new PackageMapping("*", "https://api.nuget.org/v3/index.json")
            ],
            ambientMappings:
            [
                new NuGetPackageSourceMappingInfo("mirror", ["*"])
            ],
            ambientSources: [],
            selectedSources:
            [
                new NuGetConfigSource(
                    "staging",
                    "https://example.com/staging",
                    IsAmbient: false,
                    IsEnabled: true),
                new NuGetConfigSource(
                    "nuget.org",
                    "https://api.nuget.org/v3/index.json",
                    IsAmbient: false,
                    IsEnabled: true)
            ]);

        Assert.Equal(["*"], GetPatterns(mappings, "mirror"));
        Assert.Equal(["Aspire*"], GetPatterns(mappings, "staging"));
        Assert.Empty(GetPatterns(mappings, "nuget.org"));
    }

    [Fact]
    public void ResolveNuGetConfigSources_AvoidsDormantReservedSourceKeys()
    {
        var sources = PrebuiltAppHostServer.ResolveNuGetConfigSources(
            [
                new PackageMapping("Aspire*", "https://example.com/staging"),
                new PackageMapping("*", "https://example.com/fallback")
            ],
            ambientSources: [],
            reservedPackageSourceKeys: ["aspire-0", "aspire-2"],
            s_sourceIdentityKey);

        Assert.Equal(["aspire-1", "aspire-3"], sources.Select(static source => source.Key));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ResolveNuGetConfigSources_PrefersDisabledAliasWithAuthentication(
        bool hasCredentials,
        bool hasClientCertificates)
    {
        const string source = "https://example.com/private";
        var sources = PrebuiltAppHostServer.ResolveNuGetConfigSources(
            [new PackageMapping("Aspire*", source)],
            ambientSources:
            [
                CreateNuGetSourceInfo("anonymous", source, isEnabled: false),
                CreateNuGetSourceInfo(
                    "authenticated",
                    source,
                    isEnabled: false,
                    hasCredentials,
                    hasClientCertificates)
            ],
            reservedPackageSourceKeys: ["anonymous", "authenticated"],
            s_sourceIdentityKey);

        Assert.Equal("authenticated", Assert.Single(sources).Key);
    }

    [Fact]
    public void ResolveNuGetConfigSources_PreservesEnabledAliasOverDisabledAuthenticatedAlias()
    {
        const string source = "https://example.com/private";
        var sources = PrebuiltAppHostServer.ResolveNuGetConfigSources(
            [new PackageMapping("Aspire*", source)],
            ambientSources:
            [
                CreateNuGetSourceInfo("enabled", source, isEnabled: true),
                CreateNuGetSourceInfo("authenticated", source, isEnabled: false, hasCredentials: true)
            ],
            reservedPackageSourceKeys: ["enabled", "authenticated"],
            s_sourceIdentityKey);

        Assert.Equal("enabled", Assert.Single(sources).Key);
    }

    [Fact]
    public void CreateNuGetConfigOverlay_EnablesSelectedAliasWithoutReEmittingCaseVariantDisabledKey()
    {
        const string source = "https://example.com/staging";
        var settings = new NuGetSettingsInfo(
            ConfigPaths: [],
            CacheIdentity: "settings",
            Sources:
            [
                CreateNuGetSourceInfo("Private", source, isEnabled: false),
                CreateNuGetSourceInfo("unrelated", "https://example.com/unrelated", isEnabled: false)
            ],
            SensitiveSourceValues: [],
            PackageSourceMappingEnabled: false,
            PackageSourceMappings: [],
            DisabledPackageSourceKeys: ["private", "unrelated"],
            ReservedPackageSourceKeys: ["Private", "unrelated"],
            SourceIdentityKey: s_sourceIdentityKey);

        var overlay = PrebuiltAppHostServer.CreateNuGetConfigOverlay(
            [new PackageMapping("Aspire*", source)],
            settings,
            [new NuGetConfigSource("Private", source, IsAmbient: true, IsEnabled: false)],
            globalPackagesFolder: null);

        Assert.True(overlay.ClearDisabledPackageSources);
        Assert.Equal(["unrelated"], overlay.DisabledPackageSourceKeys);
        Assert.Equal(["Aspire*"], GetPatterns(overlay.PackageSourceMappings, "Private"));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_MapsAspireToOverrideAndAddsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: null,
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverrideWithoutRequestedChannel_DoesNotIncludeExplicitChannelAspireMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var explicitChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, explicitChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: null,
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Empty(GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_PreservesRequestedChannelMappingsAndGlobalPackagesFolder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("CommunityToolkit*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var executionContext = CreateContextWithIdentityChannel("pr-12345");
        var server = CreateServerWithChannel(workspace, stagingChannel, executionContext);

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal(["CommunityToolkit*"], GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
        var gpf = doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gpf);
        // The source-specific package cache must outlive the policy overlay so BundleNuGetService's
        // manifest paths remain valid after disposal.
        var gpfValue = gpf.Attribute("value")?.Value;
        Assert.False(string.IsNullOrEmpty(gpfValue));
        Assert.True(Path.IsPathFullyQualified(gpfValue), $"globalPackagesFolder value must be an absolute path. Got: {gpfValue}");
        var overlayDirectory = result.ConfigFile.Directory!.FullName;
        Assert.False(
            gpfValue!.StartsWith(overlayDirectory, StringComparison.Ordinal),
            $"globalPackagesFolder must not be under the policy overlay directory '{overlayDirectory}'. Got: {gpfValue}");
        // The cache key is derived from the --source override, not the channel's own mappings,
        // so users running multiple overrides against the same CLI get distinct cache buckets.
        var expectedCacheKey = CliPathHelper.ComputeStagingCacheIdentityKey(result.CacheIdentity);
        Assert.NotNull(expectedCacheKey);
        var expectedCachePath = Path.Combine(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory),
            expectedCacheKey);
        Assert.Equal(expectedCachePath, gpfValue);
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_DropsRequestedChannelAspireMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource), new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Empty(GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Theory]
    [InlineData("Aspire.Hosting.Redis")]
    [InlineData("CommunityToolkit.Aspire.Hosting.Redis")]
    public async Task CreateRestoreOverlay_WithExactPackageSourceOverride_PreservesChannelAspireMappings(
        string packageId)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/integration-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var server = CreateServerWithChannel(
            workspace,
            stagingChannel,
            CreateContextWithIdentityChannel("pr-12345"));

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride,
            packageSourceOverridePattern: packageId);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal([packageId, PackageMapping.AllPackages], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithUnknownRequestedChannel_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRestoreOverlayAsync(
                server,
                requestedChannel: PackageChannelNames.Staging,
                packageSourceOverride: packageSourceOverride));

        Assert.Contains(PackageChannelNames.Staging, exception.Message);
        Assert.Equal(PackageChannelNames.Staging, packagingService.LastRequestedChannelName);
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_UsesChannelAllPackagesMappingAsFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping(PackageMapping.AllPackages, channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, channelSource));
        Assert.Empty(GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_WhenChannelLookupFails_StillCreatesOverridePolicyWithFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channel lookup failed.")
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithExplicitChannelAndNoOverride_WhenChannelLookupFails_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channel lookup failed.")
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRestoreOverlayAsync(
                server,
                requestedChannel: "staging",
                packageSourceOverride: null));

        Assert.Equal("Channel lookup failed.", exception.Message);
    }

    [Fact]
    public async Task ResolveAdditionalSources_WithPackageSourceOverrideAndMatchedChannel_OmitsChannelAspireFeedFromSources()
    {
        // Additional sources are co-eligible with mapped sources, so the channel's Aspire feed
        // must be excluded when an explicit source owns the Aspire package mapping.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await ResolveAdditionalSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.DoesNotContain(channelSource, sources);
        Assert.Contains(NuGetOrgSource, sources);
    }

    [Fact]
    public async Task ResolveAdditionalSources_WithPackageSourceOverrideAndMatchedChannelNonAspireMapping_KeepsChannelSourceAndAddsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("CommunityToolkit*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await ResolveAdditionalSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.Contains(channelSource, sources);
        // The policy adds NuGet.org as the catch-all when the channel does not provide one.
        Assert.Contains(NuGetOrgSource, sources);
    }

    [Fact]
    public async Task ResolveAdditionalSources_WithPackageSourceOverrideAndMatchedChannelAllPackagesMapping_OmitsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping(PackageMapping.AllPackages, channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await ResolveAdditionalSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.Contains(channelSource, sources);
        // The channel's catch-all remains authoritative, so NuGet.org is not added as another
        // co-eligible source.
        Assert.DoesNotContain(NuGetOrgSource, sources);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", "https://api.nuget.org/v3/index.json")]
    [InlineData("/tmp/aspire-packages", "/tmp/aspire-packages")]
    [InlineData(@"C:\packages", @"C:\packages")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json", "https://***@feed.example.com/v3/index.json")]
    [InlineData("https://feed.blob.core.windows.net/foo/index.json?sv=2024-01&sig=secret-sig", "https://feed.blob.core.windows.net/foo/index.json")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json?sig=secret", "https://***@feed.example.com/v3/index.json")]
    [InlineData("https://feed.example.com/v3/index.json#fragment", "https://feed.example.com/v3/index.json")]
    public void RedactSourceForDisplay_StripsCredentialsAndQueryFromHttpUrlsButPreservesPlainSources(string input, string expected)
    {
        Assert.Equal(expected, PrebuiltAppHostServer.RedactSourceForDisplay(input));
    }

    [Fact]
    public async Task CreateRestoreOverlay_StagingRequested_RefusesWhenPackagingServiceReportsUnavailable()
    {
        // An unavailable staging channel must fail with the packaging service's actionable reason
        // instead of allowing restore to use another explicit channel.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRestoreOverlayAsync(server, "staging"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StagingRequestedWithSourceOverride_RefusesWhenPackagingServiceReportsUnavailable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRestoreOverlayAsync(server, "staging", "/tmp/aspire-pr-hive/packages"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task ResolveAdditionalSources_StagingRequested_RefusesWhenPackagingServiceReportsUnavailable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveAdditionalSourcesAsync(server, "staging"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task ResolveAdditionalSources_NonStagingRequest_NotAffectedByStagingUnavailableReason()
    {
        // Negative control: the staging refusal must only fire for requestedChannel == "staging".
        // A request for any other channel name must continue to resolve normally even when the
        // packaging service is reporting staging-unavailable.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]),
            GetStagingChannelUnavailableReasonCallback = () => "Staging unavailable"
        };

        var server = CreatePrebuiltAppHostServer(workspace, packagingService: packagingService, executionContext: executionContext);

        var sources = await ResolveAdditionalSourcesAsync(server, "daily");

        Assert.NotNull(sources);
        Assert.Contains("https://pkgs.dev.azure.com/fake/v3/index.json", sources);
    }

    private static PrebuiltAppHostServer CreateServerWithUnavailableStagingChannel(
        TemporaryWorkspace workspace,
        CliExecutionContext executionContext,
        string unavailableReason)
    {
        // PackagingService omits an unavailable staging channel and reports the reason separately.
        // Returning a daily channel verifies that source resolution does not substitute it.
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]),
            GetStagingChannelUnavailableReasonCallback = () => unavailableReason
        };

        return CreatePrebuiltAppHostServer(workspace, packagingService: packagingService, executionContext: executionContext);
    }

    private static CliExecutionContext CreateContextWithIdentityChannel(
        string identityChannel,
        string? identityVersion = null) =>
        new(new DirectoryInfo(Path.GetTempPath()),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hives")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "sdks")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "logs")),
            "test.log",
            identityChannel: identityChannel,
            identityVersion: identityVersion);

    // Builds a PrebuiltAppHostServer with the constant test wiring (socket name, SDK installer, process
    // execution factory, logger) so individual tests only specify the parameters their scenario exercises.
    // The execution context defaults to a fresh test context and is shared with the default NuGet service.
    // Tests that need bundle-layout discovery (FixedLayoutDiscovery) or a custom process runner build their
    // own BundleNuGetService and pass it via nugetService.
    private static PrebuiltAppHostServer CreatePrebuiltAppHostServer(
        TemporaryWorkspace workspace,
        string? appPath = null,
        LayoutConfiguration? layout = null,
        TestDotNetCliRunner? dotNetCliRunner = null,
        IPackagingService? packagingService = null,
        CliExecutionContext? executionContext = null,
        BundleNuGetService? nugetService = null)
    {
        executionContext ??= TestExecutionContextFactory.CreateTestContext();

        if (nugetService is null)
        {
            var nugetLayoutRoot = workspace.CreateDirectory("nuget-layout");
            var managedDirectory = nugetLayoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
            File.WriteAllText(
                Path.Combine(
                    managedDirectory.FullName,
                    BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                string.Empty);
            var nugetExecutionFactory = new TestProcessExecutionFactory
            {
                AssertionCallback = (args, _, _, _) =>
                {
                    WriteNuGetConfigOverlayIfRequested(args);
                    WritePackageProbeManifestIfRequested(args);
                },
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse())
            };
            nugetService = new BundleNuGetService(
                new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = nugetLayoutRoot.FullName }),
                new LayoutProcessRunner(nugetExecutionFactory),
                new TestFeatures(),
                new TestEnvironment(),
                NullLogger<BundleNuGetService>.Instance);
        }

        return new PrebuiltAppHostServer(
            appPath ?? workspace.WorkspaceRoot.FullName,
            "test.sock",
            layout ?? new LayoutConfiguration(),
            nugetService,
            dotNetCliRunner ?? new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            packagingService ?? MockPackagingServiceFactory.Create(),
            executionContext,
            new TestProcessExecutionFactory(),
            new TestEnvironment(),
            NullLogger.Instance);
    }

    private static PrebuiltAppHostServer CreateServerWithExplicitChannel(
        TemporaryWorkspace workspace,
        string channelName,
        CliExecutionContext executionContext)
    {
        // channelName is the name of the channel registered in the TestPackagingService — i.e. the
        // channel a project's aspire.config.json would resolve to when it requests that name.
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var channel = PackageChannel.CreateExplicitChannel(
            channelName, PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        return CreateServerWithChannel(workspace, channel, executionContext);
    }

    private static PrebuiltAppHostServer CreateServerWithChannel(
        TemporaryWorkspace workspace,
        PackageChannel channel,
        CliExecutionContext executionContext)
    {
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        return CreateServerWithPackagingService(workspace, packagingService, executionContext);
    }

    private static PrebuiltAppHostServer CreateServerWithPackagingService(
        TemporaryWorkspace workspace,
        IPackagingService packagingService,
        CliExecutionContext? executionContext = null)
    {
        return CreatePrebuiltAppHostServer(workspace, packagingService: packagingService, executionContext: executionContext);
    }

    private static async Task<TemporaryNuGetConfig?> CreateRestoreOverlayAsync(
        PrebuiltAppHostServer server,
        string? requestedChannel,
        string? packageSourceOverride = null,
        string? packageSourceOverridePattern = null)
    {
        var restoreSources = await server.ResolveIntegrationRestoreSourcesAsync(
            requestedChannel,
            packageSourceOverride,
            packageSourceOverridePattern,
            CancellationToken.None);
        var configSources = PrebuiltAppHostServer.ResolveNuGetConfigSources(
            restoreSources.PackageSourceMappings,
            ambientSources: [],
            reservedPackageSourceKeys: [],
            s_sourceIdentityKey);
        return await server.CreateRestoreOverlayAsync(
            restoreSources,
            configSources,
            new NuGetSettingsInfo([], "settings", [], [], false, [], [], [], s_sourceIdentityKey),
            CancellationToken.None);
    }

    private static NuGetSourceInfo CreateNuGetSourceInfo(
        string name,
        string source,
        bool isEnabled,
        bool hasCredentials = false,
        bool hasClientCertificates = false)
        => new(
            name,
            NuGetSourceIdentity.Compute(source, s_sourceIdentityKey),
            isEnabled,
            hasCredentials,
            hasClientCertificates);

    private static async Task<IReadOnlyList<string>?> ResolveAdditionalSourcesAsync(
        PrebuiltAppHostServer server,
        string? requestedChannel,
        string? packageSourceOverride = null,
        string? packageSourceOverridePattern = null)
    {
        var restoreSources = await server.ResolveIntegrationRestoreSourcesAsync(
            requestedChannel,
            packageSourceOverride,
            packageSourceOverridePattern,
            CancellationToken.None);
        return restoreSources.AdditionalSources.Count > 0 ? restoreSources.AdditionalSources : null;
    }

    [Fact]
    public async Task ResolveRequestedChannel_UsesProjectLocalAspireConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-new"
            }
            """);

        var server = CreatePrebuiltAppHostServer(workspace);

        var channel = server.ResolveRequestedChannel();

        Assert.Equal("pr-new", channel);
    }

    [Fact]
    public async Task PrepareAsync_WithNoIntegrations_WritesDefaultAppSettings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var server = CreatePrebuiltAppHostServer(workspace);

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", []);

            Assert.True(
                result.Success,
                result.Output is null
                    ? null
                    : string.Join(Environment.NewLine, result.Output.GetLines().Select(static line => line.Line)));
            Assert.Null(server.SelectedProjectLayoutPath);

            var appSettingsPath = Path.Combine(workingDirectory, "appsettings.json");
            Assert.True(File.Exists(appSettingsPath));

            var appSettingsContent = await File.ReadAllTextAsync(appSettingsPath);
            Assert.Contains("\"Aspire.Hosting\"", appSettingsContent);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferences_SetsOnlyPackageProbeManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")]);

            Assert.True(result.Success);
            Assert.Null(server.SelectedProjectLayoutPath);
            Assert.Equal(3, executionFactory.AttemptCount);

            var manifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            Assert.StartsWith(
                CliPathHelper.StripMacOSFirmlinkPrefix(
                    Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore")),
                CliPathHelper.StripMacOSFirmlinkPrefix(manifestPath),
                StringComparison.OrdinalIgnoreCase);

            var startInfo = server.CreateStartInfo(123);
            Assert.Equal(manifestPath, startInfo.Environment[KnownConfigNames.IntegrationProbeManifestPath]);
            Assert.False(startInfo.Environment.ContainsKey(KnownConfigNames.IntegrationLibsPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenPackageRestoreIsCanceled_PropagatesCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AsyncAttemptCallback = (_, _, cancellationToken) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<(int ExitCode, string? Stdout)>(cancellationToken);
        };
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")],
                cancellationToken: cancellation.Token));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferences_UsesPackageSourceOverride()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSourceOverride = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "aspire-pr-hive",
            "packages");
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Equal([packageSourceOverride, NuGetOrgSource], Assert.IsType<string[]>(configuredSources));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageSourceOverride_AddsNuGetOrgFallbackSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSourceOverride = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "aspire-pr-hive",
            "packages");
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Equal([packageSourceOverride, NuGetOrgSource], Assert.IsType<string[]>(configuredSources));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("pr-12345")]
    [InlineData("local")]
    [InlineData("worktree-feature")]
    public async Task PrepareAsync_WithHiveBackedChannel_UsesLocalAspireSourceAsOverride(string channelName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSource = workspace.CreateDirectory("hive-packages");
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, $$"""
            {
                "channel": "{{channelName}}"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: channelName,
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSource.FullName)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Equal([packageSource.FullName, NuGetOrgSource], Assert.IsType<string[]>(configuredSources));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHiveBackedChannelUsingFileUri_UsesLocalAspireSourceAsOverride()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSource = workspace.CreateDirectory("hive-packages");
        var packageSourceUri = new Uri(packageSource.FullName).AbsoluteUri;
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSourceUri)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Contains(packageSourceUri, configuredSources!);
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithExplicitPackageSourceOverride_IgnoresHiveBackedAspireSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var explicitPackageSource = workspace.CreateDirectory("explicit-packages");
        var hivePackageSource = workspace.CreateDirectory("hive-packages");
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", hivePackageSource.FullName),
                new PackageMapping(PackageMapping.AllPackages, channelSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: explicitPackageSource.FullName);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Equal([explicitPackageSource.FullName, channelSource], Assert.IsType<string[]>(configuredSources));
            Assert.DoesNotContain(hivePackageSource.FullName, restoreArgs!);
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHttpBackedChannel_DoesNotUseExactPackageVersions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Equal([channelSource], Assert.IsType<string[]>(configuredSources));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,13.4.0-pr.17141.gf142085f", restoreArgs!);
            Assert.DoesNotContain("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenPackagingServiceThrowsForExplicitChannel_Fails()
    {
        // An explicitly requested channel must not silently fall back to ambient NuGet sources
        // when channel resolution fails, because that could restore a different package build
        // while reporting the requested channel.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelName = "pr-12345";
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, $$"""
            {
                "channel": "{{channelName}}"
            }
            """);

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromException<IEnumerable<PackageChannel>>(
                new InvalidOperationException("simulated packaging service failure"))
        };
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.False(result.Success);
            Assert.Null(restoreArgs);
            Assert.Contains(
                result.Output!.GetLines(),
                line => line.Line.Contains("simulated packaging service failure", StringComparison.Ordinal));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHiveBackedChannelPointingAtMissingLocalDirectory_DoesNotApplyOverride()
    {
        // Negative case for ResolveLocalPackageSourceOverrideAsync: a stale aspire.config.json
        // (e.g. user pinned channel = "pr-12345" but later deleted the local hive directory)
        // must NOT cause the prebuilt restore to pin Aspire packages to a non-existent local
        // directory. GetExistingLocalAspirePackageSource skips mappings whose Source does not
        // exist on disk, so auto-discovery returns null and restore falls through to the
        // ambient + channel-source path with no exact-pin.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var missingPackageSource = Path.Combine(workspace.WorkspaceRoot.FullName, "this-hive-was-deleted");
        Assert.False(Directory.Exists(missingPackageSource));
        List<string>? restoreArgs = null;
        string[]? configuredSources = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", missingPackageSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
                configuredSources = GetPackageSourcesFromConfigArguments(args);
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            // The override was not applied (Directory.Exists check failed), so the source list
            // is just the channel's raw Aspire mapping with no NuGet.org fallback appended (the
            // fallback only fires on the override path), and no exact-pin is emitted. Contrast
            // with PrepareAsync_WithHiveBackedChannel_UsesLocalAspireSourceAsOverride where the
            // existing local directory promotes the channel source to an override and adds the
            // NuGet.org fallback + exact-pinning.
            Assert.Empty(GetSourceArguments(restoreArgs!));
            Assert.Equal([missingPackageSource], Assert.IsType<string[]>(configuredSources));
            Assert.DoesNotContain(NuGetOrgSource, configuredSources!);
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,13.4.0-pr.17141.gf142085f", restoreArgs!);
            Assert.DoesNotContain("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithSourceAndChannelHavingAspireMapping_PolicyOverlayDropsChannelAspireMapping()
    {
        // End-to-end check that `aspire new --source <pr> --channel <X>` does not let the channel's
        // Aspire* feed remain co-eligible with the override at restore time.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSourceOverride = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "aspire-pr-hive",
            "packages");
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        XDocument? policyOverlay = null;
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            WriteNuGetConfigOverlayIfRequested(args);
            if (args is ["nuget", "restore", ..])
            {
                // Read the policy overlay while it still exists; it is disposed when
                // PrepareAsync's inner `using var` exits, which races with our assertions.
                var argsList = (IReadOnlyList<string>)args;
                var nugetConfigIndex = -1;
                for (var i = 0; i < argsList.Count - 1; i++)
                {
                    if (argsList[i] == "--nuget-config")
                    {
                        nugetConfigIndex = i;
                        break;
                    }
                }

                if (nugetConfigIndex >= 0)
                {
                    policyOverlay = XDocument.Load(argsList[nugetConfigIndex + 1]);
                }
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.Equal("daily", result.ChannelName);
            Assert.NotNull(policyOverlay);

            // The overlay's complete mapping policy makes only the override eligible for Aspire packages.
            Assert.Equal(["Aspire*"], GetPackagePatternsForSource(policyOverlay!, packageSourceOverride));
            Assert.Empty(GetPackagePatternsForSource(policyOverlay!, channelSource));
            Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(policyOverlay!, NuGetOrgSource));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_OutputIncludesSourceAndChannelContext()
    {
        // When restore fails, the displayed output is the only debugging surface most users see.
        // Pin that --source and the requested channel are present so a failed
        // `aspire new --source <X> --channel <Y>` doesn't require re-running with diagnostic logs
        // just to recover which inputs were in play.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        // Fail the restore step itself; BundleNuGetService throws on non-zero exit which
        // propagates through PrepareAsync's outer catch.
        executionFactory.DefaultExitCode = 1;

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: packageSourceOverride);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            Assert.Contains($"--source: {packageSourceOverride}", combined);
            Assert.Contains("channel:  daily", combined);
            Assert.Contains("packages: Aspire.Hosting.CodeGeneration.TypeScript 13.4.0-pr.17141.gf142085f", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_WithManyPackages_TruncatesPackageList()
    {
        // The package preview caps at 5 entries with a "(+N more)" suffix so the error footer
        // doesn't explode for projects with large package counts. Pin the truncation shape so
        // it can't silently regress.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.DefaultExitCode = 1;

        var packages = Enumerable.Range(0, 8)
            .Select(i => IntegrationReference.FromPackage($"Aspire.Hosting.Pkg{i}", "1.0.0"))
            .ToArray();

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                packages,
                packageSourceOverride: packageSourceOverride);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            // First five packages appear; later ones are collapsed into a count.
            Assert.Contains("Aspire.Hosting.Pkg0 1.0.0", combined);
            Assert.Contains("Aspire.Hosting.Pkg4 1.0.0", combined);
            Assert.DoesNotContain("Aspire.Hosting.Pkg5 1.0.0", combined);
            Assert.DoesNotContain("Aspire.Hosting.Pkg7 1.0.0", combined);
            Assert.Contains("(+3 more)", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferencesAndNoRequestedChannel_UsesAmbientSourcePolicy()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        XDocument? generatedProject = null;
        var policyConfigPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            AspireJsonConfiguration.SettingsFolder,
            "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(policyConfigPath)!);
        await File.WriteAllTextAsync(policyConfigPath, "<configuration />");

        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, _, _) =>
            {
                generatedProject = XDocument.Load(projectFilePath.FullName);
                WriteClosureInputs(
                    projectFilePath.Directory!,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MyIntegration.dll"] = "integration-v1"
                    },
                    ["MyIntegration"]);
                return 0;
            }
        };
        var channel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            logger: NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };
        var server = CreatePrebuiltAppHostServer(
            workspace,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(generatedProject);
            var ns = generatedProject.Root!.GetDefaultNamespace();
            Assert.Equal(
                string.Empty,
                generatedProject.Descendants(ns + "RestoreAdditionalProjectSources").Single().Value);
            Assert.False(File.Exists(Path.Combine(workingDirectory, "integration-restore", "NuGet.Config")));
            Assert.False(File.Exists(policyConfigPath));
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferencesAndExplicitChannel_UsesDiscoveredConfigAndPersistentPolicyOverlay()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var noRestoreValues = new List<bool>();
        var processOptions = new List<ProcessInvocationOptions>();
        XDocument? generatedProject = null;
        XDocument? generatedPolicyOverlay = null;
        string? nugetConfigDiscoveryDirectory = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="anonymousAlias" value="{{channelSource}}" />
                <add key="private" value="{{channelSource}}" />
                <add key="unrelated" value="https://example.com/unrelated" />
              </packageSources>
              <disabledPackageSources>
                <add key="anonymousAlias" value="true" />
                <add key="private" value="true" />
                <add key="unrelated" value="true" />
              </disabledPackageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, noRestore, options, _) =>
            {
                noRestoreValues.Add(noRestore);
                processOptions.Add(options);
                generatedProject = XDocument.Load(projectFilePath.FullName);
                generatedPolicyOverlay = LoadGeneratedPolicyOverlay(projectFilePath);
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            logger: NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };

        var layout = CreateBundleLayout(workspace);
        var nugetExecutionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
            {
                WriteNuGetConfigOverlayIfRequested(args);
                if (args is ["nuget", "settings", ..])
                {
                    nugetConfigDiscoveryDirectory = GetArgumentValue(args, "--working-dir");
                }
            },
            AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse(
                [ambientConfigPath],
                [
                    ("anonymousAlias", channelSource, false),
                    ("private", channelSource, false),
                    ("unrelated", "https://example.com/unrelated", false)
                ],
                disabledPackageSourceKeys: ["anonymousAlias", "private", "unrelated"],
                credentialSourceKeys: ["private"]))
        };
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(nugetExecutionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance)
        {
            SourceIdentityKeyFactory = static () => s_sourceIdentityKey
        };
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);
            var secondResult = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.True(firstResult.Success);
            Assert.True(secondResult.Success);
            Assert.Equal([false, false], noRestoreValues);
            Assert.All(processOptions, options =>
            {
                Assert.False(options.SuppressLogging);
                Assert.NotNull(options.EnvironmentVariableFilter);
                Assert.True(options.EnvironmentVariableFilter(CliPathHelper.NuGetPackagesEnvironmentVariable));
                Assert.True(options.EnvironmentVariableFilter(PrebuiltAppHostServer.IntegrationHostingVersionPropertyName));
                Assert.True(options.EnvironmentVariableFilter(PrebuiltAppHostServer.IntegrationPackageSourcesPropertyName));
                Assert.False(options.EnvironmentVariableFilter("PATH"));
                Assert.Equal(
                    "13.4.0-pr.17141.gf142085f",
                    options.EnvironmentVariables?[PrebuiltAppHostServer.IntegrationHostingVersionPropertyName]);
                Assert.Equal(
                    channelSource,
                    options.EnvironmentVariables?[PrebuiltAppHostServer.IntegrationPackageSourcesPropertyName]);
                Assert.False(options.EnvironmentVariables?.ContainsKey("RestoreAdditionalProjectSources"));
                Assert.Equal(
                    generatedPolicyOverlay?
                        .Descendants("config")
                        .Elements("add")
                        .Single(element => element.Attribute("key")?.Value == "globalPackagesFolder")
                        .Attribute("value")?
                        .Value,
                    options.EnvironmentVariables?[CliPathHelper.NuGetPackagesEnvironmentVariable]);
            });
            Assert.NotNull(generatedProject);
            Assert.Equal(
                workspace.WorkspaceRoot.FullName,
                nugetConfigDiscoveryDirectory);

            var ns = generatedProject!.Root!.GetDefaultNamespace();
            var restoreConfigFile = generatedProject.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
            var restoreSources = generatedProject.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault()?.Value;
            Assert.Null(restoreConfigFile);
            Assert.True(string.IsNullOrEmpty(restoreSources));
            Assert.True(File.Exists(Path.Combine(
                workspace.WorkspaceRoot.FullName,
                AspireJsonConfiguration.SettingsFolder,
                "NuGet.Config")));
            var directoryBuildProps = XDocument.Load(Path.Combine(
                workingDirectory,
                "integration-restore",
                "Directory.Build.props"));
            Assert.Equal(
                Path.Combine(workspace.WorkspaceRoot.FullName, AspireJsonConfiguration.SettingsFolder),
                directoryBuildProps.Descendants("RestoreRootConfigDirectory").Single().Value);

            Assert.NotNull(generatedPolicyOverlay);
            Assert.Empty(generatedPolicyOverlay.Descendants("packageSources"));
            Assert.Empty(GetPackagePatternsForKey(generatedPolicyOverlay, "anonymousAlias"));
            Assert.Equal(["Aspire*"], GetPackagePatternsForKey(generatedPolicyOverlay, "private"));
            var disabledPackageSources = Assert.Single(generatedPolicyOverlay.Descendants("disabledPackageSources"));
            Assert.NotNull(disabledPackageSources.Element("clear"));
            Assert.Equal(
                ["anonymousAlias", "unrelated"],
                disabledPackageSources
                    .Elements("add")
                    .Select(static source => source.Attribute("key")!.Value));
            Assert.NotNull(XDocument.Load(ambientConfigPath).Descendants("packageSourceCredentials").ElementAtOrDefault(0));

            // Aspire package versions remain in their original (non-pinned) form when no override
            // is in play; the exact-version pinning only fires when a single source is selected.
            var packageElement = Assert.Single(generatedProject.Descendants("PackageReference"));
            Assert.Equal("Aspire.Hosting", packageElement.Attribute("Include")?.Value);
            Assert.Equal("13.4.0-pr.17141.gf142085f", packageElement.Attribute("Version")?.Value);
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithCredentialBearingChannelSource_OmitsProjectSourceHint()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var credentialMarker = $"credential-marker-{Guid.NewGuid():N}";
        var channelSource = $"https://packages.example.invalid/v3/index.json?opaque={credentialMarker}";
        ProcessInvocationOptions? buildOptions = null;

        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName),
            """
            {
              "channel": "daily"
            }
            """);

        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, options, _) =>
            {
                buildOptions = options;
                WriteClosureInputs(
                    projectFilePath.Directory!,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MyIntegration.dll"] = "integration-v1"
                    },
                    ["MyIntegration"]);
                return 0;
            }
        };
        var channel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            logger: NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };
        var server = CreatePrebuiltAppHostServer(
            workspace,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(buildOptions);
            Assert.True(buildOptions.SuppressLogging);
            Assert.NotNull(buildOptions.EnvironmentVariableFilter);
            Assert.True(buildOptions.EnvironmentVariableFilter(
                PrebuiltAppHostServer.IntegrationPackageSourcesPropertyName));
            Assert.False(buildOptions.EnvironmentVariables?.ContainsKey(
                PrebuiltAppHostServer.IntegrationPackageSourcesPropertyName));
            Assert.DoesNotContain(
                buildOptions.EnvironmentVariables?.Values ?? [],
                value => value.Contains(credentialMarker, StringComparison.Ordinal));
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithCredentialBearingAmbientSource_SuppressesRawProcessLogging()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string credentialBearingSource = "https://user:secret@packages.example.com/v3/index.json";
        ProcessInvocationOptions? buildOptions = null;
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, options, _) =>
            {
                buildOptions = options;
                options.StandardErrorCallback?.Invoke(
                    $"NU1301: Unable to load the service index for source {credentialBearingSource}.");
                return 1;
            }
        };

        var layout = CreateBundleLayout(workspace);
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory
            {
                AssertionCallback = (args, _, _, _) => WriteNuGetConfigOverlayIfRequested(args),
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse(
                    sources: [("private", credentialBearingSource, true)]))
            }),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance)
        {
            SourceIdentityKeyFactory = static () => s_sourceIdentityKey
        };
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.False(result.Success);
            Assert.NotNull(buildOptions);
            Assert.True(buildOptions.SuppressLogging);
            var output = string.Join(Environment.NewLine, result.Output!.GetLines().Select(static line => line.Line));
            Assert.DoesNotContain("secret", output);
            Assert.Contains("packages.example.com/v3/index.json", output);
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferencesAndNoMappings_InvalidatesCacheWhenAmbientNuGetConfigChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, """
            <configuration>
              <config>
                <add key="signatureValidationMode" value="accept" />
              </config>
            </configuration>
            """);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
        {
            var args = executionFactory.LastArguments!;
            return Task.FromResult(args is ["nuget", "settings", ..]
                ? (0, (string?)CreateNuGetSettingsResponse(
                    [ambientConfigPath],
                    cacheIdentity: File.ReadAllText(ambientConfigPath)))
                : (0, (string?)null));
        };

        var workingDirectory = GetWorkingDirectory(server);
        try
        {
            var firstResult = await server.PrepareAsync(
                "13.4.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0")]);
            var firstManifestPath = server.IntegrationProbeManifestPath;

            await File.WriteAllTextAsync(ambientConfigPath, """
                <configuration>
                  <config>
                    <add key="signatureValidationMode" value="require" />
                  </config>
                </configuration>
                """);

            var secondResult = await server.PrepareAsync(
                "13.4.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0")]);
            var secondManifestPath = server.IntegrationProbeManifestPath;

            Assert.True(firstResult.Success);
            Assert.True(secondResult.Success);
            Assert.NotNull(firstManifestPath);
            Assert.NotNull(secondManifestPath);
            Assert.NotEqual(firstManifestPath, secondManifestPath);
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferencesAndExplicitChannel_LoadsAmbientConfigAfterPolicyOverlay()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://packages.example.com/v3/index.json";
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="private" value="{{channelSource}}" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
              </packageSourceCredentials>
              <config>
                <add key="signatureValidationMode" value="require" />
              </config>
            </configuration>
            """);

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        XDocument? restoreOverlay = null;
        string[]? restoreConfigPaths = null;
        string? policyOverlayPath = null;
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
        {
            var args = executionFactory.LastArguments!;
            if (args is ["nuget", "settings", ..])
            {
                return Task.FromResult((
                    0,
                    (string?)CreateNuGetSettingsResponse(
                        [ambientConfigPath],
                        [("private", channelSource, true)])));
            }

            if (args is ["nuget", "restore", ..])
            {
                restoreConfigPaths = GetArgumentValues(args, "--nuget-config");
                policyOverlayPath = restoreConfigPaths[0];
                restoreOverlay = XDocument.Load(policyOverlayPath);
            }

            return Task.FromResult((0, (string?)null));
        };

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0")],
                requestedChannel: "daily");

            Assert.True(result.Success);
            Assert.NotNull(policyOverlayPath);
            Assert.Equal([policyOverlayPath, ambientConfigPath], Assert.IsType<string[]>(restoreConfigPaths));
            Assert.NotNull(restoreOverlay);
            Assert.Empty(restoreOverlay.Descendants("packageSources"));
            Assert.Equal(["*", "Aspire*"], GetPackagePatternsForKey(restoreOverlay, "private"));
            var ambientConfig = XDocument.Load(ambientConfigPath);
            Assert.NotNull(ambientConfig.Descendants("packageSourceCredentials").Single().Element("private"));
            Assert.Equal(
                "require",
                ambientConfig.Descendants("config").Elements("add").Single().Attribute("value")?.Value);
            Assert.False(File.Exists(policyOverlayPath));
            Assert.NotNull(server.IntegrationProbeManifestPath);
            Assert.True(Directory.Exists(Path.GetDirectoryName(server.IntegrationProbeManifestPath)));
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(GetWorkingDirectory(server));
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenNuGetConfigChanges_InvalidatesRestoreStamp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, """
            <configuration>
              <packageSources>
                <add key="private" value="https://packages.example.com/v1/index.json" />
              </packageSources>
            </configuration>
            """);

        var noRestoreValues = new List<bool>();
        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, noRestore, _, _) =>
            {
                noRestoreValues.Add(noRestore);
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };
        var layout = CreateBundleLayout(workspace);
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory
            {
                AssertionCallback = (args, _, _, _) => WriteNuGetConfigOverlayIfRequested(args),
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse([ambientConfigPath]))
            }),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);
        var integrations = new[]
        {
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        try
        {
            var firstResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");
            var secondResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");
            await File.WriteAllTextAsync(ambientConfigPath, """
                <configuration>
                  <packageSources>
                    <add key="private" value="https://packages.example.com/v2/index.json" />
                  </packageSources>
                </configuration>
                """);
            var thirdResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");

            Assert.True(firstResult.Success);
            Assert.True(secondResult.Success);
            Assert.True(thirdResult.Success);
            Assert.Equal([false, false, false], noRestoreValues);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_AmbientOnlyRestoreInvalidatesStalePolicyOverlayStamp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, """
            <configuration>
              <packageSources>
                <add key="private" value="https://packages.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        var noRestoreValues = new List<bool>();
        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, noRestore, _, _) =>
            {
                noRestoreValues.Add(noRestore);
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/fake/v3/index.json")],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };
        var layout = CreateBundleLayout(workspace);
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory
            {
                AssertionCallback = (args, _, _, _) => WriteNuGetConfigOverlayIfRequested(args),
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse([ambientConfigPath]))
            }),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);
        var integrations = new[]
        {
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        try
        {
            var firstResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");
            var ambientResult = await server.PrepareAsync("13.4.0", integrations);
            var finalResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");

            Assert.True(firstResult.Success);
            Assert.True(ambientResult.Success);
            Assert.True(finalResult.Success);
            Assert.Equal([false, false, false], noRestoreValues);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithCredentialBearingChannelSource_RedactsProjectRestoreFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://feed.blob.core.windows.net/packages/index.json?sig=secret-sig";
        var buildCalled = false;
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, options, _) =>
            {
                buildCalled = true;
                options.StandardErrorCallback?.Invoke(
                    $"NU1301: Unable to load the service index for source {channelSource}.");
                return 1;
            }
        };
        var channel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };
        var server = CreatePrebuiltAppHostServer(
            workspace,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ],
                requestedChannel: "daily");

            Assert.False(result.Success);
            Assert.True(buildCalled);
            Assert.NotNull(result.Output);
            var output = string.Join(Environment.NewLine, result.Output.GetLines().Select(static line => line.Line));
            Assert.Contains("https://feed.blob.core.windows.net/packages/index.json", output);
            Assert.DoesNotContain("secret-sig", output);
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_WithAutoDiscoveredLocalSource_FooterShowsEffectiveSource()
    {
        // When the caller passes no --source but a local hive is auto-discovered, the failure
        // footer must identify the effective source that participated in restore.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var localHive = workspace.CreateDirectory("local-aspire-hive").FullName;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var prChannel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", localHive)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([prChannel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.DefaultExitCode = 1;

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.12345.gabcdef00",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.12345.gabcdef00")]);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            Assert.Contains($"--source: {localHive}", combined);
            Assert.Contains("channel:  pr-12345", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("https://user:p@ss@host/path", "<unparseable http source>")]
    [InlineData("https://user:p#word@host/", "<unparseable http source>")]
    [InlineData("http://foo bar/path", "<unparseable http source>")]
    [InlineData("HTTPS://user:p@ss@host/path", "<unparseable http source>")]
    [InlineData("/tmp/aspire/some path with [brackets]", "/tmp/aspire/some path with [brackets]")]
    public void RedactSourceForDisplay_FailsClosedForMalformedHttpButPassesThroughLocalPaths(string input, string expected)
    {
        // HTTP-looking inputs that Uri.TryCreate cannot parse (e.g. unescaped @ or # in user-info
        // or embedded whitespace) must return the sentinel rather than risk exposing credentials.
        // Plain non-HTTP inputs pass through because they represent local source paths.
        Assert.Equal(expected, PrebuiltAppHostServer.RedactSourceForDisplay(input));
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferencesAndPackageSourceOverride_UsesPolicyOverlayAndRestoreHints()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSourceOverride = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "aspire-pr-hive",
            "packages");
        XDocument? generatedProject = null;
        XDocument? restoreOverlay = null;
        ProcessInvocationOptions? buildOptions = null;

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, options, _) =>
            {
                generatedProject = XDocument.Load(projectFilePath.FullName);
                restoreOverlay = LoadGeneratedPolicyOverlay(projectFilePath);
                buildOptions = options;
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };
        var server = CreatePrebuiltAppHostServer(workspace, dotNetCliRunner: dotNetCliRunner);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17166.ga49d604d",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17166.ga49d604d"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(generatedProject);

            var ns = generatedProject.Root!.GetDefaultNamespace();
            var restoreConfigFile = generatedProject.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
            Assert.Null(restoreConfigFile);
            Assert.True(string.IsNullOrEmpty(
                generatedProject.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault()?.Value));
            Assert.NotNull(restoreOverlay);
            Assert.Equal(2, restoreOverlay.Descendants("packageSources").Elements("add").Count());
            Assert.Equal(["Aspire*"], GetPackagePatternsForSource(restoreOverlay, packageSourceOverride));
            Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(restoreOverlay, NuGetOrgSource));
            Assert.Equal(
                "13.4.0-pr.17166.ga49d604d",
                buildOptions?.EnvironmentVariables?[PrebuiltAppHostServer.IntegrationHostingVersionPropertyName]);
            Assert.Equal(
                packageSourceOverride,
                buildOptions?.EnvironmentVariables?[PrebuiltAppHostServer.IntegrationPackageSourcesPropertyName]);
            Assert.False(buildOptions?.EnvironmentVariables?.ContainsKey("RestoreAdditionalProjectSources"));

            var packageElement = Assert.Single(generatedProject.Descendants("PackageReference"));
            Assert.Equal("Aspire.Hosting", packageElement.Attribute("Include")?.Value);
            Assert.Equal("[13.4.0-pr.17166.ga49d604d]", packageElement.Attribute("Version")?.Value);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithStagingPinnedProjectOutsideLaunchDirectory_UsesStagingSourcesAndPolicyOverlay()
    {
        const string stagingFeed = "https://example.com/staging/v3/index.json";

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("elsewhere");
        var config = AspireConfigFile.LoadOrCreate(projectDirectory.FullName);
        config.Channel = PackageChannelNames.Staging;
        config.Save(projectDirectory.FullName);

        var layout = CreateBundleLayout(workspace);
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workspace.WorkspaceRoot,
            identityChannel: PackageChannelNames.Stable);

        string[]? restoreInvocation = null;
        IDictionary<string, string>? restoreEnvironment = null;
        string? policyOverlayContent = null;
        var inheritedPackagesFolder = Path.Combine(workspace.WorkspaceRoot.FullName, "inherited-packages");
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, environment, _, _) =>
            {
                WriteNuGetConfigOverlayIfRequested(args);
                if (args.Length > 1 &&
                    args[0] == "nuget" &&
                    args[1] == "restore")
                {
                    restoreInvocation = args.ToArray();
                    restoreEnvironment = environment;
                    policyOverlayContent = File.ReadAllText(GetArgumentValue(args, "--nuget-config"));
                }
            }
        };
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
            Task.FromResult(executionFactory.LastArguments is ["nuget", "settings", ..]
                ? (0, (string?)CreateNuGetSettingsResponse())
                : (executionFactory.DefaultExitCode, (string?)null));

        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(new Dictionary<string, string?>
            {
                [CliPathHelper.NuGetPackagesEnvironmentVariable] = inheritedPackagesFolder
            }),
            NullLogger<BundleNuGetService>.Instance);

        var stagingChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Staging,
            PackageChannelQuality.Both,
            [
                new PackageMapping("Aspire*", stagingFeed),
                new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
            ],
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([stagingChannel])
        };

        var server = CreatePrebuiltAppHostServer(
            workspace,
            appPath: projectDirectory.FullName,
            layout: layout,
            packagingService: packagingService,
            executionContext: executionContext,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")]);

            Assert.True(result.Success);
            Assert.Equal(PackageChannelNames.Staging, result.ChannelName);

            Assert.NotNull(restoreInvocation);
            Assert.Empty(GetSourceArguments(restoreInvocation!));
            Assert.Equal(
                CliPathHelper.StripMacOSFirmlinkPrefix(projectDirectory.FullName),
                CliPathHelper.StripMacOSFirmlinkPrefix(GetArgumentValue(restoreInvocation!, "--working-dir")));
            Assert.NotNull(restoreEnvironment);
            Assert.NotNull(policyOverlayContent);
            Assert.Contains(stagingFeed, policyOverlayContent!);
            Assert.Contains("Aspire*", policyOverlayContent!);
            var globalPackagesFolder = XDocument.Parse(policyOverlayContent!)
                .Descendants("config")
                .Elements("add")
                .Single(element => element.Attribute("key")?.Value == "globalPackagesFolder")
                .Attribute("value")?.Value;
            Assert.NotEqual(inheritedPackagesFolder, globalPackagesFolder);
            Assert.Equal(globalPackagesFolder, restoreEnvironment![CliPathHelper.NuGetPackagesEnvironmentVariable]);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithOnlyProjectReferences_SetsOnlyProjectLayout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };

        var layout = CreateBundleLayout(workspace);
        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], layout: layout);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")]);

            Assert.True(
                result.Success,
                result.Output is null
                    ? null
                    : string.Join(Environment.NewLine, result.Output.GetLines().Select(static line => line.Line)));
            Assert.Null(server.IntegrationProbeManifestPath);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            Assert.True(File.Exists(Path.Combine(layoutPath, "libs", "MyIntegration.dll")));

            var startInfo = server.CreateStartInfo(123);
            Assert.Equal(Path.Combine(layoutPath, "libs"), startInfo.Environment[KnownConfigNames.IntegrationLibsPath]);
            Assert.False(startInfo.Environment.ContainsKey(KnownConfigNames.IntegrationProbeManifestPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_ReusesProjectLayoutWhenClosureIsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstLayoutPath, server.SelectedProjectLayoutPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithMixedReferences_UsesPackageProbeManifestAndCopiesCompleteProjectOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(result.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedLibs = Directory.GetFiles(Path.Combine(layoutPath, "libs"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(layoutPath, "libs"), path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["Aspire.Hosting.Redis.dll", "MyIntegration.dll"], copiedLibs);

            var probeManifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            Assert.Contains(
                Path.Combine(".aspire", "integrations", "package-restore"),
                probeManifestPath,
                StringComparison.OrdinalIgnoreCase);

            var generatedProject = XDocument.Load(Path.Combine(workingDirectory, "integration-restore", PrebuiltAppHostServer.IntegrationProjectFileName));
            var packageReference = Assert.Single(generatedProject.Descendants("PackageReference"));
            Assert.Equal("Aspire.Hosting", packageReference.Attribute("Include")?.Value);
            Assert.Equal("13.2.0", packageReference.Attribute("Version")?.Value);
            Assert.Single(generatedProject.Descendants("ProjectReference"));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_CopiesRestoreAssetsIntoProjectLayout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["fr/Aspire.Hosting.Redis.resources.dll"] = "redis-fr",
            ["runtimes/test-rid/native/testnative.so"] = "native",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = new Dictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/Aspire.Hosting.Redis.dll", "runtime"),
            ["fr/Aspire.Hosting.Redis.resources.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/fr/Aspire.Hosting.Redis.resources.dll", "resources"),
            ["runtimes/test-rid/native/testnative.so"] = ("Aspire.Hosting.Redis", "13.2.0", "runtimes/test-rid/native/testnative.so", "native")
        };

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(result.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedLibs = Directory.GetFiles(Path.Combine(layoutPath, "libs"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(layoutPath, "libs"), path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(
                [
                    "Aspire.Hosting.Redis.dll",
                    "MyIntegration.dll",
                    "fr/Aspire.Hosting.Redis.resources.dll",
                    "runtimes/test-rid/native/testnative.so"
                ],
                copiedLibs);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_CreatesNewProjectLayoutWhenClosureChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);

            closureFiles["MyIntegration.dll"] = "integration-v2";

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);

            var secondLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            Assert.NotEqual(firstLayoutPath, secondLayoutPath);
            Assert.True(Directory.Exists(firstLayoutPath));
            Assert.True(Directory.Exists(secondLayoutPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_RecreatesProjectLayoutWhenCachedLayoutIsCorrupt()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedFilePath = Path.Combine(layoutPath, "libs", "MyIntegration.dll");
            await File.WriteAllTextAsync(copiedFilePath, "corrupt");

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);

            Assert.Equal(layoutPath, server.SelectedProjectLayoutPath);
            Assert.Equal("integration-v1", await File.ReadAllTextAsync(copiedFilePath));
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_DoesNotTouchLockedPreviousProjectLayout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var lockedFilePath = Path.Combine(firstLayoutPath, "libs", "MyIntegration.dll");

            using (var lockedFile = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                closureFiles["MyIntegration.dll"] = "integration-v2";

                var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
                Assert.True(secondResult.Success);

                var secondLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
                Assert.NotEqual(firstLayoutPath, secondLayoutPath);
                Assert.True(File.Exists(lockedFilePath));
                Assert.True(File.Exists(Path.Combine(secondLayoutPath, "libs", "MyIntegration.dll")));
            }
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public void ClosureManifest_WithPackageBackedEntries_ChangesFingerprintWhenPackageSourcePathChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var firstPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-a");
        var secondPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-b");
        var firstSourcePath = Path.Combine(firstPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var secondSourcePath = Path.Combine(secondPackageRoot.FullName, "Aspire.Hosting.Redis.dll");

        File.WriteAllText(firstSourcePath, "redis");
        File.WriteAllText(secondSourcePath, "redis");

        var firstManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                firstSourcePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime")
        ],
        "{}",
        CancellationToken.None);

        var secondManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                secondSourcePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime")
        ],
        "{}",
        CancellationToken.None);

        Assert.NotEqual(firstManifest.ManifestFingerprint, secondManifest.ManifestFingerprint);
    }

    [Fact]
    public void ClosureManifest_ProjectLayoutManifestIncludesPackageBackedEntries()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var firstPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-a");
        var secondPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-b");
        var projectRoot = workspace.WorkspaceRoot.CreateSubdirectory("project");
        var firstPackagePath = Path.Combine(firstPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var secondPackagePath = Path.Combine(secondPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var projectPath = Path.Combine(projectRoot.FullName, "MyIntegration.dll");

        File.WriteAllText(firstPackagePath, "redis");
        File.WriteAllText(secondPackagePath, "redis");
        File.WriteAllText(projectPath, "integration");

        var firstManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                firstPackagePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime"),
            new AppHostServerClosureSource(projectPath, "MyIntegration.dll")
        ],
        "{}",
        CancellationToken.None);

        var secondManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                secondPackagePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime"),
            new AppHostServerClosureSource(projectPath, "MyIntegration.dll")
        ],
        "{}",
        CancellationToken.None);

        Assert.NotEqual(firstManifest.ManifestFingerprint, secondManifest.ManifestFingerprint);
        Assert.NotEqual(firstManifest.ProjectLayoutFingerprint, secondManifest.ProjectLayoutFingerprint);
        Assert.NotEqual(firstManifest.GetProjectLayoutManifestLines(), secondManifest.GetProjectLayoutManifestLines());
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public async Task ReadClosureManifestAsync_PreservesTrailingWhitespaceInPaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var restoreDirectory = workspace.CreateDirectory("restore");
        var sourcePath = Path.Combine(restoreDirectory.FullName, "MyIntegration.dll ");
        var relativePath = "MyIntegration.dll ";
        File.WriteAllText(sourcePath, "integration");
        File.WriteAllLines(
            Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureSourcesFileName),
            [sourcePath]);
        File.WriteAllLines(
            Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureMetadataFileName),
            ["|||"]);
        File.WriteAllLines(
            Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureTargetsFileName),
            [relativePath]);
        var intermediateOutputPath = Path.Combine(restoreDirectory.FullName, "obj");
        WriteProjectAssetsFile(intermediateOutputPath, packageMetadata: null);

        var manifest = await IntegrationClosureBuilder.ReadClosureManifestAsync(
            restoreDirectory.FullName,
            Path.Combine(intermediateOutputPath, IntegrationClosureBuilder.ProjectAssetsFileName),
            "{}",
            ClosureFileMissingBehavior.Throw,
            logger: null,
            CancellationToken.None);

        var entry = Assert.Single(Assert.IsType<AppHostServerClosureManifest>(manifest).Entries);
        Assert.Equal(sourcePath, entry.SourcePath);
        Assert.Equal(relativePath, entry.RelativePath);
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_ReusesProjectLayoutWhenOnlyPackageTimestampChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var packageSourcePath = Path.Combine(workingDirectory, "integration-restore", "closure-sources", "Aspire.Hosting.Redis.dll");
            File.SetLastWriteTimeUtc(packageSourcePath, File.GetLastWriteTimeUtc(packageSourcePath).AddMinutes(5));

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstLayoutPath, server.SelectedProjectLayoutPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    private static IReadOnlyList<IntegrationReference> CreateProjectReferenceIntegrations()
    {
        return
        [
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0"),
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        ];
    }

    private static PrebuiltAppHostServer CreateProjectReferenceServer(
        TemporaryWorkspace workspace,
        IReadOnlyDictionary<string, string> closureFiles,
        IReadOnlyList<string> projectReferenceAssemblyNames,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata = null,
        LayoutConfiguration? layout = null)
    {
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, _, _) =>
            {
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, projectReferenceAssemblyNames, packageMetadata);
                return 0;
            }
        };

        return CreatePrebuiltAppHostServer(workspace, layout: layout, dotNetCliRunner: dotNetCliRunner);
    }

    private static (PrebuiltAppHostServer Server, TestProcessExecutionFactory ExecutionFactory) CreatePackageReferenceServer(TemporaryWorkspace workspace)
    {
        return CreatePackageReferenceServer(workspace, MockPackagingServiceFactory.Create());
    }

    private static (PrebuiltAppHostServer Server, TestProcessExecutionFactory ExecutionFactory) CreatePackageReferenceServer(
        TemporaryWorkspace workspace,
        IPackagingService packagingService,
        CliExecutionContext? executionContext = null)
    {
        var layout = CreateBundleLayout(workspace);
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => WriteNuGetConfigOverlayIfRequested(args)
        };
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
            Task.FromResult(executionFactory.LastArguments is ["nuget", "settings", ..]
                ? (0, (string?)CreateNuGetSettingsResponse())
                : (executionFactory.DefaultExitCode, (string?)null));
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance)
        {
            SourceIdentityKeyFactory = static () => s_sourceIdentityKey
        };

        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            packagingService: packagingService,
            nugetService: nugetService,
            executionContext: executionContext);

        return (server, executionFactory);
    }

    private static LayoutConfiguration CreateBundleLayout(TemporaryWorkspace workspace)
    {
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(
                managedDirectory.FullName,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);

        return new LayoutConfiguration { LayoutPath = layoutRoot.FullName };
    }

    private static void WriteClosureInputs(
        DirectoryInfo restoreDirectory,
        IReadOnlyDictionary<string, string> closureFiles,
        IReadOnlyList<string> projectReferenceAssemblyNames,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata = null)
    {
        var sourceRoot = restoreDirectory.CreateSubdirectory("closure-sources");
        var metadataLines = new List<string>();
        var sourcePaths = new List<string>();
        var targetPaths = new List<string>();

        foreach (var (relativePath, content) in closureFiles.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            var sourcePath = Path.Combine(sourceRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var sourcePathDirectory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(sourcePathDirectory))
            {
                Directory.CreateDirectory(sourcePathDirectory);
            }

            if (!File.Exists(sourcePath) || File.ReadAllText(sourcePath) != content)
            {
                File.WriteAllText(sourcePath, content);
            }

            sourcePaths.Add(sourcePath);
            targetPaths.Add(relativePath.Replace('/', Path.DirectorySeparatorChar));
            metadataLines.Add(packageMetadata is not null && packageMetadata.TryGetValue(relativePath, out var package)
                ? $"{package.NuGetPackageId}|{package.NuGetPackageVersion}|{package.PathInPackage}|{package.AssetType}"
                : "|||");
        }

        WriteProjectAssetsFile(GetIntermediateOutputPath(restoreDirectory), packageMetadata);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureMetadataFileName), metadataLines);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureSourcesFileName), sourcePaths);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureTargetsFileName), targetPaths);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ProjectRefAssemblyNamesFileName), projectReferenceAssemblyNames);
    }

    private static IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)> CreatePackageMetadata()
    {
        return new Dictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/Aspire.Hosting.Redis.dll", "runtime")
        };
    }

    private static string GetIntermediateOutputPath(DirectoryInfo restoreDirectory)
    {
        var document = XDocument.Load(Path.Combine(restoreDirectory.FullName, "Directory.Build.props"));
        return document.Descendants("BaseIntermediateOutputPath").Single().Value;
    }

    private static void WriteProjectAssetsFile(
        string intermediateOutputPath,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata)
    {
        var objDirectory = Directory.CreateDirectory(intermediateOutputPath);
        var libraries = packageMetadata is null
            ? string.Empty
            : string.Join(
                ",\n",
                packageMetadata.Values
                    .GroupBy(static package => (package.NuGetPackageId, package.NuGetPackageVersion))
                    .Select(static group => group.First())
                    .OrderBy(static package => package.NuGetPackageId, StringComparer.Ordinal)
                    .ThenBy(static package => package.NuGetPackageVersion, StringComparer.Ordinal)
                    .Select(static package => $$"""
                        "{{package.NuGetPackageId}}/{{package.NuGetPackageVersion}}": {
                          "sha512": "sha512-{{package.NuGetPackageId}}-{{package.NuGetPackageVersion}}",
                          "type": "package",
                          "path": "{{package.NuGetPackageId.ToLowerInvariant()}}/{{package.NuGetPackageVersion}}",
                          "files": [
                            "{{package.PathInPackage}}"
                          ]
                        }
                        """));

        var projectAssetsContent = $$"""
            {
              "libraries": {
            {{libraries}}
              }
            }
            """;
        File.WriteAllText(Path.Combine(objDirectory.FullName, "project.assets.json"), projectAssetsContent);
    }

    private static string GetWorkingDirectory(PrebuiltAppHostServer server)
    {
        return Assert.IsType<string>(
            typeof(PrebuiltAppHostServer)
                .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(server));
    }

    private static string GetArgumentValue(IReadOnlyList<string> arguments, string optionName)
    {
        var optionIndex = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], optionName, StringComparison.Ordinal))
            {
                optionIndex = i;
                break;
            }
        }

        Assert.True(optionIndex >= 0 && optionIndex < arguments.Count - 1, $"Option '{optionName}' was not found.");
        return arguments[optionIndex + 1];
    }

    private static void WritePackageProbeManifestIfRequested(IReadOnlyList<string> arguments)
    {
        if (arguments is not ["nuget", "manifest", ..])
        {
            return;
        }

        IntegrationPackageProbeManifest.WriteAsync(
            GetArgumentValue(arguments, "--output"),
            IntegrationPackageProbeManifest.Empty).GetAwaiter().GetResult();
    }

    [Fact]
    public void CreateStartInfo_SetsCliLogFilePathEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var layout = CreateBundleLayout(workspace);
        var executionContext = TestExecutionContextFactory.CreateTestContext();
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            executionContext: executionContext,
            nugetService: nugetService);

        var startInfo = server.CreateStartInfo(123);

        Assert.Equal(executionContext.LogFilePath, startInfo.Environment[KnownConfigNames.CliLogFilePath]);
    }

    private static string[] GetSourceArguments(IReadOnlyList<string> args)
    {
        var sources = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--source")
            {
                sources.Add(args[i + 1]);
            }
        }

        return [.. sources];
    }

    private static string[] GetPackageSourcesFromConfigArguments(IReadOnlyList<string> args)
    {
        var configPath = GetArgumentValues(args, "--nuget-config").FirstOrDefault();
        if (configPath is null)
        {
            return [];
        }

        return XDocument.Load(configPath)
            .Descendants("packageSources")
            .Elements("add")
            .Select(static source => source.Attribute("value")!.Value)
            .ToArray();
    }

    private static XDocument LoadGeneratedPolicyOverlay(FileInfo projectFilePath)
    {
        var props = XDocument.Load(Path.Combine(projectFilePath.DirectoryName!, "Directory.Build.props"));
        var policyDirectory = props
            .Descendants("RestoreRootConfigDirectory")
            .Single()
            .Value;
        return XDocument.Load(Path.Combine(policyDirectory, "NuGet.Config"));
    }

    private static string CreateNuGetSettingsResponse(
        IEnumerable<string>? configPaths = null,
        IEnumerable<(string Name, string Source, bool IsEnabled)>? sources = null,
        IEnumerable<(string SourceKey, string[] Patterns)>? packageSourceMappings = null,
        IEnumerable<string>? disabledPackageSourceKeys = null,
        IEnumerable<string>? reservedPackageSourceKeys = null,
        IEnumerable<string>? credentialSourceKeys = null,
        IEnumerable<string>? clientCertificateSourceKeys = null,
        string cacheIdentity = "settings")
    {
        var sourceArray = sources?.ToArray() ?? [];
        var mappingArray = packageSourceMappings?.ToArray() ?? [];
        var disabledSourceKeyArray = disabledPackageSourceKeys?.ToArray() ?? [];
        var credentialSourceKeySet = credentialSourceKeys?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var clientCertificateSourceKeySet = clientCertificateSourceKeys?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var reservedSourceKeyArray = reservedPackageSourceKeys?.ToArray()
            ?? sourceArray
                .Select(static source => source.Name)
                .Concat(mappingArray.Select(static mapping => mapping.SourceKey))
                .Concat(disabledSourceKeyArray)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            ConfigPaths = configPaths?.ToArray() ?? [],
            CacheIdentity = cacheIdentity,
            Sources = sourceArray
                .Select(source => new
                {
                    source.Name,
                    Identity = NuGetSourceIdentity.Compute(source.Source, s_sourceIdentityKey),
                    source.IsEnabled,
                    HasCredentials = credentialSourceKeySet.Contains(source.Name),
                    HasClientCertificates = clientCertificateSourceKeySet.Contains(source.Name)
                })
                .ToArray(),
            SensitiveSourceValues = sourceArray
                .Select(static source => source.Source)
                .Where(NuGetSourceIdentity.HasCredentialMaterial)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            PackageSourceMappingEnabled = mappingArray.Length > 0,
            PackageSourceMappings = mappingArray.Select(static mapping => new
            {
                mapping.SourceKey,
                mapping.Patterns
            }),
            DisabledPackageSourceKeys = disabledSourceKeyArray,
            ReservedPackageSourceKeys = reservedSourceKeyArray
        });
    }

    private static void WriteNuGetConfigOverlayIfRequested(IReadOnlyList<string> args)
    {
        if (args is not ["nuget", "write-config", ..])
        {
            return;
        }

        var request = JsonSerializer.Deserialize<NuGetConfigOverlayInfo>(
            File.ReadAllText(GetArgumentValue(args, "--request")))
            ?? throw new InvalidDataException("The test NuGet configuration request was empty.");
        var configuration = new XElement("configuration");

        if (request.Sources.Length > 0)
        {
            configuration.Add(new XElement(
                "packageSources",
                request.Sources.Select(static source => new XElement(
                    "add",
                    new XAttribute("key", source.Key),
                    new XAttribute("value", source.Source)))));
        }

        if (request.ClearDisabledPackageSources)
        {
            configuration.Add(new XElement(
                "disabledPackageSources",
                new XElement("clear"),
                request.DisabledPackageSourceKeys.Select(static sourceKey => new XElement(
                    "add",
                    new XAttribute("key", sourceKey),
                    new XAttribute("value", "true")))));
        }

        if (request.PackageSourceMappings.Length > 0)
        {
            configuration.Add(new XElement(
                "packageSourceMapping",
                new XElement("clear"),
                request.PackageSourceMappings.Select(static mapping => new XElement(
                    "packageSource",
                    new XAttribute("key", mapping.SourceKey),
                    mapping.Patterns.Select(static pattern => new XElement(
                        "package",
                        new XAttribute("pattern", pattern)))))));
        }

        if (!string.IsNullOrEmpty(request.GlobalPackagesFolder))
        {
            configuration.Add(new XElement(
                "config",
                new XElement(
                    "add",
                    new XAttribute("key", "globalPackagesFolder"),
                    new XAttribute("value", request.GlobalPackagesFolder))));
        }

        var outputPath = GetArgumentValue(args, "--output");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        new XDocument(configuration).Save(outputPath);
    }

    private static string[] GetArgumentValues(IReadOnlyList<string> args, string argumentName)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == argumentName)
            {
                values.Add(args[i + 1]);
            }
        }

        return [.. values];
    }

    private static string[] GetPackagePatternsForSource(XDocument doc, string source)
    {
        var sourceKey = doc.Descendants("packageSources")
            .Elements("add")
            .SingleOrDefault(element => PackageSourceIdentity.Comparer.Equals(
                element.Attribute("value")?.Value,
                source))
            ?.Attribute("key")
            ?.Value;
        sourceKey ??= doc.Descendants("packageSource")
            .Select(static element => element.Attribute("key")?.Value)
            .FirstOrDefault(key => key is not null && PackageSourceIdentity.Comparer.Equals(key, source));
        if (sourceKey is null)
        {
            return [];
        }

        return GetPackagePatternsForKey(doc, sourceKey);
    }

    private static string[] GetPatterns(
        IReadOnlyList<NuGetPackageSourceMappingInfo> mappings,
        string sourceKey)
        => mappings
            .SingleOrDefault(mapping => string.Equals(
                mapping.SourceKey,
                sourceKey,
                StringComparison.OrdinalIgnoreCase))
            ?.Patterns ?? [];

    private static string[] GetPackagePatternsForKey(XDocument doc, string sourceKey)
    {
        return [.. doc.Descendants("packageSource")
            .Where(element => string.Equals(
                element.Attribute("key")?.Value,
                sourceKey,
                StringComparison.OrdinalIgnoreCase))
            .Elements("package")
            .Select(static element => element.Attribute("pattern")?.Value)
            .OfType<string>()];
    }

    private static void DeleteWorkingDirectory(string workingDirectory)
    {
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class FixedLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }

}
