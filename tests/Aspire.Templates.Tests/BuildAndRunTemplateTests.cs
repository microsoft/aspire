// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

// This class has tests that start projects on their own
public partial class BuildAndRunTemplateTests : TemplateTestsBase
{
    public BuildAndRunTemplateTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {}

    public static TheoryData<string> BuildConfigurationsForTestData()
    {
        var data = new TheoryData<string>() { "Debug" };
        if (!PlatformDetection.IsRunningPRValidation)
        {
            data.Add("Release");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BuildConfigurationsForTestData))]
    [RequiresFeature(TestFeature.SSLCertificate), RequiresFeature(TestFeature.Playwright)]
    [Trait("category", "basic-build")]
    [OuterloopTest("playwright test")]
    public async Task BuildAndRunAspireTemplate(string config)
    {
        string id = GetNewProjectId(prefix: $"aspire_{config}");
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(id, "aspire", _testOutput, buildEnvironment: BuildEnvironment.ForDefaultFramework);

        await project.BuildAsync(extraBuildArgs: [$"-c {config}"]);
        await project.StartAppHostAsync(extraArgs: [$"-c {config}"]);

        await using var context = await CreateNewBrowserContextAsync();
        var page = await project.OpenDashboardPageAsync(context);
        await CheckDashboardHasResourcesAsync(page, [], logPath: project.LogPath);
    }

    [Fact]
    public async Task BuildAndRunAspireTemplateWithCentralPackageManagement()
    {
        string id = GetNewProjectId(prefix: "aspire_CPM");
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            id,
            "aspire",
            _testOutput,
            buildEnvironment: BuildEnvironment.ForDefaultFramework);

        string version = ExtractVersionFromSdkAndAddRedisPackageReference(project);

        CreateCPMFile(project, version);

        await project.BuildAsync();
        await project.StartAppHostAsync();
        await project.StopAppHostAsync();

        static string ExtractVersionFromSdkAndAddRedisPackageReference(AspireProject project)
        {
            var projectName = Directory.GetFiles(project.AppHostProjectDirectory, "*.csproj").FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(projectName));

            var projectContents = File.ReadAllText(projectName);

            var match = ProjectSdkVersionRegex().Match(projectContents);

            File.WriteAllText(
                projectName,
                ProjectClosingTagRegex().Replace(projectContents,
                """
                  <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                  </ItemGroup>
                </Project>
                """)
            );

            var version = match.Groups[1].Value;
            Assert.NotNull(version);

            return version;
        }

        static void CreateCPMFile(AspireProject project, string version)
        {
            var cpmFilePath = Path.Combine(project.RootDir, "Directory.Packages.props");
            var cpmContent = $"""
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <!-- Do not warn for not using package source mapping when using CPM -->
                    <NoWarn>NU1507;$(NoWarn)</NoWarn>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="{version}" />
                  </ItemGroup>
                </Project>
                """;

            File.WriteAllText(cpmFilePath, cpmContent);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildAndRunAspireTemplateWithExplicitSdkReference(bool includeAspireHostingAppHostPackageReference)
    {
        string id = GetNewProjectId(prefix: "aspire_explicit_SDK");
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            id,
            "aspire",
            _testOutput,
            buildEnvironment: BuildEnvironment.ForDefaultFramework);

        UpdateSdkReferencesAndAddAppHostPackageReference(project, includeAspireHostingAppHostPackageReference);

        await project.BuildAsync();
        await project.StartAppHostAsync();
        await project.StopAppHostAsync();

        static void UpdateSdkReferencesAndAddAppHostPackageReference(AspireProject project, bool addPackageRef)
        {
            var projectName = Directory.GetFiles(project.AppHostProjectDirectory, "*.csproj").FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(projectName));

            var projectContents = File.ReadAllText(projectName);

            var match = ProjectSdkVersionRegex().Match(projectContents);
            var version = match.Groups[1].Value;
            Assert.NotNull(version);

            File.WriteAllText(
                projectName,
                ProjectSdkVersionRegex().Replace(projectContents,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <Sdk Name="Aspire.AppHost.Sdk" Version="{version}" />
                """)
            );

            if (addPackageRef)
            {
                File.WriteAllText(
                    projectName,
                    ProjectClosingTagRegex().Replace(projectContents,
                    $"""
                      <ItemGroup>
                        <PackageReference Include="Aspire.Hosting.AppHost" Version="{version}" />
                      </ItemGroup>
                    </Project>
                    """));
            }
        }
    }

    [Theory]
    [MemberData(nameof(BuildConfigurationsForTestData))]
    [RequiresFeature(TestFeature.SSLCertificate), RequiresFeature(TestFeature.Playwright)]
    [Trait("category", "basic-build")]
    [OuterloopTest("playwright test")]
    public async Task StarterTemplateNewAndRunWithoutExplicitBuild(string config)
    {
        var id = GetNewProjectId(prefix: $"aspire_starter_run_{config}");
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            id,
            "aspire-starter",
            _testOutput,
            buildEnvironment: BuildEnvironment.ForDefaultFramework);

        await using var context = await CreateNewBrowserContextAsync();
        await AssertStarterTemplateRunAsync(context, project, config, _testOutput);
    }

    [Fact]
    [RequiresFeature(TestFeature.Playwright)]
    [OuterloopTest("playwright test")]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/9155", typeof(PlatformDetection), nameof(PlatformDetection.IsMacOS))]
    public async Task ProjectWithNoHTTPSRequiresExplicitOverrideWithEnvironmentVariable()
    {
        string id = GetNewProjectId(prefix: "aspire");
        // Using a copy so envvars can be modified without affecting other tests
        var testSpecificBuildEnvironment = new BuildEnvironment(BuildEnvironment.ForDefaultFramework);

        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            id,
            "aspire",
            _testOutput,
            buildEnvironment: testSpecificBuildEnvironment,
            extraArgs: "--no-https");

        await project.BuildAsync();
        using var buildCmd = new DotNetCommand(_testOutput, buildEnv: testSpecificBuildEnvironment, label: "first-run")
                                    .WithWorkingDirectory(project.AppHostProjectDirectory);

        var res = await buildCmd.ExecuteAsync("run");
        Assert.True(res.ExitCode != 0, $"Expected the app run to fail");
        Assert.Contains($"setting must be an https address unless the '{KnownConfigNames.AllowUnsecuredTransport}'", res.Output);

        // Run with the environment variable set
        testSpecificBuildEnvironment.EnvVars[KnownConfigNames.AllowUnsecuredTransport] = "true";
        await project.StartAppHostAsync();

        await using var context = await CreateNewBrowserContextAsync();
        var page = await project.OpenDashboardPageAsync(context);
        await CheckDashboardHasResourcesAsync(page, [], logPath: project.LogPath);
    }

    [Theory]
    [InlineData("9.*-*")]
    [InlineData("[9.0.0]")]
    [Trait("category", "basic-build")]
    public async Task CreateAndModifyAspireAppHostTemplate(string version)
    {
        string id = GetNewProjectId(prefix: $"aspire_apphost_{version.Replace("*", "wildcard").Replace("[", "").Replace("]", "")}");
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(id, "aspire-apphost", _testOutput, buildEnvironment: BuildEnvironment.ForDefaultFramework, addEndpointsHook: false);

        ModifyProjectFile(project, version);

        await project.BuildAsync(workingDirectory: project.RootDir);

        static void ModifyProjectFile(AspireProject project, string version)
        {
            var projectName = Directory.GetFiles(project.RootDir, "*.csproj").FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(projectName));

            var projectContents = File.ReadAllText(projectName);

            var modifiedContents = AppHostVersionRegex().Replace(projectContents, $@"<PackageReference Include=""Aspire.Hosting.AppHost"" Version=""{version}"" />");

            File.WriteAllText(projectName, modifiedContents);
        }
    }

    [GeneratedRegex(@"<PackageReference\s+Include=""Aspire\.Hosting\.AppHost""\s+Version=""([^""]+)""\s+/>")]
    private static partial Regex AppHostVersionRegex();

    [GeneratedRegex(@"</Project>")]
    private static partial Regex ProjectClosingTagRegex();

    [GeneratedRegex(@"<Project\s+Sdk=""Aspire\.AppHost\.Sdk\/([^""]+)"">")]
    private static partial Regex ProjectSdkVersionRegex();
}
