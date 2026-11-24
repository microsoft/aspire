// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class DashboardViewPreferencesTests : DashboardTestContext
{
    [Theory]
    [InlineData("Cards")]
    [InlineData("Resources")]
    public async Task Load_RemovedLayout_WritesSummaryOnce(string removedLayout)
    {
        object? stored = removedLayout;
        var writes = new List<(string Key, object? Value)>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, stored) : (false, null),
            OnSetUnprotectedAsync = (key, value) =>
            {
                writes.Add((key, value));
                stored = value;
            }
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();
        await preferences.EnsureLoadedAsync();

        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal([(BrowserStorageKeys.GroupOverview, (object?)"Summary")], writes);

        await using var reopened = new DashboardViewPreferencesTests();
        FluentUISetupHelpers.AddCommonDashboardServices(reopened, localStorage: storage);
        await reopened.Services.GetRequiredService<DashboardViewPreferences>().EnsureLoadedAsync();
        Assert.Single(writes);
    }

    [Fact]
    public async Task Load_RemovedSpaciousDensity_WritesComfortableOnce()
    {
        object? stored = "Spacious";
        var writes = new List<(string Key, object? Value)>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourceListDensity ? (true, stored) : (false, null),
            OnSetUnprotectedAsync = (key, value) =>
            {
                writes.Add((key, value));
                stored = value;
            }
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();

        Assert.Equal(ResourceListDensity.Comfortable, preferences.Density);
        Assert.Equal([(BrowserStorageKeys.ResourceListDensity, (object?)"Comfortable")], writes);

        await using var reopened = new DashboardViewPreferencesTests();
        FluentUISetupHelpers.AddCommonDashboardServices(reopened, localStorage: storage);
        var reopenedPreferences = reopened.Services.GetRequiredService<DashboardViewPreferences>();
        await reopenedPreferences.EnsureLoadedAsync();
        Assert.Equal(ResourceListDensity.Comfortable, reopenedPreferences.Density);
        Assert.Single(writes);
    }

    [Theory]
    [InlineData(null, ResourceListDensity.Comfortable)]
    [InlineData("invalid", ResourceListDensity.Comfortable)]
    [InlineData("Compact", ResourceListDensity.Compact)]
    public async Task Load_UsesComfortableUnlessCompactIsSaved(string? stored, ResourceListDensity expected)
    {
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourceListDensity && stored is not null ? (true, stored) : (false, null),
            OnSetUnprotectedAsync = (_, _) => Assert.Fail("Loading the density should not write a preference.")
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();

        Assert.Equal(expected, preferences.Density);
    }

    [Theory]
    [InlineData(null, GroupOverview.Summary)]
    [InlineData("", GroupOverview.Summary)]
    [InlineData("invalid", GroupOverview.Summary)]
    [InlineData("999", GroupOverview.Summary)]
    [InlineData("0", GroupOverview.Summary)]
    [InlineData("3", GroupOverview.Summary)]
    [InlineData("1", GroupOverview.Summary)]
    [InlineData("2", GroupOverview.Graph)]
    [InlineData("Summary", GroupOverview.Summary)]
    [InlineData("Graph", GroupOverview.Graph)]
    public async Task Load_ExistingOrInvalidPreference_UsesAvailableLayout(string? stored, GroupOverview expected)
    {
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, stored) : (false, null),
            OnSetUnprotectedAsync = (_, _) => Assert.Fail("Only removed named layouts require a migration write.")
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();

        Assert.Equal(expected, preferences.GroupOverview);
        Assert.Equal([GroupOverview.Summary, GroupOverview.Graph], Enum.GetValues<GroupOverview>());
    }

    [Fact]
    public async Task Load_DisabledGraph_FallsBackToSummaryWithoutChangingExistingGraphPolicy()
    {
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, "Graph") : (false, null),
            OnSetUnprotectedAsync = (_, _) => Assert.Fail("The graph preference is unchanged.")
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = true);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();
        await preferences.SetGroupOverviewAsync(GroupOverview.Graph);

        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
    }

    [Fact]
    public async Task Load_ReadsDensityAndGroupOverviewConcurrently()
    {
        var requestedKeys = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var allRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new TestLocalStorage
        {
            OnBeforeGetUnprotectedAsync = key =>
            {
                requestedKeys.TryAdd(key, 0);
                if (requestedKeys.ContainsKey(BrowserStorageKeys.ResourceListDensity) &&
                    requestedKeys.ContainsKey(BrowserStorageKeys.GroupOverview))
                {
                    allRequested.TrySetResult();
                }

                // Hold every read until both keys have been requested; sequential reads would deadlock here.
                return allRequested.Task;
            },
            OnGetUnprotectedAsync = key => key switch
            {
                BrowserStorageKeys.ResourceListDensity => (true, nameof(ResourceListDensity.Comfortable)),
                BrowserStorageKeys.GroupOverview => (true, nameof(GroupOverview.Graph)),
                _ => (false, null)
            }
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync().DefaultTimeout();

        Assert.Equal(1, storage.SnapshotReadCount);
        Assert.Equal(ResourceListDensity.Comfortable, preferences.Density);
        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
    }
}
