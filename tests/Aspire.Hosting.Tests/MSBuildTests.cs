// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "6")]
public class MSBuildTests
{
    /// <summary>
    /// Tests that when an AppHost has a ProjectReference to a library project, a warning is emitted.
    /// </summary>
    [Fact]
    public void EnsureWarningsAreEmittedWhenProjectReferencingLibraries()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        CreateLibraryProject(tempDirectory.Path, "Library");

        var appHostDirectory = Path.Combine(tempDirectory.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsAspireHost>true</IsAspireHost>

                <!--
                  Test applications have their own way of referencing Aspire.Hosting.AppHost, as well as DCP and Dashboard, so we disable
                  the Aspire.AppHost.SDK targets that will automatically add these references to projects.
                -->
                <SkipAddAspireDefaultReferences Condition="'$(TestsRunningOutsideOfRepo)' != 'true'">true</SkipAddAspireDefaultReferences>
                <AspireHostingSDKVersion>9.0.0</AspireHostingSDKVersion>
                <_AspireUseTaskHostFactory>true</_AspireUseTaskHostFactory>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting.AppHost\Aspire.Hosting.AppHost.csproj" IsAspireProjectResource="false" />

                <ProjectReference Include="..\Library\Library.csproj" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.cs"),
            """
            var builder = DistributedApplication.CreateBuilder();
            builder.Build().Run();
            """);

        CreateDirectoryBuildFiles(appHostDirectory, repoRoot);

        var output = BuildProject(appHostDirectory);

        // Ensure a warning is emitted when an AppHost references a Library project
        Assert.Contains("warning ASPIRE004", output);
    }

    /// <summary>
    /// Tests that the metadata sources are emitted correctly.
    /// </summary>
    [Fact]
    public async Task ValidateMetadataSources()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        CreateAppProject(tempDirectory.Path, "App");

        var appHostDirectory = Path.Combine(tempDirectory.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsAspireHost>true</IsAspireHost>

                <!--
                  Test applications have their own way of referencing Aspire.Hosting.AppHost, as well as DCP and Dashboard, so we disable
                  the Aspire.AppHost.SDK targets that will automatically add these references to projects.
                -->
                <SkipAddAspireDefaultReferences Condition="'$(TestsRunningOutsideOfRepo)' != 'true'">true</SkipAddAspireDefaultReferences>
                <AspireHostingSDKVersion>9.0.0</AspireHostingSDKVersion>
                <_AspireUseTaskHostFactory>true</_AspireUseTaskHostFactory>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting.AppHost\Aspire.Hosting.AppHost.csproj" IsAspireProjectResource="false" />
                <ProjectReference Include="..\App\App.csproj" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.cs"),
            """
            var builder = DistributedApplication.CreateBuilder();
            builder.Build().Run();
            """);

        CreateDirectoryBuildFiles(appHostDirectory, repoRoot);

        var output = BuildProject(appHostDirectory);

        var metadataDirectory = Path.Combine(appHostDirectory, "obj", "Debug", "net8.0", "Aspire", "references");
        var appHostMetadata = await File.ReadAllTextAsync(Path.Combine(metadataDirectory, "_AppHost.ProjectMetadata.g.cs"));
        var appMetadata = await File.ReadAllTextAsync(Path.Combine(metadataDirectory, "App.ProjectMetadata.g.cs"));

        await Verify(new
        {
            AppHost = appHostMetadata,
            App = appMetadata
        }).ScrubLinesWithReplace(line =>
            {
                var temp = tempDirectory?.Path;
                if (temp is not null)
                {
                    line = line.Replace($"/private{temp}", "{AspirePath}") // Handle macOS temp symlinks
                               .Replace(temp, "{AspirePath}")
                               .Replace(Path.DirectorySeparatorChar, '/');
                }
                return line;
            });
    }

    private static void CreateDirectoryBuildFiles(string basePath, string repoRoot)
    {
#if DEBUG
        var config = "Debug";
#else
        var config = "Release";
#endif

        File.WriteAllText(Path.Combine(basePath, "Directory.Build.props"),
        $"""
        <Project>
          <PropertyGroup>
            <SkipAspireWorkloadManifest>true</SkipAspireWorkloadManifest>
            <AspireUseCliBundle>false</AspireUseCliBundle>
            <NoWarn>$(NoWarn);ASPIRE010</NoWarn>
          </PropertyGroup>

          <Import Project="{repoRoot}\src\Aspire.Hosting.AppHost\build\Aspire.Hosting.AppHost.props" />
        </Project>
        """);
        File.WriteAllText(Path.Combine(basePath, "Directory.Build.targets"),
        $"""
        <Project>
          <PropertyGroup>
            <_AspireTasksAssembly>{repoRoot}\artifacts\bin\Aspire.Hosting.Tasks\{config}\net8.0\Aspire.Hosting.Tasks.dll</_AspireTasksAssembly>
          </PropertyGroup>

          <Import Project="{repoRoot}\src\Aspire.Hosting.AppHost\build\Aspire.Hosting.AppHost.in.targets" />
          <Import Project="{repoRoot}\src\Aspire.AppHost.Sdk\SDK\Sdk.in.targets" />
        </Project>
        """);
    }

    private static void CreateLibraryProject(string basePath, string name)
    {
        var libraryDirectory = Path.Combine(basePath,  name);
        Directory.CreateDirectory(libraryDirectory);

        File.WriteAllText(Path.Combine(libraryDirectory, $"{name}.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

            </Project>
            """);
        File.WriteAllText(Path.Combine(libraryDirectory, "Class1.cs"),
            """
            namespace Library;

            public class Class1
            {
            }
            """);
    }

    private static void CreateAppProject(string basePath, string name)
    {
        var appDirectory = Path.Combine(basePath, name);
        Directory.CreateDirectory(appDirectory);

        File.WriteAllText(Path.Combine(appDirectory, $"{name}.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(appDirectory, "Program.cs"),
            """
            Console.WriteLine("Hello, Aspire!");
            """);
    }

    private static string BuildProject(string workingDirectory, IDictionary<string, string>? environment = null)
    {
        var result = BuildProjectCore(workingDirectory, environment);

        Assert.True(result.ExitCode == 0, $"Build failed: {Environment.NewLine}{result.Output}");

        return result.Output;
    }

    private static string BuildProjectWithFailure(string workingDirectory, IDictionary<string, string>? environment = null)
    {
        var result = BuildProjectCore(workingDirectory, environment);

        Assert.NotEqual(0, result.ExitCode);

        return result.Output;
    }

    private static (int ExitCode, string Output) BuildProjectCore(string workingDirectory, IDictionary<string, string>? environment = null)
    {
        return RunDotNet(workingDirectory, "build --disable-build-servers", timeoutMilliseconds: 180_000, environment);
    }

    private static string PackProject(string projectFile, string outputDirectory, string packageId)
    {
        var workingDirectory = Path.GetDirectoryName(projectFile);
        Assert.NotNull(workingDirectory);

        var result = RunDotNet(
            workingDirectory,
            $"pack \"{projectFile}\" --disable-build-servers -o \"{outputDirectory}\"",
            timeoutMilliseconds: 300_000);

        Assert.True(result.ExitCode == 0, $"Pack failed: {Environment.NewLine}{result.Output}");

        return Directory.GetFiles(outputDirectory, $"{packageId}.*.nupkg")
            .Single(path => !path.EndsWith(".snupkg", StringComparison.Ordinal));
    }

    private static string ReadPackageVersion(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspecEntry.Open();
        var document = XDocument.Load(stream);
        var version = document.Root?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "version")?
            .Value;

        Assert.False(string.IsNullOrEmpty(version), $"Could not determine package version from {packagePath}.");

        return version!;
    }

    private static (int ExitCode, string Output) RunDotNet(string workingDirectory, string arguments, int timeoutMilliseconds, IDictionary<string, string>? environment = null)
    {
        var output = new StringBuilder();
        var outputDone = new ManualResetEvent(false);
        using var process = new Process();
        // set '--disable-build-servers' so the MSBuild and Roslyn server processes don't hang around, which may hang the test in CI
        process.StartInfo = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                outputDone.Set();
            }
            else
            {
                output.AppendLine(e.Data);
            }
        };
        process.Start();
        process.BeginOutputReadLine();

        Assert.True(process.WaitForExit(milliseconds: timeoutMilliseconds), $"dotnet {arguments} command timed out.");
        Assert.True(outputDone.WaitOne(millisecondsTimeout: 60_000), "Timed out waiting for output to complete.");

        return (process.ExitCode, output.ToString());
    }

    [Fact]
    public async Task CliBundleDefaultResolvesExplicitBundlePath()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var bundle = CreateFakeCliBundle(tempDirectory.Path);
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliBundlePath>{bundle.LayoutRoot}</AspireCliBundlePath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory);

        var resolvedPaths = await File.ReadAllLinesAsync(Path.Combine(appHostDirectory, "obj", "resolved-aspire-paths.txt"));
        Assert.Equal(bundle.DcpDir, resolvedPaths[0]);
        Assert.Equal(bundle.ManagedDir, resolvedPaths[1]);
        Assert.Equal(bundle.ManagedPath, resolvedPaths[2]);
    }

    [Fact]
    public async Task CliBundleDefaultResolvesNewestVersionedBundlePath()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var layoutRoot = Path.Combine(tempDirectory.Path, "layout");
        _ = CreateFakeCliBundleAtVersionedLayoutRoot(layoutRoot, "13.9.0-preview.1.25301.1_older-aaaaaaaaaaaaaaaa");
        var newestBundle = CreateFakeCliBundleAtVersionedLayoutRoot(layoutRoot, "13.10.0-preview.1.25301.1_newer-bbbbbbbbbbbbbbbb");
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliBundlePath>{layoutRoot}</AspireCliBundlePath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory);

        var resolvedPaths = await File.ReadAllLinesAsync(Path.Combine(appHostDirectory, "obj", "resolved-aspire-paths.txt"));
        Assert.Equal(newestBundle.DcpDir, resolvedPaths[0]);
        Assert.Equal(newestBundle.ManagedDir, resolvedPaths[1]);
        Assert.Equal(newestBundle.ManagedPath, resolvedPaths[2]);
    }

    [Fact]
    public async Task CliBundleDefaultIgnoresTemporaryVersionedBundleDirectories()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var layoutRoot = Path.Combine(tempDirectory.Path, "layout");
        _ = CreateFakeCliBundleAtVersionedLayoutRoot(layoutRoot, "99.0.0-preview.1.tmp.aaaaaaaaaaaaaaaa");
        var newestBundle = CreateFakeCliBundleAtVersionedLayoutRoot(layoutRoot, "13.10.0-preview.1.25301.1_newer-bbbbbbbbbbbbbbbb");
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliBundlePath>{layoutRoot}</AspireCliBundlePath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory);

        var resolvedPaths = await File.ReadAllLinesAsync(Path.Combine(appHostDirectory, "obj", "resolved-aspire-paths.txt"));
        Assert.Equal(newestBundle.DcpDir, resolvedPaths[0]);
        Assert.Equal(newestBundle.ManagedDir, resolvedPaths[1]);
        Assert.Equal(newestBundle.ManagedPath, resolvedPaths[2]);
    }

    [Fact]
    public void CliBundleOptOutEmitsWarning()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            """
              <AspireUseCliBundle>false</AspireUseCliBundle>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        var output = BuildProject(appHostDirectory);

        Assert.Contains("warning ASPIRE010", output);
        Assert.Contains("Some Aspire features may not work when the Aspire CLI bundle is not being used", output);
    }

    [Fact]
    public async Task CliBundleDefaultResolvesAspireHomeBundleForSidecarlessCli()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "bin"));
        var fakeCliPath = CreateFakeAspireCli(fakeCliDirectory.FullName);
        var bundle = CreateFakeCliBundle(Path.Combine(tempDirectory.Path, "aspire-home"));
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliPath>{fakeCliPath}</AspireCliPath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory, new Dictionary<string, string> { ["ASPIRE_HOME"] = bundle.LayoutRoot });

        var resolvedPaths = await File.ReadAllLinesAsync(Path.Combine(appHostDirectory, "obj", "resolved-aspire-paths.txt"));
        Assert.Equal(bundle.DcpDir, resolvedPaths[0]);
        Assert.Equal(bundle.ManagedDir, resolvedPaths[1]);
        Assert.Equal(bundle.ManagedPath, resolvedPaths[2]);
    }

    [Fact]
    public async Task CliBundleDefaultResolvesAspireHomeBundleWithoutCliOnPath()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var bundle = CreateFakeCliBundle(Path.Combine(tempDirectory.Path, "aspire-home"));
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot, additionalProperties: "");

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory, new Dictionary<string, string>
        {
            ["ASPIRE_HOME"] = bundle.LayoutRoot,
            ["PATH"] = GetDotNetOnlyPath()
        });

        var resolvedPaths = await File.ReadAllLinesAsync(Path.Combine(appHostDirectory, "obj", "resolved-aspire-paths.txt"));
        Assert.Equal(bundle.DcpDir, resolvedPaths[0]);
        Assert.Equal(bundle.ManagedDir, resolvedPaths[1]);
        Assert.Equal(bundle.ManagedPath, resolvedPaths[2]);
    }

    [Fact]
    public async Task CliBundleDefaultResolvesDotNetToolStoreBundleFromShimPath()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var toolsDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, ".dotnet", "tools"));
        var shimPath = CreateFakeAspireCli(toolsDirectory.FullName);
        var nativeCliDirectory = Directory.CreateDirectory(Path.Combine(
            toolsDirectory.FullName,
            ".store",
            "aspire.cli",
            "13.4.0",
            OperatingSystem.IsWindows() ? "aspire.cli.win-x64" : "aspire.cli.linux-x64",
            "13.4.0",
            "tools",
            "net10.0",
            OperatingSystem.IsWindows() ? "win-x64" : "linux-x64"));
        _ = CreateFakeAspireCli(nativeCliDirectory.FullName);
        File.WriteAllText(Path.Combine(nativeCliDirectory.FullName, ".aspire-install.json"), """{"source":"dotnet-tool"}""");
        var bundle = CreateFakeCliBundleAtLayoutRoot(nativeCliDirectory.FullName);
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliPath>{shimPath}</AspireCliPath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory);

        var resolvedPaths = await File.ReadAllLinesAsync(Path.Combine(appHostDirectory, "obj", "resolved-aspire-paths.txt"));
        Assert.Equal(bundle.DcpDir, resolvedPaths[0]);
        Assert.Equal(bundle.ManagedDir, resolvedPaths[1]);
        Assert.Equal(bundle.ManagedPath, resolvedPaths[2]);
    }

    [Fact]
    public async Task CliBundleOptInPrefersExistingRepoPathsOverBundlePath()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var bundle = CreateFakeCliBundle(tempDirectory.Path);
        var repoDcpDir = EnsureTrailingSeparator(Path.Combine(tempDirectory.Path, "repo-dcp"));
        var repoDashboardDir = EnsureTrailingSeparator(Path.Combine(tempDirectory.Path, "repo-dashboard"));
        Directory.CreateDirectory(repoDcpDir);
        Directory.CreateDirectory(repoDashboardDir);

        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <DcpDir>{repoDcpDir}</DcpDir>
              <AspireDashboardDir>{repoDashboardDir}</AspireDashboardDir>
              <AspireCliBundlePath>{bundle.LayoutRoot}</AspireCliBundlePath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory);

        var resolvedPaths = await File.ReadAllLinesAsync(Path.Combine(appHostDirectory, "obj", "resolved-aspire-paths.txt"));
        Assert.Equal(repoDcpDir, resolvedPaths[0]);
        Assert.Equal(repoDashboardDir, resolvedPaths[1]);
        Assert.Contains(Path.Combine(repoDashboardDir, "Aspire.Dashboard"), resolvedPaths[2]);
    }

    [Fact]
    public void CliBundleOptInFailsWhenExplicitBundlePathIsInvalid()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var missingBundlePath = Path.Combine(tempDirectory.Path, "missing-bundle");
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliBundlePath>{missingBundlePath}</AspireCliBundlePath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        var output = BuildProjectWithFailure(appHostDirectory);

        Assert.Contains("warning", output);
        Assert.Contains("AspireCliBundlePath", output);
        Assert.Contains(missingBundlePath, output);
        Assert.Contains("ASPIRE009", output);
        Assert.Contains("the bundle could not be resolved", output);
        Assert.Contains("New features require the Aspire CLI to be installed.", output);
        Assert.Contains("https://get.aspire.dev", output);
        Assert.DoesNotContain("DCP path could not be resolved", output);
    }

    [Fact]
    public void CliBundleOptInFailsWhenExplicitCliPathIsInvalid()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var missingCliPath = Path.Combine(tempDirectory.Path, "missing-aspire");
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliPath>{missingCliPath}</AspireCliPath>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        var output = BuildProjectWithFailure(appHostDirectory);

        Assert.Contains("warning", output);
        Assert.Contains("AspireCliPath", output);
        Assert.Contains(missingCliPath, output);
        Assert.Contains("ASPIRE009", output);
    }

    [Fact]
    public async Task CliBundleDefaultAppliesToNonCSharpAppHostProject()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var appHostDirectory = Path.Combine(tempDirectory.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.fsproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>

              <Target Name="WriteAspireUseCliBundleProperty">
                <WriteLinesToFile File="$(BaseIntermediateOutputPath)aspire-use-cli-bundle.txt"
                                  Lines="Value=$(AspireUseCliBundle)"
                                  Overwrite="true" />
              </Target>
            </Project>
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        var result = RunDotNet(appHostDirectory, "msbuild AppHost.fsproj /t:WriteAspireUseCliBundleProperty /v:minimal", timeoutMilliseconds: 180_000);

        Assert.Equal(0, result.ExitCode);
        var property = await File.ReadAllTextAsync(Path.Combine(appHostDirectory, "obj", "aspire-use-cli-bundle.txt"));
        Assert.Equal("Value=true", property.Trim());
    }

    [Fact]
    public async Task CliBundleOptInKeepsSdkProjectReferenceMutation()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        CreateAppProject(tempDirectory.Path, "App");
        var bundle = CreateFakeCliBundle(tempDirectory.Path);
        var appHostDirectory = CreateSdkBundleAppHostProject(tempDirectory.Path, repoRoot,
            $"""
              <AspireCliBundlePath>{bundle.LayoutRoot}</AspireCliBundlePath>
            """,
            """
                <ProjectReference Include="..\App\App.csproj" />
            """);

        CreateAppHostPackageDirectoryBuildFiles(appHostDirectory, repoRoot);

        BuildProject(appHostDirectory);

        var metadataPath = Path.Combine(appHostDirectory, "obj", "Debug", "net8.0", "Aspire", "references", "App.ProjectMetadata.g.cs");
        var appMetadata = await File.ReadAllTextAsync(metadataPath);

        Assert.Contains("class App : global::Aspire.Hosting.IProjectMetadata", appMetadata);
    }

    private static string CreateSdkBundleAppHostProject(string basePath, string repoRoot, string additionalProperties, string additionalProjectReferences = "")
    {
        var appHostDirectory = Path.Combine(basePath, "AppHost");
        Directory.CreateDirectory(appHostDirectory);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsAspireHost>true</IsAspireHost>
                <AspireHostingSDKVersion>9.0.0</AspireHostingSDKVersion>
                <SkipAddAspireDefaultReferences>true</SkipAddAspireDefaultReferences>
                <_AspireUseTaskHostFactory>true</_AspireUseTaskHostFactory>
            {additionalProperties}
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting.AppHost\Aspire.Hosting.AppHost.csproj" IsAspireProjectResource="false" />
            {additionalProjectReferences}
              </ItemGroup>

              <Target Name="WriteResolvedAspirePaths" AfterTargets="GetAssemblyAttributes">
                <WriteLinesToFile File="$(BaseIntermediateOutputPath)resolved-aspire-paths.txt"
                                  Lines="$(DcpDir);$(AspireDashboardDir);$(AspireDashboardPath)"
                                  Overwrite="true" />
              </Target>

            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.cs"),
            """
            var builder = DistributedApplication.CreateBuilder();
            builder.Build().Run();
            """);

        return appHostDirectory;
    }

    private static void CreateAppHostPackageDirectoryBuildFiles(string basePath, string repoRoot)
    {
#if DEBUG
        var config = "Debug";
#else
        var config = "Release";
#endif

        File.WriteAllText(Path.Combine(basePath, "Directory.Build.props"),
        $"""
        <Project>
          <PropertyGroup>
            <SkipAspireWorkloadManifest>true</SkipAspireWorkloadManifest>
          </PropertyGroup>

          <Import Project="{repoRoot}\src\Aspire.Hosting.AppHost\build\Aspire.Hosting.AppHost.props" />
        </Project>
        """);
        File.WriteAllText(Path.Combine(basePath, "Directory.Build.targets"),
        $"""
        <Project>
          <PropertyGroup>
            <_AspireTasksAssembly>{repoRoot}\artifacts\bin\Aspire.Hosting.Tasks\{config}\net8.0\Aspire.Hosting.Tasks.dll</_AspireTasksAssembly>
          </PropertyGroup>

          <Import Project="{repoRoot}\src\Aspire.Hosting.AppHost\build\Aspire.Hosting.AppHost.in.targets" />
          <Import Project="{repoRoot}\src\Aspire.AppHost.Sdk\SDK\Sdk.in.targets" />
        </Project>
        """);
    }

    private static (string LayoutRoot, string DcpDir, string ManagedDir, string ManagedPath) CreateFakeCliBundle(string basePath)
    {
        return CreateFakeCliBundleAtLayoutRoot(Path.Combine(basePath, "layout"));
    }

    private static (string LayoutRoot, string DcpDir, string ManagedDir, string ManagedPath) CreateFakeCliBundleAtLayoutRoot(string layoutRoot)
    {
        return CreateFakeCliBundleRoot(layoutRoot, Path.Combine(layoutRoot, "bundle"));
    }

    private static (string LayoutRoot, string DcpDir, string ManagedDir, string ManagedPath) CreateFakeCliBundleAtVersionedLayoutRoot(string layoutRoot, string versionId)
    {
        return CreateFakeCliBundleRoot(layoutRoot, Path.Combine(layoutRoot, "versions", versionId));
    }

    private static (string LayoutRoot, string DcpDir, string ManagedDir, string ManagedPath) CreateFakeCliBundleRoot(string layoutRoot, string bundleRoot)
    {
        var dcpDir = EnsureTrailingSeparator(Path.Combine(bundleRoot, "dcp"));
        var managedDir = EnsureTrailingSeparator(Path.Combine(bundleRoot, "managed"));
        Directory.CreateDirectory(dcpDir);
        Directory.CreateDirectory(managedDir);

        File.WriteAllText(Path.Combine(dcpDir, OperatingSystem.IsWindows() ? "dcp.exe" : "dcp"), "");
        var managedPath = Path.Combine(managedDir, OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed");
        File.WriteAllText(managedPath, "");

        return (layoutRoot, dcpDir, managedDir, managedPath);
    }

    private static string CreateFakeAspireCli(string directory)
    {
        var cliPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(cliPath, "");
        return cliPath;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private static string GetDotNetOnlyPath()
    {
        var dotnetPath = DotnetFileAppProcess.ResolvedExecutablePath;
        var dotnetDirectory = Path.GetDirectoryName(dotnetPath);

        Assert.False(string.IsNullOrEmpty(dotnetDirectory), $"Could not determine the directory for dotnet path '{dotnetPath}'.");

        return dotnetDirectory;
    }

    /// <summary>
    /// Tests that when TreatProjectReferencesAsResources is set to false,
    /// ProjectReference items are not mutated with Aspire-specific metadata.
    /// </summary>
    [Fact]
    public void TreatProjectReferencesAsResourcesFalse_DisablesMutation()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        CreateLibraryProject(tempDirectory.Path, "Library");

        var appHostDirectory = Path.Combine(tempDirectory.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsAspireHost>true</IsAspireHost>
                <TreatProjectReferencesAsResources>false</TreatProjectReferencesAsResources>

                <!--
                  Test applications have their own way of referencing Aspire.Hosting.AppHost, as well as DCP and Dashboard, so we disable
                  the Aspire.AppHost.SDK targets that will automatically add these references to projects.
                -->
                <SkipAddAspireDefaultReferences Condition="'$(TestsRunningOutsideOfRepo)' != 'true'">true</SkipAddAspireDefaultReferences>
                <AspireHostingSDKVersion>9.0.0</AspireHostingSDKVersion>
                <_AspireUseTaskHostFactory>true</_AspireUseTaskHostFactory>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting.AppHost\Aspire.Hosting.AppHost.csproj" />
                <ProjectReference Include="..\Library\Library.csproj" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.cs"),
            """
            var builder = DistributedApplication.CreateBuilder();
            builder.Build().Run();
            """);

        CreateDirectoryBuildFiles(appHostDirectory, repoRoot);

        var output = BuildProject(appHostDirectory);

        // When TreatProjectReferencesAsResources is false, the Library project should be treated as a normal reference
        // and no ASPIRE004 warning should be emitted since the references are not being mutated
        Assert.DoesNotContain("warning ASPIRE004", output);
    }

    /// <summary>
    /// Tests that when TreatProjectReferencesAsResources is explicitly set to true,
    /// ProjectReference items are mutated with Aspire-specific metadata (same as default).
    /// </summary>
    [Fact]
    public void TreatProjectReferencesAsResourcesTrue_EnablesMutation()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        CreateLibraryProject(tempDirectory.Path, "Library");

        var appHostDirectory = Path.Combine(tempDirectory.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsAspireHost>true</IsAspireHost>
                <TreatProjectReferencesAsResources>true</TreatProjectReferencesAsResources>

                <!--
                  Test applications have their own way of referencing Aspire.Hosting.AppHost, as well as DCP and Dashboard, so we disable
                  the Aspire.AppHost.SDK targets that will automatically add these references to projects.
                -->
                <SkipAddAspireDefaultReferences Condition="'$(TestsRunningOutsideOfRepo)' != 'true'">true</SkipAddAspireDefaultReferences>
                <AspireHostingSDKVersion>9.0.0</AspireHostingSDKVersion>
                <_AspireUseTaskHostFactory>true</_AspireUseTaskHostFactory>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting.AppHost\Aspire.Hosting.AppHost.csproj" IsAspireProjectResource="false" />
                <ProjectReference Include="..\Library\Library.csproj" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.cs"),
            """
            var builder = DistributedApplication.CreateBuilder();
            builder.Build().Run();
            """);

        CreateDirectoryBuildFiles(appHostDirectory, repoRoot);

        var output = BuildProject(appHostDirectory);

        // When TreatProjectReferencesAsResources is explicitly set to true, the mutation should happen
        // and ASPIRE004 warning should be emitted for the Library project reference
        Assert.Contains("warning ASPIRE004", output);
    }

    [Fact]
    public void AspireExportAnalyzersAreDisabledByDefault()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var projectDirectory = Path.Combine(tempDirectory.Path, "MyHostingExtension");
        Directory.CreateDirectory(projectDirectory);

        File.WriteAllText(Path.Combine(projectDirectory, "MyHostingExtension.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting\Aspire.Hosting.csproj" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(projectDirectory, "Extensions.cs"),
            """
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            namespace MyHostingExtension;

            public static class CustomResourceExtensions
            {
                public static IResourceBuilder<ContainerResource> AddCustomContainer(this IDistributedApplicationBuilder builder)
                {
                    return builder.AddContainer("custom", "custom-image");
                }
            }
            """);

        CreateExportAnalyzerDirectoryBuildFiles(projectDirectory, repoRoot);

        var output = BuildProject(projectDirectory);

        Assert.DoesNotContain("warning ASPIREEXPORT008", output);
    }

    [Fact]
    public void AspireExportAnalyzersCanBeEnabledWithMsBuildProperty()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var projectDirectory = Path.Combine(tempDirectory.Path, "MyHostingExtension");
        Directory.CreateDirectory(projectDirectory);

        File.WriteAllText(Path.Combine(projectDirectory, "MyHostingExtension.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting\Aspire.Hosting.csproj" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(projectDirectory, "Extensions.cs"),
            """
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            namespace MyHostingExtension;

            public static class CustomResourceExtensions
            {
                public static IResourceBuilder<ContainerResource> AddCustomContainer(this IDistributedApplicationBuilder builder)
                {
                    return builder.AddContainer("custom", "custom-image");
                }
            }
            """);

        CreateExportAnalyzerDirectoryBuildFiles(projectDirectory, repoRoot, enableAspireIntegrationAnalyzers: true);

        var output = BuildProject(projectDirectory);

        Assert.Contains("warning ASPIREEXPORT008", output);
    }

    [Fact]
    public void AspireIntegrationAnalyzerPackageContainsExpectedAssets()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var packageOutputPath = Path.Combine(tempDirectory.Path, "packages");
        Directory.CreateDirectory(packageOutputPath);

        var packagePath = PackProject(
            Path.Combine(repoRoot, "src", "Aspire.Hosting.Integration.Analyzers", "Aspire.Hosting.Integration.Analyzers.csproj"),
            packageOutputPath,
            "Aspire.Hosting.Integration.Analyzers");

        using var archive = ZipFile.OpenRead(packagePath);

        Assert.Contains(archive.Entries, entry => entry.FullName == "analyzers/dotnet/cs/Aspire.Hosting.Integration.Analyzers.dll");
        Assert.Contains(archive.Entries, entry => entry.FullName == "README.md");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("lib/", StringComparison.Ordinal));

        var nuspecEntry = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var nuspecReader = new StreamReader(nuspecEntry.Open());
        var nuspec = nuspecReader.ReadToEnd();

        Assert.DoesNotContain("<dependency", nuspec, StringComparison.Ordinal);
    }

    [Fact]
    public void AspireIntegrationAnalyzerPackageCanBeConsumedFromLocalSource()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var packageOutputPath = Path.Combine(tempDirectory.Path, "packages");
        Directory.CreateDirectory(packageOutputPath);

        var analyzerPackagePath = PackProject(
            Path.Combine(repoRoot, "src", "Aspire.Hosting.Integration.Analyzers", "Aspire.Hosting.Integration.Analyzers.csproj"),
            packageOutputPath,
            "Aspire.Hosting.Integration.Analyzers");

        var analyzerVersion = ReadPackageVersion(analyzerPackagePath);

        var projectDirectory = Path.Combine(tempDirectory.Path, "MyHostingExtension");
        Directory.CreateDirectory(projectDirectory);
        var nuGetConfigPath = Path.Combine(projectDirectory, "NuGet.config");

        File.WriteAllText(nuGetConfigPath,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{packageOutputPath}" />
                <add key="dotnet-public" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json" />
                <add key="dotnet-eng" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json" />
                <add key="dotnet9" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
                <add key="dotnet10" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/index.json" />
                <add key="dotnet-libraries" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-libraries/nuget/v3/index.json" />
                <add key="dotnet9-transport" value="https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local">
                  <package pattern="Aspire.Hosting.Integration.Analyzers" />
                </packageSource>
                <packageSource key="dotnet9-transport">
                  <package pattern="*WorkloadBuildTasks*" />
                </packageSource>
                <packageSource key="dotnet-public">
                  <package pattern="*" />
                  <package pattern="Microsoft.FluentUI.AspNetCore.Components" />
                  <package pattern="Microsoft.FluentUI.AspNetCore.Components.Icons" />
                </packageSource>
                <packageSource key="dotnet9">
                  <package pattern="*" />
                </packageSource>
                <packageSource key="dotnet10">
                  <package pattern="*" />
                </packageSource>
                <packageSource key="dotnet-libraries">
                  <package pattern="Microsoft.DeveloperControlPlane*" />
                </packageSource>
                <packageSource key="dotnet-eng">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        File.WriteAllText(Path.Combine(projectDirectory, "MyHostingExtension.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <RestoreConfigFile>{nuGetConfigPath}</RestoreConfigFile>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.Integration.Analyzers" Version="{analyzerVersion}" PrivateAssets="all" />
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting\Aspire.Hosting.csproj" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(projectDirectory, "Extensions.cs"),
            """
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            namespace MyHostingExtension;

            public static class CustomResourceExtensions
            {
                public static IResourceBuilder<ContainerResource> AddCustomContainer(this IDistributedApplicationBuilder builder)
                {
                    return builder.AddContainer("custom", "custom-image");
                }
            }
            """);

        var output = BuildProject(projectDirectory);

        Assert.Contains("warning ASPIREEXPORT008", output);
        Assert.DoesNotContain("CS8032", output);
    }

    private static void CreateExportAnalyzerDirectoryBuildFiles(
        string basePath,
        string repoRoot,
        bool enableAspireIntegrationAnalyzers = false)
    {
        File.WriteAllText(Path.Combine(basePath, "Directory.Build.props"),
        $"""
        <Project>
          <PropertyGroup>
            <EnableAspireIntegrationAnalyzers>{enableAspireIntegrationAnalyzers.ToString().ToLowerInvariant()}</EnableAspireIntegrationAnalyzers>
          </PropertyGroup>
        </Project>
        """);
        File.WriteAllText(Path.Combine(basePath, "Directory.Build.targets"),
        $"""
        <Project>
          <ItemGroup Condition="'$(EnableAspireIntegrationAnalyzers)' == 'true'">
            <ProjectReference Include="{repoRoot}\src\Aspire.Hosting.Integration.Analyzers\Aspire.Hosting.Integration.Analyzers.csproj"
                              PrivateAssets="all"
                              ReferenceOutputAssembly="false"
                              OutputItemType="Analyzer"
                              SetTargetFramework="TargetFramework=netstandard2.0" />
          </ItemGroup>

          <Import Project="{repoRoot}\src\Aspire.Hosting\buildTransitive\Aspire.Hosting.targets" />
        </Project>
        """);
    }

    /// <summary>
    /// Tests that when GenerateAssemblyInfo is set to false, a build error is emitted.
    /// </summary>
    [Fact]
    public void GenerateAssemblyInfoFalse_EmitsError()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var appHostDirectory = Path.Combine(tempDirectory.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsAspireHost>true</IsAspireHost>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>

                <!--
                  Test applications have their own way of referencing Aspire.Hosting.AppHost, as well as DCP and Dashboard, so we disable
                  the Aspire.AppHost.SDK targets that will automatically add these references to projects.
                -->
                <SkipAddAspireDefaultReferences Condition="'$(TestsRunningOutsideOfRepo)' != 'true'">true</SkipAddAspireDefaultReferences>
                <AspireHostingSDKVersion>9.0.0</AspireHostingSDKVersion>
                <_AspireUseTaskHostFactory>true</_AspireUseTaskHostFactory>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{repoRoot}\src\Aspire.Hosting.AppHost\Aspire.Hosting.AppHost.csproj" IsAspireProjectResource="false" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDirectory, "AppHost.cs"),
            """
            var builder = DistributedApplication.CreateBuilder();
            builder.Build().Run();
            """);

        CreateDirectoryBuildFiles(appHostDirectory, repoRoot);

        // Build should fail
        var output = new StringBuilder();
        var outputDone = new ManualResetEvent(false);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet", "build --disable-build-servers")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appHostDirectory
        };
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                outputDone.Set();
            }
            else
            {
                output.AppendLine(e.Data);
            }
        };
        process.Start();
        process.BeginOutputReadLine();

        Assert.True(process.WaitForExit(milliseconds: 180_000), "dotnet build command timed out after 3 minutes.");
        Assert.True(outputDone.WaitOne(millisecondsTimeout: 60_000), "Timed out waiting for output to complete.");

        var buildOutput = output.ToString();

        // Build should fail with ASPIRE008 error
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("error ASPIRE008", buildOutput);
        Assert.Contains("GenerateAssemblyInfo", buildOutput);
    }
}
