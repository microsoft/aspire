// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Templates.Tests;

public abstract class NewUpAndBuildSupportProjectTemplatesBase(ITestOutputHelper testOutput) : TemplateTestsBase(testOutput)
{
    [Trait("category", "basic-build")]
    protected async Task CanNewAndBuildActual(string templateName, string extraTestCreationArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        var id = GetNewProjectId(prefix: $"new_build_{FixupSymbolName(templateName)}");
        var topLevelDir = Path.Combine(BuildEnvironment.TestRootPath, id + "_root");
        string config = "Debug";

        var buildEnvToUse = sdk switch
        {
            TestSdk.Current => BuildEnvironment.ForCurrentSdkOnly,
            TestSdk.Previous => BuildEnvironment.ForPreviousSdkOnly,
            TestSdk.Next => BuildEnvironment.ForNextSdkOnly,
            TestSdk.NextSdkWithCurrentAndPreviousRuntime => BuildEnvironment.ForNextSdkWithCurrentAndPreviousRuntimes,
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

            var testProjectDir = await CreateAndAddTestTemplateProjectAsync(
                                        id: id,
                                        testTemplateName: templateName,
                                        project: project,
                                        tfm: tfm,
                                        buildEnvironment: buildEnvToUse,
                                        extraArgs: extraTestCreationArgs,
                                        overrideRootDir: topLevelDir);

            await project.BuildAsync(extraBuildArgs: [$"-c {config}"], workingDirectory: testProjectDir);
        }
        catch (ToolCommandException tce) when (error is not null)
        {
            Assert.NotNull(tce.Result);
            Assert.Contains(error, tce.Result.Value.Output);
        }
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
}
