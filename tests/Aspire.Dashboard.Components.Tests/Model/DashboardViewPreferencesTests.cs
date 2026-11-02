// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class DashboardViewPreferencesTests : DashboardTestContext
{
    [Theory]
    [InlineData(false, "Graph", GroupOverview.Summary)]
    [InlineData(true, "Graph", GroupOverview.Graph)]
    [InlineData(true, null, GroupOverview.Summary)]
    [InlineData(true, "invalid", GroupOverview.Summary)]
    [InlineData(true, "999", GroupOverview.Summary)]
    public async Task Load_RestoresCurrentLayoutOnlyOnReload(bool isReload, string? storedCurrent, GroupOverview expected)
    {
        var writes = new Dictionary<string, object?>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, "Summary") : (false, null),
            OnSetUnprotectedAsync = (key, value) => writes[key] = value
        };
        var tabWrites = new Dictionary<string, object?>();
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.ActiveGroupOverview && storedCurrent is not null ? (true, storedCurrent) : (false, null),
            OnSetAsync = (key, value) => tabWrites[key] = value
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage, sessionStorage: sessionStorage);
        JSInterop.Setup<bool>("isPageReload").SetResult(isReload);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();

        Assert.Equal(expected, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Equal(expected.ToString(), tabWrites[BrowserStorageKeys.ActiveGroupOverview]);
        Assert.Empty(writes);
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
    public async Task SetDefault_DoesNotChangeCurrentLayoutOrTabStorage()
    {
        var writes = new Dictionary<string, object?>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, "Graph") : (false, null),
            OnSetUnprotectedAsync = (key, value) => writes[key] = value
        };
        var tabWrites = new Dictionary<string, object?>();
        var sessionStorage = new TestSessionStorage { OnSetAsync = (key, value) => tabWrites[key] = value };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage, sessionStorage: sessionStorage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();
        await preferences.SetDefaultGroupOverviewAsync(GroupOverview.Summary);

        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Equal("Summary", writes[BrowserStorageKeys.GroupOverview]);
        Assert.Equal("Graph", tabWrites[BrowserStorageKeys.ActiveGroupOverview]);
    }
}
