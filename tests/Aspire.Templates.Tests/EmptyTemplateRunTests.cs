// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

public class EmptyTemplateRunTests : TemplateTestsBase, IClassFixture<EmptyTemplateRunFixture>
{
    private readonly EmptyTemplateRunFixture _testFixture;

    public EmptyTemplateRunTests(EmptyTemplateRunFixture fixture, ITestOutputHelper testOutput)
        : base(testOutput)
    {
        _testFixture = fixture;
    }

    [Fact]
    [RequiresFeature(TestFeature.Playwright)]
    [RequiresFeature(TestFeature.SSLCertificate)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcesShowUpOnDashboad()
    {
        await using var context = await CreateNewBrowserContextAsync();
        await CheckDashboardHasResourcesAsync(
            await _testFixture.Project!.OpenDashboardPageAsync(context),
            [],
            timeoutSecs: 1_000,
            logPath: _testFixture.Project.LogPath);
    }
}
