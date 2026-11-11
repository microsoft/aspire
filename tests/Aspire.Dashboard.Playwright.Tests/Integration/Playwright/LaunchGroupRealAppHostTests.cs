// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001 // Tests the experimental file-based .NET project support.
#pragma warning disable ASPIRELAUNCHGROUPS001 // Tests the experimental launch-group APIs.

using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.TestUtilities;
using Aspire.Templates.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class LaunchGroupRealAppHostTests(PlaywrightFixture playwrightFixture, ITestOutputHelper testOutputHelper)
    : IClassFixture<PlaywrightFixture>
{
    private const string FrontendGroup = "frontend";
    private const string BackendGroup = "backend";
    private const string ReplicaResourceName = "frontend-replica";
    private const string ChildResourceName = "frontend-child";
    private const string ReferencedResourceName = "referenced-only";
    private const string BackendResourceName = "backend-service";
    private const string DeferredResourceName = "deferred-project";
    private const string CallbackResourceName = "callback-resource";

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task LaunchGroupFilterUsesEffectiveMembershipWithoutStartingResources()
    {
        using (var workspace = TemporaryWorkspace.Create(testOutputHelper))
        {
            CopyFixtureToWorkspace(workspace.Path);
            var workspacePath = workspace.Path;
            var replicaSourcePath = Path.Combine(workspacePath, "ReplicaResource.cs");
            var deferredProjectPath = Path.Combine(workspacePath, "DeferredProject", "DeferredProject.csproj");
            var buildMarkerPath = Path.Combine(workspacePath, "DeferredProject", "build.marker");
            var argumentCallbackCount = 0;
            var environmentCallbackCount = 0;

            await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.LaunchGroupAppHost>(
                new DistributedApplicationTestingBuilderOptions { EnableDashboard = true },
                []);
            builder.Configuration["Aspire:LaunchGroup"] = FrontendGroup;

            var replica = builder.AddDotnetProject(
                    ReplicaResourceName,
                    replicaSourcePath,
                    options => options.ExcludeLaunchProfile = true)
                .WithLaunchGroups(FrontendGroup)
                .WithReplicas(2);

            // Launch-group inheritance follows IResourceWithParent structural chains, not UI-only relationship annotations.
            builder.AddResource(new ChildExecutableResource(ChildResourceName, replica.Resource, workspacePath))
                .WithExplicitStart()
                .WithArgs("--info");

            builder.AddExecutable(ReferencedResourceName, "dotnet", workspacePath, "--info")
                .WithExplicitStart()
                .WithReferenceRelationship(replica);

            builder.AddExecutable(BackendResourceName, "dotnet", workspacePath, "--info")
                .WithLaunchGroups(BackendGroup);

            builder.AddDotnetProject(
                    DeferredResourceName,
                    deferredProjectPath,
                    options => options.ExcludeLaunchProfile = true)
                .WithExplicitStart();

            builder.AddExecutable(CallbackResourceName, "dotnet", workspacePath)
                .WithExplicitStart()
                .WithArgs(_ => Interlocked.Increment(ref argumentCallbackCount))
                .WithEnvironment(_ => Interlocked.Increment(ref environmentCallbackCount));

            await using var app = await builder.BuildAsync();
            await app.StartAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

            Assert.False(File.Exists(buildMarkerPath), "The deferred project was built while the AppHost started.");
            Assert.Equal(0, Volatile.Read(ref argumentCallbackCount));
            Assert.Equal(0, Volatile.Read(ref environmentCallbackCount));

            using var cancellationTokenSource = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
            var dashboardUri = await app.GetDashboardUrlAsync(cancellationTokenSource.Token);
            await using var context = await playwrightFixture.CreateContextAsync(new BrowserNewContextOptions
            {
                BaseURL = dashboardUri.GetLeftPart(UriPartial.Authority)
            });
            var page = await context.NewPageAsync();
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync(dashboardUri.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            var replicaRows = GetRowsByDisplayName(page, ReplicaResourceName);
            await Assertions.Expect(replicaRows).ToHaveCountAsync(2, new() { Timeout = TestConstants.LongTimeoutDuration });
            foreach (var row in await replicaRows.AllAsync())
            {
                await Assertions.Expect(DashboardResourceAssertions.RowState(row, "Running")).ToBeVisibleAsync();
            }

            string[] notStartedResources =
            [
                ChildResourceName,
                ReferencedResourceName,
                BackendResourceName,
                DeferredResourceName,
                CallbackResourceName
            ];
            foreach (var resourceName in notStartedResources)
            {
                var row = GetRowsByDisplayName(page, resourceName);
                await Assertions.Expect(row).ToHaveCountAsync(1);
                await Assertions.Expect(DashboardResourceAssertions.RowState(row, "Not started")).ToBeVisibleAsync();
            }

            var trackedResourceNames = await GetActualResourceNamesAsync(replicaRows);
            foreach (var resourceName in notStartedResources)
            {
                trackedResourceNames.UnionWith(await GetActualResourceNamesAsync(GetRowsByDisplayName(page, resourceName)));
            }
            Assert.Equal(7, trackedResourceNames.Count);
            var initialStates = await GetResourceStatesAsync(page, trackedResourceNames);

            var filterTrigger = page.GetByRole(
                AriaRole.Button,
                new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true });
            await filterTrigger.ClickAsync();
            var filter = page.GetByRole(
                AriaRole.Group,
                new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true });
            await Assertions.Expect(filter).ToBeVisibleAsync();
            var launchGroupsHeader = filter.GetByRole(
                AriaRole.Button,
                new() { Name = Resources.Resources.ResourceLaunchGroupsHeader });
            await Assertions.Expect(launchGroupsHeader).ToBeVisibleAsync();
            await launchGroupsHeader.ClickAsync();
            var launchGroups = launchGroupsHeader.Locator("xpath=..");
            await Assertions.Expect(launchGroups.GetByRole(AriaRole.Checkbox)).ToHaveCountAsync(4);
            await Assertions.Expect(launchGroups.GetByRole(
                AriaRole.Checkbox,
                new() { Name = Resources.ControlsStrings.LabelAll, Exact = true })).ToBeCheckedAsync();
            await Assertions.Expect(launchGroups.GetByRole(
                AriaRole.Checkbox,
                new() { Name = FrontendGroup, Exact = true })).ToBeCheckedAsync();
            await Assertions.Expect(launchGroups.GetByRole(
                AriaRole.Checkbox,
                new() { Name = BackendGroup, Exact = true })).ToBeCheckedAsync();
            await Assertions.Expect(launchGroups.GetByRole(
                AriaRole.Checkbox,
                new() { Name = Resources.Resources.ResourceLaunchGroupsUngrouped, Exact = true })).ToBeCheckedAsync();
            await filterTrigger.ClickAsync();

            var firstReplicaRow = replicaRows.First;
            var firstReplicaName = await firstReplicaRow.GetAttributeAsync("data-resource-name");
            Assert.False(string.IsNullOrEmpty(firstReplicaName));
            await firstReplicaRow.Locator(".resource-row-link").ClickAsync();
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(firstReplicaName);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsEffectiveMembership, FrontendGroup);
            await AssertOverviewPropertyAbsentAsync(page, Resources.Resources.ResourceLaunchGroupsInheritedFrom);
            await AssertOverviewPropertyAbsentAsync(page, Resources.Resources.ResourceLaunchGroupsExplicitStart);

            var statusGroup = page.Locator("[aria-labelledby='overview-status-title'] .launch-group-value").First;
            var propertiesGroup = page.Locator("[aria-labelledby='overview-properties-title'] .launch-group-value").First;
            const string visibleMembers = "frontend-child, frontend-replica";
            await Assertions.Expect(statusGroup).ToHaveAttributeAsync("data-tooltip", visibleMembers);
            await Assertions.Expect(propertiesGroup).ToHaveAttributeAsync("data-tooltip", visibleMembers);
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToHaveAttributeAsync("inert", "");
            await page.Mouse.MoveAsync(1, 1);
            await page.Keyboard.PressAsync("Tab");
            await statusGroup.FocusAsync();
            await Assertions.Expect(statusGroup).ToBeFocusedAsync();
            var tooltip = page.Locator("aspire-tooltip-provider fluent-tooltip[aria-hidden='false']");
            await Assertions.Expect(tooltip).ToHaveTextAsync(visibleMembers);
            await page.Mouse.MoveAsync(1, 1);
            await statusGroup.HoverAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(visibleMembers);
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "launch-groups-overview.png"), FullPage = true });

            var viewOptions = page.GetByRole(AriaRole.Button, new() { Name = Resources.Resources.ResourcesChangeViewOptions, Exact = true });
            await viewOptions.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = Resources.ControlsStrings.ShowHiddenResources, Exact = true }).ClickAsync();
            const string allMembers = "frontend-child, frontend-replica, frontend-replica-rebuilder";
            await Assertions.Expect(statusGroup).ToHaveAttributeAsync("data-tooltip", allMembers);
            await Assertions.Expect(propertiesGroup).ToHaveAttributeAsync("data-tooltip", allMembers);
            await viewOptions.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = Resources.ControlsStrings.ShowHiddenResources, Exact = true }).ClickAsync();
            await Assertions.Expect(statusGroup).ToHaveAttributeAsync("data-tooltip", visibleMembers);

            await OpenResourceAsync(page, ChildResourceName);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsEffectiveMembership, FrontendGroup);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsInheritedFrom, ReplicaResourceName);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsExplicitStart, Resources.Resources.ResourceLaunchGroupsYes);

            var exclusionDescription = string.Format(
                CultureInfo.CurrentCulture, Resources.Columns.StateColumnResourceNotInLaunchGroup, FrontendGroup);
            await OpenResourceAsync(page, ReferencedResourceName);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsEffectiveMembership, Resources.Resources.ResourceLaunchGroupsUngrouped);
            await AssertOverviewPropertyAbsentAsync(page, Resources.Resources.ResourceLaunchGroupsInheritedFrom);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsExplicitStart, Resources.Resources.ResourceLaunchGroupsYes);
            await AssertOverviewPropertyAsync(page, Resources.ControlsStrings.ResourceDetailsStateDescriptionHeader, exclusionDescription);

            await OpenResourceAsync(page, BackendResourceName);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsEffectiveMembership, BackendGroup);
            await AssertOverviewPropertyAbsentAsync(page, Resources.Resources.ResourceLaunchGroupsExplicitStart);
            await AssertOverviewPropertyAsync(page, Resources.ControlsStrings.ResourceDetailsStateDescriptionHeader, exclusionDescription);

            await OpenResourceAsync(page, DeferredResourceName);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsEffectiveMembership, Resources.Resources.ResourceLaunchGroupsUngrouped);
            await AssertOverviewPropertyAsync(page, Resources.Resources.ResourceLaunchGroupsExplicitStart, Resources.Resources.ResourceLaunchGroupsYes);
            await AssertOverviewPropertyAsync(
                page,
                Resources.ControlsStrings.ResourceDetailsStateDescriptionHeader,
                string.Format(CultureInfo.CurrentCulture, Resources.Columns.StateColumnResourceNotInLaunchGroupDeferred, FrontendGroup));
            var stateDetails = page.Locator("[aria-labelledby='overview-status-title'] .launch-group-value").First;
            await Assertions.Expect(stateDetails).ToHaveTextAsync(FrontendGroup);
            await Assertions.Expect(stateDetails).ToHaveAttributeAsync("data-tooltip", visibleMembers);
            await stateDetails.FocusAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(visibleMembers);
            await stateDetails.HoverAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(visibleMembers);

            await OpenResourceAsync(page, ChildResourceName);
            await filterTrigger.ClickAsync();
            await Assertions.Expect(filter).ToBeVisibleAsync();
            await Assertions.Expect(launchGroupsHeader).ToHaveAttributeAsync("aria-expanded", "true");
            await launchGroups.GetByRole(AriaRole.Checkbox, new() { Name = BackendGroup, Exact = true }).UncheckAsync();
            await launchGroups.GetByRole(
                AriaRole.Checkbox,
                new() { Name = Resources.Resources.ResourceLaunchGroupsUngrouped, Exact = true }).UncheckAsync();

            await Assertions.Expect(GetRowsByDisplayName(page, ReplicaResourceName)).ToHaveCountAsync(2);
            await Assertions.Expect(GetRowsByDisplayName(page, ChildResourceName)).ToHaveCountAsync(1);
            foreach (var resourceName in notStartedResources.Where(name => name != ChildResourceName))
            {
                await Assertions.Expect(GetRowsByDisplayName(page, resourceName)).ToHaveCountAsync(0);
            }

            await launchGroups.GetByRole(
                AriaRole.Checkbox,
                new() { Name = FrontendGroup, Exact = true }).UncheckAsync();
            await Assertions.Expect(GetRowsByDisplayName(page, ReplicaResourceName)).ToHaveCountAsync(0);
            await Assertions.Expect(GetRowsByDisplayName(page, ChildResourceName)).ToHaveCountAsync(0);

            await filter.GetByRole(
                AriaRole.Button,
                new() { Name = Resources.Layout.ResourcePaneFilterReset, Exact = true }).ClickAsync();
            await Assertions.Expect(GetRowsByDisplayName(page, ReplicaResourceName)).ToHaveCountAsync(2);
            foreach (var resourceName in notStartedResources)
            {
                await Assertions.Expect(GetRowsByDisplayName(page, resourceName)).ToHaveCountAsync(1);
            }

            var finalStates = await GetResourceStatesAsync(page, trackedResourceNames);
            Assert.Equal(
                initialStates.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                finalStates.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            Assert.False(File.Exists(buildMarkerPath), "Filtering the Dashboard built the deferred project.");
            Assert.Equal(0, Volatile.Read(ref argumentCallbackCount));
            Assert.Equal(0, Volatile.Read(ref environmentCallbackCount));
        }
    }

    private static ILocator GetRowsByDisplayName(IPage page, string resourceName)
    {
        // DCP appends an instance suffix to replicated resources while single-instance resources keep the model name.
        var replicaSuffix = resourceName == ReplicaResourceName ? "(?:-.+)?" : string.Empty;
        var name = page.Locator(".resource-row-name")
            .Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(resourceName)}{replicaSuffix}$") });
        return page.Locator(".resource-row[data-resource-name]").Filter(new() { Has = name });
    }

    private static async Task<HashSet<string>> GetActualResourceNamesAsync(ILocator rows)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in await rows.AllAsync())
        {
            var name = await row.GetAttributeAsync("data-resource-name");
            Assert.False(string.IsNullOrEmpty(name));
            names.Add(name);
        }

        return names;
    }

    private static async Task<Dictionary<string, string>> GetResourceStatesAsync(
        IPage page,
        IReadOnlySet<string> trackedResourceNames)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in await page.Locator(".resource-row[data-resource-name]").AllAsync())
        {
            var name = await row.GetAttributeAsync("data-resource-name");
            if (name is null || !trackedResourceNames.Contains(name))
            {
                continue;
            }

            var state = await DashboardResourceAssertions.GetRowStateAsync(row);
            Assert.False(string.IsNullOrEmpty(state));
            states.Add(name, state);
        }

        Assert.Equal(trackedResourceNames.Count, states.Count);
        return states;
    }

    private static async Task OpenResourceAsync(IPage page, string resourceName)
    {
        var row = GetRowsByDisplayName(page, resourceName);
        await Assertions.Expect(row).ToHaveCountAsync(1);
        await row.Locator(".resource-row-link").ClickAsync();
        await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(resourceName);
    }

    private static async Task AssertOverviewPropertyAsync(IPage page, string label, string expectedValue)
    {
        var properties = page.Locator(".overview-properties");
        var propertyLabel = properties.Locator("dt")
            .Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(label)}$") });
        await Assertions.Expect(propertyLabel).ToHaveCountAsync(1);
        await Assertions.Expect(propertyLabel.Locator("xpath=following-sibling::dd[1]")).ToHaveTextAsync(expectedValue);
    }

    private static async Task AssertOverviewPropertyAbsentAsync(IPage page, string label)
    {
        var propertyLabel = page.Locator(".overview-properties dt")
            .Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(label)}$") });
        await Assertions.Expect(propertyLabel).ToHaveCountAsync(0);
    }

    private static void CopyFixtureToWorkspace(string workspacePath)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LaunchGroups");

        foreach (var directory in Directory.EnumerateDirectories(fixturePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(workspacePath, Path.GetRelativePath(fixturePath, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(fixturePath, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(workspacePath, Path.GetRelativePath(fixturePath, file));
            File.Copy(file, destinationPath);
        }
    }

    private sealed class ChildExecutableResource(string name, IResource parent, string workingDirectory)
        : ExecutableResource(name, "dotnet", workingDirectory), IResourceWithParent
    {
        public IResource Parent { get; } = parent;
    }
}
