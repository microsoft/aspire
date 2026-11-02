// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class DashboardViewPreferencesTests : DashboardTestContext
{
    [Theory]
    [InlineData(null, GroupOverview.Cards)]
    [InlineData("invalid", GroupOverview.Cards)]
    [InlineData("Resources", GroupOverview.Cards)]
    [InlineData("Cards", GroupOverview.Cards)]
    [InlineData("Summary", GroupOverview.Summary)]
    [InlineData("Graph", GroupOverview.Graph)]
    public async Task Load_PreservesExistingDefaultAndMigratesFormerTable(string? storedLayout, GroupOverview expected)
    {
        var writes = new Dictionary<string, object?>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview && storedLayout is not null ? (true, storedLayout) : (false, null),
            OnSetUnprotectedAsync = (key, value) => writes[key] = value
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();

        Assert.Equal(expected, preferences.GroupOverview);
        Assert.Equal(expected, preferences.DefaultGroupOverview);
        if (storedLayout == "Resources")
        {
            var write = Assert.Single(writes);
            Assert.Equal(BrowserStorageKeys.GroupOverview, write.Key);
            Assert.Equal("Cards", write.Value);
        }
        else
        {
            Assert.Empty(writes);
        }
    }

    [Fact]
    public async Task GraphShortcutBeforeLoading_OverridesActiveLayoutWithoutSaving()
    {
        var writes = new Dictionary<string, object?>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, "Summary") : (false, null),
            OnSetUnprotectedAsync = (key, value) => writes[key] = value
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.SetGroupOverviewAsync(GroupOverview.Graph);
        await preferences.EnsureLoadedAsync();

        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task DisabledGraph_PreservesStoredDefaultAndBlocksGraphSelections()
    {
        var writes = new Dictionary<string, object?>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, "Graph") : (false, null),
            OnSetUnprotectedAsync = (key, value) => writes[key] = value
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = true);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();
        Assert.Equal(GroupOverview.Cards, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Cards, preferences.DefaultGroupOverview);
        await preferences.SetGroupOverviewAsync(GroupOverview.Graph);
        await preferences.SetDefaultGroupOverviewAsync(GroupOverview.Graph);
        Assert.Equal(GroupOverview.Cards, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Cards, preferences.DefaultGroupOverview);
        Assert.Empty(writes);

        await preferences.SetDefaultGroupOverviewAsync(GroupOverview.Summary);
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Equal("Summary", writes[BrowserStorageKeys.GroupOverview]);
    }
}
