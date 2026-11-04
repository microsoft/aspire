// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("999")]
    [InlineData("Cards")]
    [InlineData("Resources")]
    public async Task Load_MissingOrInvalidOverview_UsesHome(string? storedOverview)
    {
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, storedOverview) : (false, null),
            OnSetUnprotectedAsync = (_, _) => Assert.Fail("Invalid preferences use the default without a migration write.")
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();

        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Equal([GroupOverview.Summary, GroupOverview.Graph], Enum.GetValues<GroupOverview>());
        Assert.Equal(ResourceListDensity.Comfortable, preferences.Density);
        Assert.Equal(RecordingsStyle.Panel, preferences.RecordingsStyle);
    }

    [Theory]
    [InlineData("Summary", GroupOverview.Summary)]
    [InlineData("Graph", GroupOverview.Graph)]
    public async Task Load_ValidOverview_RetainsPreference(string storedOverview, GroupOverview expected)
    {
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, storedOverview) : (false, null)
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();

        Assert.Equal(expected, preferences.GroupOverview);
        Assert.Equal(expected, preferences.DefaultGroupOverview);
    }

    [Theory]
    [InlineData(false, "Summary", "Graph", GroupOverview.Summary)]
    [InlineData(false, "Graph", "Summary", GroupOverview.Graph)]
    [InlineData(true, "Summary", "Graph", GroupOverview.Graph)]
    [InlineData(true, "Graph", "Summary", GroupOverview.Summary)]
    [InlineData(true, "Graph", null, GroupOverview.Graph)]
    [InlineData(true, "Graph", "invalid", GroupOverview.Graph)]
    [InlineData(true, "Graph", "999", GroupOverview.Graph)]
    [InlineData(true, "Graph", "Cards", GroupOverview.Graph)]
    [InlineData(true, "Graph", "Resources", GroupOverview.Graph)]
    public async Task Load_RestoresActiveLayoutOnlyOnReload(bool isReload, string savedDefault, string? storedActive, GroupOverview expected)
    {
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, savedDefault) : (false, null),
            OnSetUnprotectedAsync = (_, _) => Assert.Fail("Loading a valid default must not change it.")
        };
        var tabWrites = new List<(string Key, object? Value)>();
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key =>
            {
                Assert.True(isReload);
                Assert.Equal(BrowserStorageKeys.ActiveGroupOverview, key);
                return storedActive is not null ? (true, storedActive) : (false, null);
            },
            OnSetAsync = (key, value) => tabWrites.Add((key, value))
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage, sessionStorage: sessionStorage);
        JSInterop.Setup<bool>("isPageReload").SetResult(isReload);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();
        await preferences.EnsureLoadedAsync();

        Assert.Equal(expected, preferences.GroupOverview);
        Assert.Equal(Enum.Parse<GroupOverview>(savedDefault), preferences.DefaultGroupOverview);
        Assert.Equal([(BrowserStorageKeys.ActiveGroupOverview, (object?)expected.ToString())], tabWrites);
    }

    [Theory]
    [InlineData("Graph", "Graph", true)]
    [InlineData("Graph", "Summary", false)]
    [InlineData("Summary", "Graph", true)]
    [InlineData("Summary", "Graph", false)]
    public async Task Load_UnavailableGraph_ReplacesSavedAndActivePreferencesWithoutRestoration(string savedDefault, string storedActive, bool isReload)
    {
        object? storedOverview = savedDefault;
        object? tabOverview = storedActive;
        var writes = new List<(string Key, object? Value)>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, storedOverview) : (false, null),
            OnSetUnprotectedAsync = (key, value) =>
            {
                writes.Add((key, value));
                storedOverview = value;
            }
        };
        var tabWrites = new List<(string Key, object? Value)>();
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.ActiveGroupOverview ? (true, tabOverview) : (false, null),
            OnSetAsync = (key, value) =>
            {
                tabWrites.Add((key, value));
                tabOverview = value;
            }
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage, sessionStorage: sessionStorage);
        Services.Configure<DashboardOptions>(o => o.UI.DisableResourceGraph = true);
        JSInterop.Setup<bool>("isPageReload").SetResult(isReload);
        var disabled = Services.GetRequiredService<DashboardViewPreferences>();

        await disabled.EnsureLoadedAsync();
        await disabled.SetGroupOverviewAsync(GroupOverview.Graph);
        await disabled.SetDefaultGroupOverviewAsync(GroupOverview.Graph);

        Assert.Equal(GroupOverview.Summary, disabled.GroupOverview);
        Assert.Equal(GroupOverview.Summary, disabled.DefaultGroupOverview);
        Assert.Equal(savedDefault == "Graph" ? [(BrowserStorageKeys.GroupOverview, (object?)"Summary")] : [], writes);
        Assert.Equal([(BrowserStorageKeys.ActiveGroupOverview, (object?)"Summary")], tabWrites);

        await using var reenabled = new DashboardViewPreferencesTests();
        FluentUISetupHelpers.AddCommonDashboardServices(reenabled, localStorage: storage, sessionStorage: sessionStorage);
        reenabled.JSInterop.Setup<bool>("isPageReload").SetResult(true);
        var enabled = reenabled.Services.GetRequiredService<DashboardViewPreferences>();
        await enabled.EnsureLoadedAsync();
        Assert.Equal(GroupOverview.Summary, enabled.GroupOverview);
        Assert.Equal(GroupOverview.Summary, enabled.DefaultGroupOverview);

        await enabled.SetGroupOverviewAsync(GroupOverview.Graph);
        Assert.Equal(GroupOverview.Graph, enabled.GroupOverview);
        Assert.Equal("Summary", storedOverview);
        Assert.Equal("Graph", tabOverview);
        await enabled.SetDefaultGroupOverviewAsync(GroupOverview.Graph);
        Assert.Equal("Graph", storedOverview);
    }

    [Fact]
    public async Task GraphShortcutBeforeLoading_ChangesOnlyActiveLayout()
    {
        var writes = new Dictionary<string, object?>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, "Summary") : (false, null),
            OnSetUnprotectedAsync = (key, value) => writes[key] = value
        };
        var tabWrites = new Dictionary<string, object?>();
        var sessionStorage = new TestSessionStorage { OnSetAsync = (key, value) => tabWrites[key] = value };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage, sessionStorage: sessionStorage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.SetGroupOverviewAsync(GroupOverview.Graph);
        await preferences.EnsureLoadedAsync();

        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Equal("Graph", tabWrites[BrowserStorageKeys.ActiveGroupOverview]);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task SetDefault_DoesNotChangeActiveLayoutOrTabStorage()
    {
        object? savedDefault = "Graph";
        var writes = new Dictionary<string, object?>();
        var storage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, savedDefault) : (false, null),
            OnSetUnprotectedAsync = (key, value) =>
            {
                writes[key] = value;
                savedDefault = value;
            }
        };
        var tabWrites = new List<(string Key, object? Value)>();
        var sessionStorage = new TestSessionStorage { OnSetAsync = (key, value) => tabWrites.Add((key, value)) };
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage, sessionStorage: sessionStorage);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await preferences.EnsureLoadedAsync();
        await preferences.SetDefaultGroupOverviewAsync(GroupOverview.Summary);

        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Equal([(BrowserStorageKeys.ActiveGroupOverview, (object?)"Graph")], tabWrites);
        Assert.Equal("Summary", writes[BrowserStorageKeys.GroupOverview]);
    }
}
