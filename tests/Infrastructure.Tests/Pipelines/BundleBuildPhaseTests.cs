// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace Infrastructure.Tests;

public sealed class BundleBuildPhaseTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("linux-x64", "", false)]
    [InlineData("win-x64", "test", true)]
    [InlineData("win-x64", "test", false)]
    [InlineData("osx-arm64", "", false)]
    [InlineData("osx-arm64", "test", true)]
    [InlineData("osx-arm64", "real", true)]
    public async Task NativeOrchestrationPackagesFinalSignedBytesOnce(string rid, string signType, bool sign)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("native orchestration with spaces").FullName;
        var project = CreateOrchestrationProject(root, rid);
        // Windows publish requires a Windows AOT lane; provide the same prepared
        // outputs on other hosts while still executing the production sign/pack graph.
        var windows = rid.StartsWith("win-", StringComparison.Ordinal);
        if (windows)
        {
            await RunAsync(project, "PublishBundleComponents", rid, "SkipTrayBuild=true");
            var tray = Path.Combine(root, "artifacts", "tray");
            Directory.CreateDirectory(tray);
            File.WriteAllText(Path.Combine(tray, "aspire-tray.exe"), "tray");
            File.WriteAllText(Path.Combine(tray, "Aspire.ico"), "icon");
            File.Delete(Path.Combine(root, "events"));
            File.Delete(Path.Combine(root, "cache-properties"));
        }

        await RunAsync(project, "BuildNativeBundle", rid,
            $"Sign={sign}", $"DotNetSignType={signType}",
            $"SkipManagedBuild={windows}", $"SkipTrayBuild={windows}", "SkipNativeBuild=true",
            "BundleVersion=ci-bundlepayload", "RestorePackagesPath=" + Path.Combine(root, "restore cache"), "PackDcpPackage=true",
            "RequireMacTraySignature=false", "RequireWindowsTraySignature=false");

        var expected = new List<string>();
        if (!windows)
        {
            expected.AddRange(["managed", "dashboard", "terminalhost"]);
        }
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            Assert.Equal(signType == "real" ? "true" : "false",
                File.ReadAllText(Path.Combine(root, "require-tray-signature")).Trim());
            expected.AddRange(["tray", "adhoc"]);
            if (signType == "real")
            {
                expected.Add("prepare-tray");
            }
        }
        if (sign)
        {
            expected.Add("sign-components");
        }
        if (signType == "real")
        {
            expected.AddRange(["sign-tray", "notarize-tray", "restore-tray"]);
        }
        expected.AddRange(["package", "dcp-package", "restore", "layout"]);
        if (!rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            expected.Add("verify");
        }
        expected.Add("cli");
        if (sign)
        {
            expected.Add("sign-archive");
        }
        expected.Add("cli-pack");
        Assert.Equal(expected, File.ReadAllLines(Path.Combine(root, "events")));
        var dashboardBytes = sign ? "signed Dashboard" : "dashboard";
        Assert.Equal(dashboardBytes, File.ReadAllText(Path.Combine(root, "package-dashboard")).Trim());
        Assert.Equal(dashboardBytes, File.ReadAllText(Path.Combine(root, "layout-dashboard")).Trim());
        Assert.Equal(dashboardBytes, File.ReadAllText(Path.Combine(root, "cli-payload")).Trim());
        Assert.Equal(sign ? "signed CLI archive" : "CLI archive", File.ReadAllText(Path.Combine(root, "cli-package")).Trim());
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            Assert.Equal(signType == "real" ? "signed tray" : "tray",
                File.ReadAllText(Path.Combine(root, "layout-tray")).Trim());
            if (sign && !OperatingSystem.IsWindows())
            {
                foreach (var path in new[] { ManagedPath(root, rid), DashboardPath(root, rid), TerminalHostPath(root, rid) })
                {
                    Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
                }
            }
        }
        if (sign)
        {
            Assert.Equal("False", File.ReadAllText(Path.Combine(root, "component-archive-exists")).Trim());
        }
        else
        {
            Assert.False(File.Exists(Path.Combine(root, "component-archive-exists")));
        }
        var cliProperties = File.ReadAllText(Path.Combine(root, "cli-properties")).Trim().Split('|');
        Assert.Equal(["Release", rid, "ci.test", "20261014.1", "staging", ""], cliProperties[..6]);
        var expectedCache = Path.GetFullPath(Path.Combine(root, "restore cache"));
        Assert.Equal(expectedCache, Path.TrimEndingDirectorySeparator(Path.GetFullPath(cliProperties[6])));
        foreach (var line in File.ReadAllLines(Path.Combine(root, "cache-properties")))
        {
            var values = line.Split('|');
            // MSBuild tasks forward cache properties, not an Exec environment.
            var caches = values[0] is "prepare-tray" or "restore-tray" ? values[2..] : values[1..];
            Assert.All(caches, cache =>
                Assert.Equal(expectedCache, Path.TrimEndingDirectorySeparator(Path.GetFullPath(cache))));
        }
    }

    [Theory]
    [InlineData("warning", "tray")]
    [InlineData("adhoc", "tray")]
    [InlineData("sign-components", "prepare-tray")]
    [InlineData("sign-tray", "sign-components")]
    [InlineData("notarize-tray", "sign-tray")]
    [InlineData("restore-tray", "notarize-tray")]
    [InlineData("verify", "layout")]
    [InlineData("cli", "verify")]
    [InlineData("sign-archive", "cli")]
    public async Task NativeOrchestrationStopsOnFailedPhase(string phase, string lastEvent)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        const string rid = "osx-arm64";
        var project = CreateOrchestrationProject(workspace.Path, rid);
        File.WriteAllText(Path.Combine(workspace.Path, "fail-" + phase), "");

        var result = await ExecuteAsync(project, "BuildNativeBundle", rid,
            "Sign=true", "DotNetSignType=real", "BundleVersion=ci-bundlepayload");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(lastEvent, File.ReadAllLines(Path.Combine(workspace.Path, "events"))[^1]);
    }

    [Theory]
    [InlineData("Sign=invalid", "Sign must be true or false.")]
    [InlineData("Sign=true", "Signing requires DotNetSignType")]
    [InlineData("DotNetSignType=invalid", "DotNetSignType must be")]
    [InlineData("DotNetSignType=real", "Real signing requires")]
    [InlineData("RequireMacTraySignature=true", "Tray signature verification requires")]
    [InlineData("VersionSuffix=unsafe&argument", "VersionSuffix must contain")]
    [InlineData("AspireCliChannel=unsafe&argument", "AspireCliChannel must contain")]
    public async Task NativeOrchestrationRejectsInconsistentInputsBeforePublishing(string property, string message)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var project = CreateOrchestrationProject(workspace.Path, "osx-arm64");

        var result = await ExecuteAsync(project, "BuildNativeBundle", "osx-arm64", property);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(message, result.Output);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "events")));
    }

    [Fact]
    public async Task LinuxOfficialIdentityDoesNotEnableSigning()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var project = CreateOrchestrationProject(workspace.Path, "linux-x64");

        var result = await ExecuteAsync(project, "BuildNativeBundle", "linux-x64", "Sign=true", "DotNetSignType=test");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Linux native bundle lanes must disable signing.", result.Output);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "events")));
    }

    [Theory]
    [InlineData("OfficialBuild=false", "Real signing requires an official build identity")]
    [InlineData("ForceDryRunSigning=true", "Real signing requires an official build identity")]
    public async Task RealSigningRejectsDryRunIdentity(string property, string message)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var project = CreateOrchestrationProject(workspace.Path, "osx-arm64");

        var result = await ExecuteAsync(project, "BuildNativeBundle", "osx-arm64",
            "Sign=true", "DotNetSignType=real", property);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(message, result.Output);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "events")));
    }

    [Fact]
    public async Task MissingSigningPluginFailsInSigningChildBeforePackaging()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var project = CreateOrchestrationProject(workspace.Path, "osx-arm64");
        File.Delete(Path.Combine(workspace.Path, "signing-plugin.props"));

        var result = await ExecuteAsync(project, "BuildNativeBundle", "osx-arm64",
            "Sign=true", "DotNetSignType=real");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Signing child requires the MicroBuild plugin.", result.Output);
        Assert.Equal(["managed", "dashboard", "terminalhost", "tray", "adhoc", "prepare-tray"],
            File.ReadAllLines(Path.Combine(workspace.Path, "events")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraySigningValidatesPluginAfterRealPackageImports(bool installed)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The production tray signing validator requires macOS.");
        using var workspace = TemporaryWorkspace.Create(output);
        var plugins = workspace.CreateDirectory("plugins").FullName;
        if (installed)
        {
            // Use MicroBuild Core's documented override discovery, not a global
            // SigningEnabled property that would bypass the package import.
            var build = workspace.CreateDirectory("plugins/MicroBuild.Plugins.Signing/1.0.0/build").FullName;
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup", Property("MicroBuild_SigningEnabled", "true"))))
                .Save(Path.Combine(build, "MicroBuild.Plugins.Signing.props"));
        }
        var archive = Path.Combine(workspace.Path, "AspireTray.zip");
        File.WriteAllText(archive, "prepared tray");
        var project = Path.Combine(RepoRoot.Path, "eng", "pipelines", "SignTray.proj");
        var environment = new Dictionary<string, string?>
        {
            ["MicroBuild_SigningEnabled"] = null,
            ["MicroBuildOverridePluginDirectory"] = plugins,
            ["NUGET_PACKAGES"] = Path.Combine(workspace.Path, "cache")
        };
        string[] properties =
        [
            "RuntimeIdentifier=osx-arm64", "SignType=real", "TraySigningArchive=" + archive,
            "MSBuildProjectExtensionsPath=" + Path.Combine(workspace.Path, "obj") + Path.DirectorySeparatorChar,
            "BaseIntermediateOutputPath=" + Path.Combine(workspace.Path, "obj") + Path.DirectorySeparatorChar,
            "ArtifactsDir=" + Path.Combine(workspace.Path, "artifacts") + Path.DirectorySeparatorChar
        ];
        var restore = await ExecuteAsync(project, "Restore", "osx-arm64", environment, properties);
        Assert.True(restore.ExitCode == 0, restore.Output);

        // Validate only: this must never submit a signing or notarization request.
        var result = await ExecuteAsync(project, "ValidateTraySigning", "osx-arm64", environment, properties);

        Assert.Equal(installed, result.ExitCode == 0);
        if (!installed)
        {
            Assert.Contains("The official MicroBuild signing plugin must be enabled for tray signing.", result.Output);
        }
    }

    [Theory]
    [InlineData("linux-x64", false)]
    [InlineData("linux-arm64", true)]
    [InlineData("osx-arm64", false)]
    public async Task PackagingPreservesPreparedBytesWithoutPublishing(string rid, bool packDcp)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("bundle inputs with spaces").FullName;
        var project = CreateProject(root, rid);

        await RunAsync(project, "PublishBundleComponents", rid, "SkipNativeBuild=true", "SuppressAOT=true");
        var dashboard = DashboardPath(root, rid);
        File.WriteAllText(dashboard, "signed Dashboard");
        await RunAsync(project, "PackageBundlePayload", rid, $"PackDcpPackage={packDcp}", "SkipNativeBuild=true", "SuppressAOT=true");

        Assert.Equal("signed Dashboard", File.ReadAllText(dashboard));
        Assert.Equal("signed Dashboard", File.ReadAllText(Path.Combine(root, "package-dashboard")));
        Assert.Equal("signed Dashboard", File.ReadAllText(Path.Combine(root, "layout-dashboard")));
        var expected = new List<string> { "managed", "dashboard", "terminalhost" };
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            expected.Add("tray");
        }
        expected.Add("package");
        if (packDcp)
        {
            expected.Add("dcp-package");
        }
        expected.AddRange(["restore", "layout"]);
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            expected.Add("verify");
        }
        Assert.Equal(expected,
            File.ReadAllLines(Path.Combine(root, "events")).Select(line => line.Split('|')[0]));

        foreach (var line in File.ReadAllLines(Path.Combine(root, "properties")))
        {
            var values = line.Split('|');
            var runtimeIdentifier = values[0] is "managed" or "dashboard" or "terminalhost" or "tray" ? rid : "";
            Assert.Equal(["Release", rid, "true", "42.0.0", "ci.test", "20261014.1", "staging", "release", "true", "true", "", "", runtimeIdentifier], values[1..]);
        }
        Assert.Equal(rid, File.ReadAllText(Path.Combine(root, "dashboard-platform")).Trim());
        Assert.Equal("true", File.ReadAllText(Path.Combine(root, "package-skip-publish")).Trim());
        Assert.Contains("shared Dashboard publish warning",
            File.ReadAllText(Path.Combine(root, "artifacts", "log", "Release", "PublishNativeDashboard.log")));
    }

    [Theory]
    [InlineData("PublishBundleComponents")]
    [InlineData("PackageBundlePayload")]
    public async Task ExplicitPhasesRejectCiDebugBeforeDoingWork(string target)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var project = CreateProject(workspace.Path, "linux-x64");

        var result = await ExecuteAsync(project, target, "linux-x64", "Configuration=Debug");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("CI native bundles must use Configuration=Release.", result.Output);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "events")));
    }

    [Theory]
    [InlineData("win-x64:win-arm64")]
    [InlineData("win-x64;win-arm64")]
    [InlineData("win-x86")]
    public async Task ExplicitPhasesRejectUnsupportedOrMultipleRids(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var project = CreateProject(workspace.Path, "linux-x64");

        foreach (var target in new[] { "PublishBundleComponents", "PackageBundlePayload" })
        {
            var result = await ExecuteAsync(project, target, rid);
            Assert.NotEqual(0, result.ExitCode);
        }
        Assert.False(File.Exists(Path.Combine(workspace.Path, "events")));
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("dashboard")]
    [InlineData("terminalhost")]
    [InlineData("script")]
    [InlineData("sqlite")]
    public async Task PackagingFailsForMissingPreparedInputRatherThanRepublishing(string missing)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        const string rid = "linux-x64";
        var project = CreateProject(workspace.Path, rid);
        await RunAsync(project, "PublishBundleComponents", rid);
        var file = missing switch
        {
            "managed" => ManagedPath(workspace.Path, rid),
            "dashboard" => DashboardPath(workspace.Path, rid),
            "terminalhost" => TerminalHostPath(workspace.Path, rid),
            "script" => Path.Combine(Path.GetDirectoryName(DashboardPath(workspace.Path, rid))!, "wwwroot", "_framework", "blazor.web.js"),
            _ => Path.Combine(Path.GetDirectoryName(DashboardPath(workspace.Path, rid))!, "libe_sqlite3.so")
        };
        File.Delete(file);
        var before = File.ReadAllLines(Path.Combine(workspace.Path, "events"));

        var result = await ExecuteAsync(project, "PackageBundlePayload", rid);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Prepared bundle input is missing:", result.Output);
        Assert.Equal(before, File.ReadAllLines(Path.Combine(workspace.Path, "events")));
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task WindowsPackagingConsumesSignedPreparedFilesWithoutPublishing(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.Path;
        var project = CreateProject(root, rid);
        var dashboardDirectory = Path.GetDirectoryName(DashboardPath(root, rid))!;
        var tray = Path.Combine(root, "artifacts", "tray", "aspire-tray.exe");
        foreach (var file in new[]
        {
            ManagedPath(root, rid), DashboardPath(root, rid), TerminalHostPath(root, rid),
            Path.Combine(dashboardDirectory, "wwwroot", "_framework", "blazor.web.js"),
            Path.Combine(dashboardDirectory, "e_sqlite3.dll"), tray,
            Path.Combine(root, "artifacts", "tray", "Aspire.ico")
        })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "signed prepared bytes");
        }

        await RunAsync(project, "PackageBundlePayload", rid, "SkipNativeBuild=true");

        Assert.Equal(["package", "restore", "layout", "verify"],
            File.ReadAllLines(Path.Combine(root, "events")));
        Assert.Equal("signed prepared bytes", File.ReadAllText(Path.Combine(root, "package-dashboard")));
        Assert.Equal("signed prepared bytes", File.ReadAllText(Path.Combine(root, "layout-dashboard")));
        Assert.Equal("signed prepared bytes", File.ReadAllText(tray));
        Assert.Equal("signed prepared bytes", File.ReadAllText(Path.Combine(root, "layout-tray")));

        File.Delete(tray);
        var before = File.ReadAllLines(Path.Combine(root, "events"));
        var result = await ExecuteAsync(project, "PackageBundlePayload", rid);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Prepared bundle input is missing:", result.Output);
        Assert.Equal(before, File.ReadAllLines(Path.Combine(root, "events")));
    }

    [Fact]
    public async Task StandaloneBuildRetainsSkipOptionsWithoutProducingPackages()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        const string rid = "linux-x64";
        var project = CreateProject(workspace.Path, rid);
        await RunAsync(project, "Build", rid, "SkipNativeBuild=true");
        Assert.Equal(["managed", "dashboard", "terminalhost", "restore", "layout"],
            File.ReadAllLines(Path.Combine(workspace.Path, "events")).Select(line => line.Split('|')[0]));

        File.Delete(Path.Combine(workspace.Path, "events"));
        await RunAsync(project, "Build", rid, "SkipManagedBuild=true", "SkipNativeBuild=true", "SkipTrayBuild=true");
        Assert.Equal(["restore", "layout"],
            File.ReadAllLines(Path.Combine(workspace.Path, "events")).Select(line => line.Split('|')[0]));
    }

    [Fact]
    public async Task NativeCliSkipStillPublishesTrayAndTraySkipReusesIt()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        const string rid = "osx-arm64";
        var project = CreateProject(workspace.Path, rid);
        await RunAsync(project, "Build", rid, "SkipNativeBuild=true");
        Assert.Equal(["managed", "dashboard", "terminalhost", "tray", "restore", "layout", "verify"],
            File.ReadAllLines(Path.Combine(workspace.Path, "events")));

        File.Delete(Path.Combine(workspace.Path, "events"));
        await RunAsync(project, "Build", rid, "SkipManagedBuild=true", "SkipNativeBuild=true", "SkipTrayBuild=true");
        Assert.Equal(["restore", "layout", "verify"], File.ReadAllLines(Path.Combine(workspace.Path, "events")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ColdCiPublishRestoresAndImportsRealProtobufBuildTargets(bool explicitRestoreCache, bool environmentCache)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.Path;
        const string rid = "linux-x64";
        var project = CreateProject(root, rid);
        var dashboardProject = Path.Combine(root, "ColdProtobufFixture.csproj");
        var generatedDirectory = Path.Combine(root, "obj", "generated");
        // Block ancestor discovery so repository-local workspaces also reproduce
        // CI workspaces outside the repository's central package configuration.
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project />");
        File.WriteAllText(Path.Combine(root, "fixture.proto"), """
            syntax = "proto3";
            option csharp_namespace = "ColdBundle";
            message PreparedComponent { string name = 1; }
            """);
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                Property("BaseIntermediateOutputPath", Path.Combine(root, "obj") + Path.DirectorySeparatorChar),
                Property("DirectoryPackagesPropsPath", Path.Combine(RepoRoot.Path, "Directory.Packages.props"))),
            new XElement("Import", new XAttribute("Project", "Sdk.props"), new XAttribute("Sdk", "Microsoft.NET.Sdk")),
            new XElement("PropertyGroup",
                Property("TargetFramework", "net11.0"),
                Property("Protobuf_OutputPath", generatedDirectory)),
            new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "Grpc.Tools"), new XAttribute("PrivateAssets", "all")),
                new XElement("PackageReference", new XAttribute("Include", "Google.Protobuf")),
                new XElement("Protobuf", new XAttribute("Include", Path.Combine(root, "fixture.proto")),
                    new XAttribute("ProtoRoot", root), new XAttribute("GrpcServices", "None"))),
            new XElement("Import", new XAttribute("Project", "Sdk.targets"), new XAttribute("Sdk", "Microsoft.NET.Sdk")),
            // Execute the real restored Grpc.Tools target without making this regression
            // test compile the full Dashboard or invoke its Native AOT toolchain.
            new XElement("Target", new XAttribute("Name", "Publish"), new XAttribute("DependsOnTargets", "Protobuf_Compile"))))
            .Save(dashboardProject);
        File.WriteAllText(Path.Combine(root, "PreparedComponentConsumer.cs"), """
            using ColdBundle;

            internal static class PreparedComponentConsumer
            {
                public static string GetName() => new PreparedComponent { Name = "prepared" }.Name;
            }
            """);
        var bundle = XDocument.Load(project);
        bundle.Root!.Element("PropertyGroup")!.Element("DashboardProjectPath")!.Value = dashboardProject;
        bundle.Save(project);

        // Let Arcade select its real import root. Hardcoding NuGetPackageRoot
        // would mask a mismatch with RestorePackagesPath, even in a cold cache.
        var environment = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = environmentCache ? Path.Combine(root, "environment cache") : null
        };
        var properties = new List<string> { "RepoRoot=" + root + Path.DirectorySeparatorChar };
        if (explicitRestoreCache)
        {
            properties.Add("RestorePackagesPath=" + Path.Combine(root, "explicit restore cache"));
        }
        var result = await ExecuteAsync(project, "PublishBundleComponents", rid, environment, [.. properties]);

        Assert.True(result.ExitCode == 0, result.Output);
        var build = await ExecuteAsync(dashboardProject, "Build", rid, environment, [.. properties]);
        Assert.True(build.ExitCode == 0, build.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLayoutUsesTheRestoreCacheWhenTheUserDcpCacheIsCold(bool customCache)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.Path;
        const string rid = "linux-x64";
        var project = CreateProject(root, rid);
        await RunAsync(project, "PublishBundleComponents", rid);
        foreach (var file in Aspire.Shared.TerminalHostPayload.GetRequiredFiles(rid))
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(TerminalHostPath(root, rid))!, file), "terminal payload");
        }
        var cache = Path.Combine(root, customCache ? "caller-cache" : ".packages");
        var dcpDirectory = Path.Combine(cache, "microsoft.developercontrolplane.linux-amd64", "1.2.3", "tools");
        Directory.CreateDirectory(dcpDirectory);
        File.WriteAllText(Path.Combine(dcpDirectory, "dcp"), "DCP from the restore cache");
        var bundle = XDocument.Load(project);
        bundle.Root!.Elements("Target").Single(target => target.Attribute("Name")!.Value == "_RunCreateLayout").Remove();
        bundle.Root.Element("PropertyGroup")!.Add(
            Property("CreateLayoutProjectPath", Path.Combine(RepoRoot.Path, "tools", "CreateLayout", "CreateLayout.csproj")));
        bundle.Save(project);
        var home = workspace.CreateDirectory("cold-home").FullName;
        var environment = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = customCache ? cache : null,
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["DOTNET_CLI_HOME"] = home
        };

        var result = await ExecuteAsync(project, "PackageBundlePayload", rid, environment,
            "RepoRoot=" + root + Path.DirectorySeparatorChar, "BundleVersion=cold-cache");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("DCP from the restore cache",
            File.ReadAllText(Path.Combine(root, "artifacts", "bundle", rid, "dcp", "dcp")));
        Assert.True(File.Exists(Path.Combine(root, "artifacts", "bundle", "aspire-cold-cache-linux-x64.tar.gz")));
    }

    private static string CreateProject(string root, string rid)
    {
        var events = Path.Combine(root, "events");
        var properties = Path.Combine(root, "properties");
        var propertyRecord = "$(Configuration)|$(TargetRid)|$(ContinuousIntegrationBuild)|$(VersionPrefix)|$(VersionSuffix)|$(OfficialBuildId)|$(AspireCliChannel)|$(DotNetFinalVersionKind)|$(StabilizePackageVersion)|$(IsOfficialBuild)|$(SkipNativeBuild)|$(SuppressAOT)|$(RuntimeIdentifier)";
        var managed = Path.Combine(root, "managed.proj");
        var dashboard = Path.Combine(root, "dashboard.proj");
        var terminal = Path.Combine(root, "terminal.proj");
        var package = Path.Combine(root, "package.proj");
        var dcp = Path.Combine(root, "dcp.proj");
        var apphost = Path.Combine(root, "apphost.proj");
        var tray = Path.Combine(root, "tray.proj");
        var trayApp = Path.Combine(root, "artifacts", "tray", "Aspire Tray.app");
        var dashboardDirectory = Path.GetDirectoryName(DashboardPath(root, rid))!;

        WriteChild(managed, "Publish", "managed", ManagedPath(root, rid));
        WriteChild(dashboard, "Publish", "dashboard", DashboardPath(root, rid),
            new XElement("Warning", new XAttribute("Text", "shared Dashboard publish warning")),
            new XElement("Warning", new XAttribute("Code", "IL2026"),
                new XAttribute("Text", "Microsoft.AspNetCore.Http.Json.JsonOptions.CreateDefaultTypeResolver()")),
            new XElement("Warning", new XAttribute("Condition", $"Exists('{Path.Combine(root, "fail-warning")}')"),
                new XAttribute("Code", "IL2026"), new XAttribute("Text", "Unexpected trimming warning")),
            new XElement("MakeDir", new XAttribute("Directories", Path.Combine(dashboardDirectory, "Aspire.Dashboard.dSYM"))),
            Write(Path.Combine(root, "dashboard-platform"), "$(PlatformName)"),
            Write(Path.Combine(dashboardDirectory, "wwwroot", "_framework", "blazor.web.js"), "Blazor"),
            Write(Path.Combine(dashboardDirectory, rid.StartsWith("osx-", StringComparison.Ordinal) ? "libe_sqlite3.dylib"
                : rid.StartsWith("win-", StringComparison.Ordinal) ? "e_sqlite3.dll" : "libe_sqlite3.so"), "SQLite"));
        WriteChild(terminal, "Publish", "terminalhost", TerminalHostPath(root, rid));
        WriteChild(package, "Build", "package", null,
            new XElement("Error", new XAttribute("Condition", "'$(SkipDashboardPublish)' != 'true'"), new XAttribute("Text", "Packaging must not publish.")),
            Write(Path.Combine(root, "package-skip-publish"), "$(SkipDashboardPublish)"),
            Copy(DashboardPath(root, rid), Path.Combine(root, "package-dashboard")));
        WriteChild(dcp, "Build", "dcp-package", null);
        WriteChild(apphost, "Restore", "restore", null);
        WriteChild(tray, "Publish", "tray", Path.Combine(trayApp, "Contents", "MacOS", "aspire-tray"),
            Write(Path.Combine(trayApp, "Contents", "Info.plist"), "plist"),
            Write(Path.Combine(trayApp, "Contents", "Resources", "Aspire.icns"), "icon"));

        var projectPath = Path.Combine(root, "bundle.proj");
        new XDocument(new XElement("Project",
            new XElement("Import", new XAttribute("Project", Path.Combine(RepoRoot.Path, "eng", "Bundle.proj"))),
            new XElement("PropertyGroup",
                Property("ArtifactsDir", Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar),
                Property("ArtifactsLogDir", Path.Combine(root, "artifacts", "log", "Release") + Path.DirectorySeparatorChar),
                Property("DashboardPublishLogPath", Path.Combine(root, "artifacts", "log", "Release", "PublishNativeDashboard.log")),
                Property("ManagedPublishDir", Path.GetDirectoryName(ManagedPath(root, rid))!),
                Property("DashboardPublishDir", dashboardDirectory),
                Property("TerminalHostPublishDir", Path.GetDirectoryName(TerminalHostPath(root, rid))!),
                Property("ManagedProjectPath", managed),
                Property("DashboardProjectPath", dashboard),
                Property("TerminalHostProjectPath", terminal),
                Property("DashboardPackProjectPath", package),
                Property("DcpPackProjectPath", dcp),
                Property("TrayProjectPath", tray),
                Property("TrayAppPath", trayApp),
                Property("WindowsTrayPath", Path.Combine(root, "artifacts", "tray")),
                Property("AppHostProjectPath", apphost)),
            // Test-only entry points exercise the production dependency lists in
            // isolation without copying their ordering into the fixture.
            new XElement("Target", new XAttribute("Name", "PublishBundleComponents"),
                new XAttribute("DependsOnTargets", "$(PublishBundleComponentsDependsOn)")),
            new XElement("Target", new XAttribute("Name", "PackageBundlePayload"),
                new XAttribute("DependsOnTargets", "$(PackageBundlePayloadDependsOn)")),
            // Execute the production phase graph and child processes with tiny publish/pack
            // projects. Layout copying substitutes for native execution, not for phase ordering.
            new XElement("Target", new XAttribute("Name", "_RunCreateLayout"),
                Copy(DashboardPath(root, rid), Path.Combine(root, "layout-dashboard")),
                rid.StartsWith("win-", StringComparison.Ordinal)
                    ? Copy(Path.Combine(root, "artifacts", "tray", "aspire-tray.exe"), Path.Combine(root, "layout-tray")) : null,
                Record("layout")),
            new XElement("Target", new XAttribute("Name", "_PublishNativeCli"), new XAttribute("Condition", "'$(SkipNativeBuild)' != 'true'"), Record("cli")),
            new XElement("Target", new XAttribute("Name", "_VerifyNativeTrayArchive"),
                new XAttribute("AfterTargets", "_RunCreateLayout"),
                new XAttribute("Condition", "$(TargetRid.StartsWith('osx-'))"), Record("verify")),
            new XElement("Target", new XAttribute("Name", "_VerifyWindowsTrayArchive"),
                new XAttribute("AfterTargets", "_RunCreateLayout"),
                new XAttribute("Condition", "$(TargetRid.StartsWith('win-'))"), Record("verify")),
            new XElement("Target", new XAttribute("Name", "_CleanCaches")))).Save(projectPath);
        return projectPath;

        XElement Record(string component) => new("WriteLinesToFile",
            new XAttribute("File", events), new XAttribute("Lines", component), new XAttribute("Overwrite", "false"));

        void WriteChild(string path, string target, string component, string? payloadPath, params XElement[] tasks)
        {
            var directories = tasks.Where(task => task.Name == "WriteLinesToFile")
                .Select(task => Path.GetDirectoryName(task.Attribute("File")!.Value)!);
            if (payloadPath is not null)
            {
                directories = directories.Append(Path.GetDirectoryName(payloadPath)!);
            }
            foreach (var directory in directories)
            {
                Directory.CreateDirectory(directory);
            }
            new XDocument(new XElement("Project",
                new XAttribute("DefaultTargets", target),
                target == "Restore" ? null : new XElement("Target", new XAttribute("Name", "Restore")),
                new XElement("Target", new XAttribute("Name", target),
                    Record(component),
                    new XElement("WriteLinesToFile", new XAttribute("File", properties),
                        new XAttribute("Lines", $"{component}|{propertyRecord}"), new XAttribute("Overwrite", "false")),
                    new XElement("WriteLinesToFile", new XAttribute("File", Path.Combine(root, "cache-properties")),
                        new XAttribute("Lines", $"{component}|$(NUGET_PACKAGES)|$(RestorePackagesPath)|$(NuGetPackageRoot)"), new XAttribute("Overwrite", "false")),
                    payloadPath is null ? null : Write(payloadPath, component),
                    tasks))).Save(path);
        }
    }

    private static string CreateOrchestrationProject(string root, string rid)
    {
        var project = CreateProject(root, rid);
        var events = Path.Combine(root, "events");
        var signer = Path.Combine(root, "signer.proj");
        var archive = Path.Combine(root, "aspire-cli-final.tar.gz");
        var tray = Path.Combine(root, "tray.proj");
        var trayExecutable = Path.Combine(root, "artifacts", "tray", "Aspire Tray.app", "Contents", "MacOS", "aspire-tray");
        var trayArchive = Path.Combine(root, "custom signing path", "AspireTray.zip");
        var signingPlugin = Path.Combine(root, "signing-plugin.props");
        // The official job installs plugins, but SigningEnabled is computed
        // only when a signing child imports plugin props, not in Bundle.proj.
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup", Property("MicroBuild_SigningEnabled", "true")))).Save(signingPlugin);
        var bundle = XDocument.Load(project);
        bundle.Root!.Element("PropertyGroup")!.Add(
            Property("BuildScriptPath", Path.Combine(root, OperatingSystem.IsWindows() ? "build.cmd" : "build.sh")),
            Property("TraySigningArchive", trayArchive),
            Property("TraySigningProjectPath", signer));
        var layout = bundle.Root.Elements("Target").Single(target => target.Attribute("Name")!.Value == "_RunCreateLayout");
        layout.Add(Copy(Path.Combine(root, "layout-dashboard"),
            Path.Combine(root, "artifacts", "bundle", "aspire-$(BundleVersion)-$(TargetRid).tar.gz")));
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            layout.Add(Copy(trayExecutable, Path.Combine(root, "layout-tray")));
        }
        // The final CLI wrapper, not _PublishNativeCli, must be the only CLI owner.
        bundle.Root.Add(new XElement("Target", new XAttribute("Name", "_PublishNativeCli"),
            new XElement("Error", new XAttribute("Text", "The CLI must not publish twice."))));
        foreach (var target in bundle.Root.Elements("Target").Where(target =>
            target.Attribute("Name")!.Value is "_VerifyNativeTrayArchive" or "_VerifyWindowsTrayArchive"))
        {
            target.AddFirst(Fail("verify"));
            target.Add(Write(Path.Combine(root, "require-tray-signature"),
                rid.StartsWith("osx-", StringComparison.Ordinal) ? "$(RequireMacTraySignature)" : "$(RequireWindowsTraySignature)"));
        }
        bundle.Save(project);
        var trayProject = XDocument.Load(tray);
        trayProject.Root!.Add(Phase("PrepareTraySigning", "prepare-tray", Copy(trayExecutable, "$(TraySigningArchive)")),
            Phase("RestoreSignedTray", "restore-tray", Copy("$(TraySigningArchive)", "$(TrayAppPath)/Contents/MacOS/aspire-tray")));
        trayProject.Save(tray);
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                Property("ArtifactsBinDir", Path.Combine(root, "artifacts", "bin") + Path.DirectorySeparatorChar),
                Property("ArtifactsPackagesDir", root + Path.DirectorySeparatorChar),
                Property("ArtifactsShippingPackagesDir", Path.Combine(root, "shipping") + Path.DirectorySeparatorChar),
                Property("VisualStudioSetupInsertionPath", Path.Combine(root, "insertion"))),
            new XElement("Import", new XAttribute("Project", signingPlugin),
                new XAttribute("Condition", $"Exists('{signingPlugin}')")),
            // Exercise the production evaluation-time archive glob, not a
            // second synthetic discovery rule that could mask stale evaluation.
            new XElement("Import", new XAttribute("Project", Path.Combine(RepoRoot.Path, "eng", "Signing.props"))),
            new XElement("ItemGroup", new XElement("ArchiveToSign",
                new XAttribute("Include", $"@(ItemsToSign->WithMetadataValue('FullPath', '{archive}'))"))),
            new XElement("Target", new XAttribute("Name", "Restore")),
            new XElement("Target", new XAttribute("Name", "ValidateSigningPlugin"),
                new XAttribute("BeforeTargets", "SignComponents;SignTray;SignArchive"),
                new XAttribute("Condition", "'$(DotNetSignType)' == 'real'"),
                new XElement("Error", new XAttribute("Condition", "'$(MicroBuild_SigningEnabled)' != 'true'"),
                    new XAttribute("Text", "Signing child requires the MicroBuild plugin."))),
            Phase("SignComponents", "sign-components",
                Write(Path.Combine(root, "component-archive-exists"), "$([System.IO.File]::Exists('" + archive + "'))"),
                Write(DashboardPath(root, rid), "signed Dashboard"),
                new XElement("Exec", new XAttribute("Condition", "!$([MSBuild]::IsOSPlatform('Windows')) and $(TargetRid.StartsWith('osx-'))"),
                    new XAttribute("Command", $"chmod 644 \"{ManagedPath(root, rid)}\" \"{DashboardPath(root, rid)}\" \"{TerminalHostPath(root, rid)}\""))),
            Phase("SignTray", "sign-tray", Write("$(TraySigningArchive)", "signed tray")),
            Phase("NotarizeTray", "notarize-tray"),
            Phase("BuildCli", "cli",
                new XElement("Error", new XAttribute("Condition", "'$(SkipNativeBuild)' != ''"),
                    new XAttribute("Text", "Do not inherit SkipNativeBuild.")),
                Copy("$(BundlePayloadPath)", Path.Combine(root, "cli-payload")),
                Write(Path.Combine(root, "cli-properties"), "$(Configuration)|$(TargetRids)|$(VersionSuffix)|$(OfficialBuildId)|$(AspireCliChannel)|$(SkipNativeBuild)|$(RestorePackagesPath)"),
                Write(archive, "CLI archive")),
            Phase("SignArchive", "sign-archive",
                new XElement("Error", new XAttribute("Condition", "'@(ArchiveToSign)' == ''"),
                    new XAttribute("Text", "Fresh signing evaluation must discover the final archive.")),
                Write(archive, "signed CLI archive")),
            Phase("PackCli", "cli-pack", Copy(archive, Path.Combine(root, "cli-package")))))
            .Save(signer);
        File.WriteAllText(Path.Combine(root, "build.ps1"), $$"""
            $ErrorActionPreference = 'Stop'
            $BuildArguments = $args
            $properties = @($BuildArguments | Where-Object { $_.StartsWith('/p:') })
            $project = Join-Path $PSScriptRoot 'signer.proj'
            $targets = if ($BuildArguments -contains '-build') {
                @('BuildCli') + $(if ($BuildArguments -contains '-sign') { 'SignArchive' }) + @('PackCli')
            } else { @('SignComponents') }
            foreach ($target in $targets) {
                & dotnet msbuild $project "-t:$target" @properties
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            }
            """);
        // SignTray's real implementation signs and then notarizes. Keep those
        // operations independently fallible in the small subprocess fixture.
        var signerProject = XDocument.Load(signer);
        signerProject.Root!.Elements("Target").Single(target => target.Attribute("Name")!.Value == "SignTray")
            .Add(new XElement("CallTarget", new XAttribute("Targets", "NotarizeTray")));
        signerProject.Save(signer);
        var tools = Path.Combine(root, "bin");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(root, "codesign.ps1"), $$"""
            $ErrorActionPreference = 'Stop'
            if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'fail-adhoc')) { exit 1 }
            Add-Content -LiteralPath (Join-Path $PSScriptRoot 'events') -Value 'adhoc'
            """);
        WriteLauncher(Path.Combine(root, OperatingSystem.IsWindows() ? "build.cmd" : "build.sh"), Path.Combine(root, "build.ps1"));
        WriteLauncher(Path.Combine(tools, OperatingSystem.IsWindows() ? "codesign.cmd" : "codesign"), Path.Combine(root, "codesign.ps1"));
        return project;

        XElement Fail(string phase) => new("Error", new XAttribute("Condition", $"Exists('{Path.Combine(root, "fail-" + phase)}')"),
            new XAttribute("Text", "Failed " + phase));

        XElement Phase(string name, string phase, params XElement[] tasks) => new("Target",
            new XAttribute("Name", name), Fail(phase),
            new XElement("WriteLinesToFile", new XAttribute("File", events),
                new XAttribute("Lines", phase), new XAttribute("Overwrite", "false")),
            new XElement("WriteLinesToFile", new XAttribute("File", Path.Combine(root, "cache-properties")),
                new XAttribute("Lines", $"{phase}|$(NUGET_PACKAGES)|$(RestorePackagesPath)|$(NuGetPackageRoot)"), new XAttribute("Overwrite", "false")), tasks);

        static void WriteLauncher(string path, string script)
        {
            File.WriteAllText(path, OperatingSystem.IsWindows()
                ? $"@pwsh -NoProfile -File \"{script}\" %*\r\n"
                : $"#!/bin/sh\nexec pwsh -NoProfile -File \"{script}\" \"$@\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static XElement Property(string name, string value) => new(name, value);

    private static XElement Write(string path, string contents) => new("WriteLinesToFile",
        new XAttribute("File", path), new XAttribute("Lines", contents), new XAttribute("Overwrite", "true"));

    private static XElement Copy(string source, string destination) => new("Copy",
        new XAttribute("SourceFiles", source), new XAttribute("DestinationFiles", destination));

    private static string ManagedPath(string root, string rid) =>
        Path.Combine(root, "artifacts", "bin", "Aspire.Managed", "Release", "net10.0", rid, "publish",
            rid.StartsWith("win-", StringComparison.Ordinal) ? "aspire-managed.exe" : "aspire-managed");

    private static string DashboardPath(string root, string rid) =>
        Path.Combine(root, "artifacts", "bin", "Aspire.Dashboard", rid, "Release", "net11.0", rid, "publish",
            rid.StartsWith("win-", StringComparison.Ordinal) ? "Aspire.Dashboard.exe" : "Aspire.Dashboard");

    private static string TerminalHostPath(string root, string rid) =>
        Path.Combine(root, "artifacts", "bin", "Aspire.TerminalHost", "Release", "net11.0", rid, "publish",
            rid.StartsWith("win-", StringComparison.Ordinal) ? "Aspire.TerminalHost.exe" : "Aspire.TerminalHost");

    private async Task RunAsync(string project, string target, string rid, params string[] properties)
    {
        var result = await ExecuteAsync(project, target, rid, properties);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private Task<(int ExitCode, string Output)> ExecuteAsync(string project, string target, string rid, params string[] properties) =>
        ExecuteAsync(project, target, rid, new Dictionary<string, string?>(), properties);

    private async Task<(int ExitCode, string Output)> ExecuteAsync(string project, string target, string rid,
        IReadOnlyDictionary<string, string?> environment, params string[] properties)
    {
        var dotnet = Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Bundle's Exec children must use the same SDK as this MSBuild invocation.
        start.Environment["PATH"] = Path.Combine(Path.GetDirectoryName(project)!, "bin") + Path.PathSeparator
            + Path.GetDirectoryName(dotnet) + Path.PathSeparator + start.Environment["PATH"];
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
        foreach (var argument in new[] { "msbuild", project, "-nologo", $"-t:{target}", "-p:Configuration=Release",
            "-p:ContinuousIntegrationBuild=true", $"-p:TargetRid={rid}", "-p:VersionPrefix=42.0.0",
            "-p:VersionSuffix=ci.test", "-p:OfficialBuildId=20261014.1", "-p:AspireCliChannel=staging",
            "-p:DotNetFinalVersionKind=release", "-p:StabilizePackageVersion=true", "-p:IsOfficialBuild=true" }
            .Concat(properties.Select(property => "-p:" + property)))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var text = await stdout + await stderr;
        output.WriteLine(text);
        return (process.ExitCode, text);
    }
}
