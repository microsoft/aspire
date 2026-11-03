// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Templates.Tests;

public class DashboardResourceTests(ITestOutputHelper testOutput) : TemplateTestsBase(testOutput)
{
    [Theory]
    [InlineData("apiservice", "Running", "Example.ApiService.csproj", null)]
    [InlineData("apiservice", "Failed to start", "Example.ApiService.csproj", "State for apiservice")]
    [InlineData("apiservice", "Finished", "Example.ApiService.csproj", "State for apiservice")]
    [InlineData("apiservice", "Running", "Wrong.ApiService.csproj", "Example.ApiService.csproj")]
    [InlineData("__dotnet-project-build", "Failed", "build.proj", "Row with unknown name found")]
    [RequiresFeature(TestFeature.Playwright)]
    public async Task CheckDashboardHasResourcesValidatesCards(string resourceName, string state, string source, string? expectedError)
    {
        PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
        await using var context = await CreateNewBrowserContextAsync();
        var page = await context.NewPageWithLoggingAsync(_testOutput);
        await page.Page.SetContentAsync($"""
            <section class="resource-card-grid">
                <article class="resource-card" aria-label="{resourceName}">
                    <header><span class="resource-card-title">{resourceName}</span></header>
                    <div class="state-column-cell">{state}</div>
                    <span class="resource-card-source">{source}</span>
                </article>
                <article class="resource-card" aria-label="cache">
                    <div class="state-column-cell">Running</div>
                    <span class="resource-card-source">docker.io/library/redis:7</span>
                </article>
            </section>
            """);

        ResourceRow[] expected =
        [
            new("Project", "apiservice", "Running", "Example.ApiService.csproj"),
            new("Container", "cache", "Running", "docker.io/library/redis:7")
        ];
        var logDirectory = Directory.CreateTempSubdirectory();
        try
        {
            if (expectedError is null)
            {
                var resources = await CheckDashboardHasResourcesAsync(page, expected, logDirectory.FullName, timeoutSecs: 5);
                Assert.Equal(expected, resources);
            }
            else
            {
                var error = await Assert.ThrowsAnyAsync<XunitException>(() =>
                    CheckDashboardHasResourcesAsync(page, expected, logDirectory.FullName, timeoutSecs: 5));
                Assert.Contains(expectedError, error.Message);
            }
        }
        finally
        {
            logDirectory.Delete(recursive: true);
        }
    }
}
