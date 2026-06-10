// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

public abstract class StarterTemplateProjectNamesTests : TemplateTestsBase
{
    private readonly string _testType;
    public StarterTemplateProjectNamesTests(string testType, ITestOutputHelper testOutput)
        : base(testOutput)
    {
        _testType = testType;
    }

    public static TheoryData<string> ProjectNames_TestData()
        => new(GetProjectNamesForTest());

    [Theory]
    [MemberData(nameof(ProjectNames_TestData))]
    [RequiresFeature(TestFeature.SSLCertificate)]
    [RequiresFeature(TestFeature.Playwright)]
    [OuterloopTest("playwright test")]
    public async Task StarterTemplateWithTest_ProjectNames(string prefix)
    {
        string id = $"{prefix}-{_testType}";
        string config = "Debug";

        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            id,
            "aspire-starter",
            _testOutput,
            BuildEnvironment.ForDefaultFramework,
            $"-t {_testType}");

        await using var context = await CreateNewBrowserContextAsync();
        _testOutput.WriteLine($"Checking the starter template project");
        await AssertStarterTemplateRunAsync(context, project, config, _testOutput);

        _testOutput.WriteLine($"Checking the starter template project tests");
        await AssertTestProjectRunAsync(project.TestsProjectDirectory, _testType, _testOutput, config);
    }
}

// Individual class for each test framework so the tests can run in separate helix jobs
public class None_StarterTemplateProjectNamesTests : StarterTemplateProjectNamesTests
{
    public None_StarterTemplateProjectNamesTests(ITestOutputHelper testOutput) : base("none", testOutput)
    {
    }
}

public class MSTest_StarterTemplateProjectNamesTests : StarterTemplateProjectNamesTests
{
    public MSTest_StarterTemplateProjectNamesTests(ITestOutputHelper testOutput) : base("mstest", testOutput)
    {
    }
}

public class Xunit_StarterTemplateProjectNamesTests : StarterTemplateProjectNamesTests
{
    public Xunit_StarterTemplateProjectNamesTests(ITestOutputHelper testOutput) : base("xunit.net", testOutput)
    {
    }
}

public class Nunit_StarterTemplateProjectNamesTests : StarterTemplateProjectNamesTests
{
    public Nunit_StarterTemplateProjectNamesTests(ITestOutputHelper testOutput) : base("nunit", testOutput)
    {
    }
}
