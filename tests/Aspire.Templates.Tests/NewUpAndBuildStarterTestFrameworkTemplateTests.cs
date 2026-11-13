// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Templates.Tests;

// CI shards this project by concrete test class. Keep each SDK/TFM matrix at the original
// standalone class's 51 cases so adding framework coverage does not exhaust its 20-minute budget.
public class NewUpAndBuildStarterTestFrameworkTemplateTests(ITestOutputHelper testOutput) : NewUpAndBuildStandaloneTemplateTestsBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-starter", "--test-framework MSTest"])]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-starter", "--test-framework NUnit"])]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-starter", "--test-framework xUnit.net"])]
    [Trait("category", "basic-build")]
    public Task CanNewAndBuild(string templateName, string extraArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraArgs, sdk, tfm, error);
    }
}

public class NewUpAndBuildStarterXUnitVersionTemplateTests(ITestOutputHelper testOutput) : NewUpAndBuildStandaloneTemplateTestsBase(testOutput)
{
    [Theory]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-starter", "--test-framework xUnit.net --xunit-version v2"])]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-starter", "--test-framework xUnit.net --xunit-version v3"])]
    [MemberData(nameof(TestDataForNewAndBuildTemplateTests), arguments: ["aspire-starter", "--test-framework xUnit.net --xunit-version v3mtp"])]
    [Trait("category", "basic-build")]
    public Task CanNewAndBuild(string templateName, string extraArgs, TestSdk sdk, TestTargetFramework tfm, string? error)
    {
        return CanNewAndBuildActual(templateName, extraArgs, sdk, tfm, error);
    }
}
