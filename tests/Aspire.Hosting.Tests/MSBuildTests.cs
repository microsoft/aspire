// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

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

    private static string BuildProject(string workingDirectory)
    {
        var output = new StringBuilder();
        var outputDone = new ManualResetEvent(false);
        using var process = new Process();
        // set '--disable-build-servers' so the MSBuild and Roslyn server processes don't hang around, which may hang the test in CI
        process.StartInfo = new ProcessStartInfo("dotnet", "build --disable-build-servers")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
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
        Assert.True(process.ExitCode == 0, $"Build failed: {Environment.NewLine}{output}");

        Assert.True(outputDone.WaitOne(millisecondsTimeout: 60_000), "Timed out waiting for output to complete.");

        return output.ToString();
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
