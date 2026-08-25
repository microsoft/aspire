// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
                                        withAppHostReference: withAppHostReference);

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
}

public class Standalone_NewUpAndBuildSupportProjectTemplatesTests(ITestOutputHelper testOutput) : NewUpAndBuildSupportProjectTemplatesBase(testOutput)
{
    [Theory]
    [InlineData("aspire-mstest")]
    [InlineData("aspire-nunit")]
    [InlineData("aspire-xunit")]
    public Task CanNewAndBuildWithoutAppHostReference(string templateName)
    {
        return CanNewAndBuildActual(
            templateName,
            "",
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
