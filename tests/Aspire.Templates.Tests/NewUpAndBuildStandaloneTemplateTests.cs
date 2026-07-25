// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Templates.Tests;

public class NewUpAndBuildStandaloneTemplateTests(ITestOutputHelper testOutput) : TemplateTestsBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire", ""])]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-starter", ""])]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-ts-cs-starter", ""])]
    [Trait("category", "basic-build")]
    public async Task CanNewAndBuild(string templateName, string extraArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        var id = GetNewProjectId(prefix: $"new_build_{templateName}_{tfm.ToTFMString()}");

        var buildEnvToUse = sdk switch
        {
            TestSdk.Net8 => BuildEnvironment.ForNet8SdkOnly,
            TestSdk.Net9 => BuildEnvironment.ForNet9SdkOnly,
            TestSdk.Net10 => BuildEnvironment.ForNet10SdkOnly,
            TestSdk.Net11 => BuildEnvironment.ForNet11SdkOnly,
            TestSdk.Net11WithAllSupportedRuntimes => BuildEnvironment.ForNet11SdkWithAllSupportedRuntimes,
            _ => throw new ArgumentOutOfRangeException(nameof(sdk))
        };

        try
        {
            await using var project = await AspireProject.CreateNewTemplateProjectAsync(
                id,
                templateName,
                _testOutput,
                buildEnvironment: buildEnvToUse,
                extraArgs: extraArgs,
                targetFramework: tfm);

            Assert.True(error is null, $"Expected to throw an exception with message: {error}");

            if (templateName == "aspire-starter")
            {
                await AssertStarterAspNetCoreTemplateContentAsync(project, tfm);
            }

            await project.BuildAsync(extraBuildArgs: [$"-c Debug"]);
        }
        catch (ToolCommandException tce) when (error is not null)
        {
            Assert.NotNull(tce.Result);
            Assert.Contains(error, tce.Result.Value.Output);
        }
    }

    private static async Task AssertStarterAspNetCoreTemplateContentAsync(AspireProject project, TestTargetFramework tfm)
    {
        var webProjectDirectory = Path.Combine(project.RootDir, $"{project.Id}.Web");

        var appContent = await File.ReadAllTextAsync(Path.Combine(webProjectDirectory, "Components", "App.razor"));
        Assert.Equal(tfm == TestTargetFramework.Net11, appContent.Contains("<BasePath />", StringComparison.Ordinal));
        Assert.Equal(tfm != TestTargetFramework.Net11, appContent.Contains("<base href=\"/\" />", StringComparison.Ordinal));

        var importsContent = await File.ReadAllTextAsync(Path.Combine(webProjectDirectory, "Components", "_Imports.razor"));
        Assert.Equal(tfm == TestTargetFramework.Net11, importsContent.Contains("@using Microsoft.AspNetCore.Components.Endpoints", StringComparison.Ordinal));

        var errorPageContent = await File.ReadAllTextAsync(Path.Combine(webProjectDirectory, "Components", "Pages", "Error.razor"));
        Assert.Equal(tfm is TestTargetFramework.Net10 or TestTargetFramework.Net11, errorPageContent.Contains("[PersistentState]", StringComparison.Ordinal));

        var navMenuContent = await File.ReadAllTextAsync(Path.Combine(webProjectDirectory, "Components", "Layout", "NavMenu.razor"));
        var expectedNavMenuScript = tfm == TestTargetFramework.Net8
            ? "<script type=\"module\" src=\"Components/Layout/NavMenu.razor.js\"></script>"
            : "<script type=\"module\" src=\"@Assets[\"Components/Layout/NavMenu.razor.js\"]\"></script>";
        Assert.Contains(expectedNavMenuScript, navMenuContent, StringComparison.Ordinal);
        Assert.Contains("id=\"nav-scrollable\"", navMenuContent, StringComparison.Ordinal);
        Assert.False(navMenuContent.Contains("onclick=", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(webProjectDirectory, "Components", "Layout", "NavMenu.razor.js")));
    }
}
