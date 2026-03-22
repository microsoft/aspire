// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

public class BuildAndRunStarterTemplateBuiltInTest : TemplateTestsBase
{
    public BuildAndRunStarterTemplateBuiltInTest(ITestOutputHelper testOutput)
        : base(testOutput)
    {}

    public static TheoryData<string, string> TestFrameworkTypeWithConfig()
    {
        var data = new TheoryData<string, string>();
        foreach (var testType in TemplateTestsBase.TestFrameworkTypes)
        {
            data.Add("Debug", testType);
            if (!PlatformDetection.IsRunningPRValidation)
            {
                data.Add("Release", testType);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(TestFrameworkTypeWithConfig))]
    [RequiresFeature(TestFeature.Docker | TestFeature.SSLCertificate)]
    [Trait("category", "basic-build")]
    public async Task BuildAndRunStarterTemplateBuiltInTest_Test(string config, string testType)
    {
        string id = TemplateTestsBase.GetNewProjectId(prefix: $"starter test.{config}-{testType.Replace(".", "_")}");
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
                                            id,
                                            "aspire-starter",
                                            _testOutput,
                                            buildEnvironment: BuildEnvironment.ForDefaultFramework,
                                            extraArgs: $"-t {testType}");

        await TemplateTestsBase.AssertTestProjectRunAsync(project.TestsProjectDirectory, testType, _testOutput, config);
    }
}
