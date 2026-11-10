// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Templates.Tests;

public static class DashboardResourceAssertions
{
    public static async Task<ResourceRow[]> CheckResourcesAsync(
        WrapperForIPage dashboardPageWrapper,
        IEnumerable<ResourceRow> expectedResources,
        ITestOutputHelper testOutput,
        int timeoutSecs)
    {
        testOutput.WriteLine("Waiting for resources to appear on the dashboard");
        var page = dashboardPageWrapper.Page;
        var originalUrl = page.Url;
        var expectedRows = expectedResources.ToDictionary(resource => resource.Name);
        var foundRows = new Dictionary<string, ResourceRow>();
        var reloadAttempts = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSecs));

        while (foundRows.Count < expectedRows.Count && !cts.IsCancellationRequested)
        {
            if (dashboardPageWrapper.HasErrors)
            {
                if (reloadAttempts >= 3)
                {
                    throw new InvalidOperationException($"Failed to load dashboard page after {reloadAttempts} attempts");
                }

                testOutput.WriteLine("Reloading dashboard page");
                await dashboardPageWrapper.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });
                reloadAttempts++;
            }

            // Summary and Graph do not list resources. The sidebar retains names and states at every density,
            // but source metadata is only consistently available in the individual resource's status panel.
            foreach (var row in await page.Locator(".resource-row[data-resource-name]").AllAsync())
            {
                var name = (await row.Locator(".resource-row-name").InnerTextAsync()).Trim();
                if (!expectedRows.TryGetValue(name, out var expected))
                {
                    Assert.Fail($"Row with unknown name found: '{name}'. Expected values: {string.Join(", ", expectedRows.Keys)}");
                }
                if (foundRows.ContainsKey(name))
                {
                    continue;
                }

                var state = await GetRowStateAsync(row);
                if (state is null)
                {
                    continue;
                }
                if (expected.State != state && state != "Finished" && !state.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Assert.Equal(expected.State, state);

                var resourceName = await row.GetAttributeAsync("data-resource-name");
                Assert.NotNull(resourceName);
                await dashboardPageWrapper.GotoAsync(new Uri(new Uri(originalUrl), $"/resources/{Uri.EscapeDataString(resourceName)}").AbsoluteUri);
                var source = page.Locator(".overview-properties dd.overview-mono");
                await Assertions.Expect(source).ToBeVisibleAsync();
                Assert.Contains(expected.SourceContains, await source.GetAttributeAsync("data-tooltip"));

                foundRows.Add(name, expected);
            }

            if (foundRows.Count < expectedRows.Count)
            {
                await Task.Delay(500);
            }
        }

        if (foundRows.Count != expectedRows.Count)
        {
            Assert.Fail($"Expected rows not found: {string.Join(", ", expectedRows.Keys.Except(foundRows.Keys))}");
        }

        if (page.Url != originalUrl)
        {
            await dashboardPageWrapper.GotoAsync(originalUrl);
        }

        return foundRows.Values.ToArray();
    }

    // Compact rows, the dashboard's default, show the state as a dot whose name is in the tooltip of its column.
    // Comfortable and spacious rows show the state's name as text after the dot.
    private const string CompactStateSelector = ".resource-row-meta-state";
    private const string StateTextSelector = ".resource-row-sub > .state-dot + span";

    /// <summary>
    /// Gets the state a resource row shows, at any density, or <see langword="null"/> if the row hasn't rendered one yet.
    /// </summary>
    public static Task<string?> GetRowStateAsync(ILocator row) =>
        row.EvaluateAsync<string?>(
            """
            (row, selectors) => {
                const compact = row.querySelector(selectors.compact);
                if (compact) {
                    return compact.getAttribute('data-tooltip')?.trim() ?? null;
                }
                return row.querySelector(selectors.text)?.textContent?.trim() ?? null;
            }
            """,
            new { compact = CompactStateSelector, text = StateTextSelector });

    /// <summary>
    /// Locates the element that shows <paramref name="state"/> in a resource row, at any density.
    /// </summary>
    public static ILocator RowState(ILocator row, string state) =>
        row.Locator($"{CompactStateSelector}[data-tooltip=\"{EscapeCssString(state)}\"]")
            .Or(row.Locator(StateTextSelector).Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(state)}$") }));

    private static string EscapeCssString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed record ResourceRow(string Type, string Name, string State, string SourceContains);
