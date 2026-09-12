// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
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

            var testProjectDir = await CreateAndAddTestTemplateProjectAsync(
                                        id: id,
                                        testTemplateName: templateName,
                                        project: project,
                                        tfm: tfm,
                                        buildEnvironment: buildEnvToUse,
                                        extraArgs: extraTestCreationArgs,
                                        overrideRootDir: topLevelDir);

            AssertTestFrameworkPackages(testProjectDir, templateName, extraTestCreationArgs);
            await project.BuildAsync(extraBuildArgs: [$"-c {config}"], workingDirectory: testProjectDir);
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
        var packageReferences = XDocument.Load(projectPath)
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
