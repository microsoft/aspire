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
            TestSdk.Current => BuildEnvironment.ForCurrentSdkOnly,
            TestSdk.Previous => BuildEnvironment.ForPreviousSdkOnly,
            TestSdk.Next => BuildEnvironment.ForNextSdkOnly,
            TestSdk.NextSdkWithCurrentAndPreviousRuntime => BuildEnvironment.ForNextSdkWithCurrentAndPreviousRuntimes,
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

            await project.BuildAsync(extraBuildArgs: [$"-c Debug"]);
        }
        catch (ToolCommandException tce) when (error is not null)
        {
            Assert.NotNull(tce.Result);
            Assert.Contains(error, tce.Result.Value.Output);
        }
    }
}
