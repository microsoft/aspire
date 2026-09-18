// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Xunit;

namespace Aspire.Templates.Tests;

public abstract class NewUpAndBuildSupportProjectTemplatesBase(ITestOutputHelper testOutput) : TemplateTestsBase(testOutput)
{
    [Trait("category", "basic-build")]
    protected async Task CanNewAndBuildActual(
        string templateName,
        string extraTestCreationArgs,
        TestSdk sdk,
        TestTargetFramework tfm,
        string? error,
        string? appHostDirectoryNamePrefix = null,
        bool withAppHostReference = true,
        bool useAppHostTargetFramework = true,
        bool runTests = false)
    {
        var id = GetNewProjectId(prefix: $"new_build_{FixupSymbolName(templateName)}");
        var topLevelDir = Path.Combine(BuildEnvironment.TestRootPath, id + "_root");
        string config = "Debug";

        var buildEnvToUse = sdk switch
        {
            TestSdk.Net8 => BuildEnvironment.ForNet8SdkOnly,
            TestSdk.Net9 => BuildEnvironment.ForNet9SdkOnly,
            TestSdk.Net10 => BuildEnvironment.ForNet10SdkOnly,
            TestSdk.Net11 => BuildEnvironment.ForNet11SdkOnly,
            TestSdk.Net11WithAllSupportedRuntimes => BuildEnvironment.ForNet11SdkWithAllSupportedRuntimes,
            _ => throw new ArgumentOutOfRangeException(nameof(sdk))
        };

        if (Directory.Exists(topLevelDir))
        {
            Directory.Delete(topLevelDir, recursive: true);
        }
        Directory.CreateDirectory(topLevelDir);

        try
        {
            await using var project = await AspireProject.CreateNewTemplateProjectAsync(
                id: id + ".AppHost",
                template: "aspire-apphost",
                testOutput: _testOutput,
                buildEnvironment: buildEnvToUse,
                targetFramework: tfm,
                addEndpointsHook: false,
                overrideRootDir: topLevelDir);
            project.AppHostProjectDirectory = Path.Combine(topLevelDir, id + ".AppHost");
            if (appHostDirectoryNamePrefix is not null)
            {
                var specialAppHostDirectory = Path.Combine(topLevelDir, GetNewProjectId(appHostDirectoryNamePrefix));
                Directory.Move(project.AppHostProjectDirectory, specialAppHostDirectory);
                project.AppHostProjectDirectory = specialAppHostDirectory;
            }

            var testProjectDir = await CreateAndAddTestTemplateProjectAsync(
                                        id: id,
                                        testTemplateName: templateName,
                                        project: project,
                                        tfm: tfm,
                                        buildEnvironment: buildEnvToUse,
                                        extraArgs: extraTestCreationArgs,
                                        overrideRootDir: topLevelDir,
                                        withAppHostReference: withAppHostReference,
                                        useAppHostTargetFramework: useAppHostTargetFramework);

            AssertTestFrameworkPackages(testProjectDir, templateName, extraTestCreationArgs);
            await project.BuildAsync(extraBuildArgs: [$"-c {config}"], workingDirectory: testProjectDir);
            if (runTests)
            {
                using var testCommand = new DotNetCommand(_testOutput, buildEnv: buildEnvToUse, label: $"test-{templateName}")
                    .WithWorkingDirectory(testProjectDir)
                    .WithTimeout(TimeSpan.FromMinutes(3));

                var testResult = await testCommand.ExecuteAsync($"test -c {config} --no-build");

                Assert.Equal(0, testResult.ExitCode);
                Assert.Matches("Passed! * - Failed: *0, Passed: *1, Skipped: *0, Total: *1", testResult.Output);
            }
        }
        catch (ToolCommandException tce) when (error is not null)
        {
            Assert.NotNull(tce.Result);
            Assert.Contains(error, tce.Result.Value.Output);
        }
    }

    private static void AssertTestFrameworkPackages(string testProjectDir, string templateName, string extraTestCreationArgs)
    {
        var projectPath = Directory.EnumerateFiles(testProjectDir, "*.csproj").Single();
        var project = XDocument.Load(projectPath);
        var packageReferences = project
            .Descendants("PackageReference")
            .Where(element => element.Attribute("Include")?.Value != "Aspire.Hosting.Testing")
            .Select(element => $"{element.Attribute("Include")?.Value}/{element.Attribute("Version")?.Value}")
            .OrderBy(packageReference => packageReference)
            .ToArray();

        string[] expectedPackageReferences = (templateName, extraTestCreationArgs) switch
        {
            ("aspire-mstest", _) =>
            [
                "MSTest/4.4.0",
            ],
            ("aspire-nunit", _) =>
            [
                "coverlet.collector/10.0.1",
                "Microsoft.NET.Test.Sdk/18.10.0",
                "NUnit/4.6.1",
                "NUnit.Analyzers/4.14.0",
                "NUnit3TestAdapter/6.3.0",
            ],
            ("aspire-xunit", "--xunit-version v3mtp") =>
            [
                "xunit.v3/4.0.0",
            ],
            ("aspire-xunit", "--xunit-version v3") =>
            [
                "coverlet.collector/10.0.1",
                "Microsoft.NET.Test.Sdk/18.10.0",
                "xunit.runner.visualstudio/4.0.0",
                "xunit.v3.mtp-off/4.0.0",
            ],
            ("aspire-xunit", _) =>
            [
                "coverlet.collector/10.0.1",
                "Microsoft.NET.Test.Sdk/18.10.0",
                "xunit/2.9.3",
                "xunit.runner.visualstudio/4.0.0",
            ],
            _ => throw new InvalidOperationException($"Unexpected test template '{templateName}'."),
        };

        Assert.Equal(expectedPackageReferences.OrderBy(packageReference => packageReference), packageReferences);

        if (templateName == "aspire-xunit" && extraTestCreationArgs is "--xunit-version v3" or "--xunit-version v3mtp")
        {
            Assert.Equal("Exe", project.Descendants("OutputType").Single().Value);
        }
    }
}

public class Wired_NewUpAndTestSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [InlineData("aspire-mstest", "")]
    [InlineData("aspire-nunit", "")]
    [InlineData("aspire-xunit", "--xunit-version v2")]
    [InlineData("aspire-xunit", "--xunit-version v3mtp")]
    public Task CanNewAndTestWithAppHostReference(string templateName, string extraTestCreationArgs)
    {
        return CanNewAndBuildActual(
            templateName,
            extraTestCreationArgs,
            TestSdk.Net10,
            TestTargetFramework.Net10,
            error: null,
            runTests: true);
    }

    [Fact]
    public Task AppHostTargetFrameworkOverridesExplicitFramework()
    {
        return CanNewAndBuildActual(
            "aspire-mstest",
            "--framework net11.0",
            TestSdk.Net10,
            TestTargetFramework.Net10,
            error: null,
            runTests: true);
    }

    [Theory]
    [InlineData("aspire-mstest")]
    [InlineData("aspire-nunit")]
    [InlineData("aspire-xunit")]
    public Task MissingAppHostTargetFrameworkFallsBackToExplicitFramework(string templateName)
    {
        return CanNewAndBuildActual(
            templateName,
            "",
            TestSdk.Net11WithAllSupportedRuntimes,
            TestTargetFramework.Net9,
            error: null,
            useAppHostTargetFramework: false,
            runTests: true);
    }

    [Theory]
    [InlineData("aspire-mstest")]
    [InlineData("aspire-nunit")]
    [InlineData("aspire-xunit")]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public Task CanNewAndBuildWithQuoteInAppHostPath(string templateName)
    {
        return CanNewAndBuildActual(
            templateName,
            "",
            TestSdk.Net10,
            TestTargetFramework.Net10,
            error: null,
            appHostDirectoryNamePrefix: "AppHost_\"quoted");
    }

    [Theory]
    [InlineData("aspire-mstest")]
    [InlineData("aspire-nunit")]
    [InlineData("aspire-xunit")]
    public async Task AppHostTargetFrameworkAcceptsPlatformQualifiedFramework(string templateName)
    {
        var buildEnvironment = BuildEnvironment.ForNet10SdkOnly;
        var id = GetNewProjectId(prefix: $"platform_tfm_{FixupSymbolName(templateName)}");
        var topLevelDir = Path.Combine(BuildEnvironment.TestRootPath, id + "_root");
        var testProjectName = $"{id}.Tests";
        var testProjectDir = Path.Combine(topLevelDir, testProjectName);

        if (Directory.Exists(topLevelDir))
        {
            Directory.Delete(topLevelDir, recursive: true);
        }
        Directory.CreateDirectory(topLevelDir);

        try
        {
            using var newTestCmd = new DotNetNewCommand(
                _testOutput,
                label: $"platform-tfm-{templateName}",
                buildEnv: buildEnvironment)
                .WithWorkingDirectory(topLevelDir);

            var appHostProjectPath = Path.Combine("..", "AppHost", "AppHost.csproj");
            var result = await newTestCmd.ExecuteAsync(
                $"{templateName} -o \"{testProjectName}\" --no-restore " +
                $"--WithAppHostReference true --AppHostProjectPath \"{appHostProjectPath}\" " +
                "--AppHostProjectName AppHost --AppHostTargetFramework net10.0-windows");
            result.EnsureSuccessful();

            var testProjectPath = Assert.Single(Directory.EnumerateFiles(testProjectDir, "*.csproj"));
            var testProject = XDocument.Load(testProjectPath);
            Assert.Equal("net10.0-windows", Assert.Single(testProject.Descendants("TargetFramework")).Value);
        }
        finally
        {
            if (Directory.Exists(topLevelDir))
            {
                Directory.Delete(topLevelDir, recursive: true);
            }
        }
    }
}

public class Standalone_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [InlineData("aspire-mstest", "")]
    [InlineData("aspire-nunit", "")]
    [InlineData("aspire-xunit", "")]
    [InlineData("aspire-xunit", "--xunit-version v3mtp")]
    public Task CanNewAndBuildWithoutAppHostReference(string templateName, string extraTestCreationArgs)
    {
        return CanNewAndBuildActual(
            templateName,
            extraTestCreationArgs,
            TestSdk.Net10,
            TestTargetFramework.Net10,
            error: null,
            withAppHostReference: false);
    }
}

public class NUnit_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-nunit", ""])]
    public Task CanNewAndBuild(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraTestCreationArgs, sdk, tfm, error);
    }
}

public class XUnit_Default_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-xunit", ""])]
    public Task CanNewAndBuild(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraTestCreationArgs, sdk, tfm, error);
    }
}

public class XUnit_V2_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-xunit", "--xunit-version v2"])]
    public Task CanNewAndBuild(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraTestCreationArgs, sdk, tfm, error);
    }
}

public class XUnit_V3_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-xunit", "--xunit-version v3"])]
    public Task CanNewAndBuild(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraTestCreationArgs, sdk, tfm, error);
    }
}

public class XUnit_V3MTP_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-xunit", "--xunit-version v3mtp"])]
    public Task CanNewAndBuild(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraTestCreationArgs, sdk, tfm, error);
    }
}

public class XUnit_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-xunit", ""])]
    public Task CanNewAndBuild(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraTestCreationArgs, sdk, tfm, error);
    }
}

public class MSTest_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-mstest", ""])]
    public Task CanNewAndBuild(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraTestCreationArgs, sdk, tfm, error);
    }

    [Fact]
    public Task CanNewAndBuildWithMSBuildSpecialCharactersInAppHostPath()
    {
        return CanNewAndBuildActual(
            "aspire-mstest",
            "",
            TestSdk.Net10,
            TestTargetFramework.Net10,
            error: null,
            appHostDirectoryNamePrefix: "AppHost_$(literal);100%@'&");
    }
}
