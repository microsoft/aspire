// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Tests.Shared;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class PageWithSessionAndUrlStateTests
{
    private const string BaseUri = "http://localhost/";

    [Theory]
    [InlineData("traces", "traces", "/traces/resource/api", "/traces/resource/api")]
    [InlineData("legacy/traces", "traces", "/traces/resource/api", "/legacy/traces/resource/api")]
    [InlineData("legacy", "", "/?view=Graph", "/legacy?view=Graph")]
    [InlineData("legacy/", "", "/?view=Graph", "/legacy?view=Graph")]
    public async Task InitializeViewModelAsync_SavedState_RestoresInCurrentExperience(string currentPath, string basePath, string savedUrl, string expectedPath)
    {
        var page = new TestPage(BaseUri + currentPath, basePath, savedUrl);

        var redirected = await page.InitializeViewModelAsync();

        Assert.True(redirected);
        Assert.Equal(BaseUri.TrimEnd('/') + expectedPath, page.Navigation.Uri);
        // The visit to the page's base path is an intermediate address, so going back must skip it.
        Assert.True(page.Navigation.LastNavigationOptions?.ReplaceHistoryEntry);
        Assert.False(page.QueryApplied);
    }

    [Theory]
    [InlineData("legacy/traces/resource/api", "traces")]
    [InlineData("legacy?view=Graph", "")]
    [InlineData("legacyfoo", "")]
    [InlineData("legacy/metrics", "traces")]
    public async Task InitializeViewModelAsync_NotOnBasePath_AppliesQueryWithoutRestoring(string currentPath, string basePath)
    {
        var page = new TestPage(BaseUri + currentPath, basePath, "/traces/resource/api");

        var redirected = await page.InitializeViewModelAsync();

        Assert.False(redirected);
        Assert.Equal(BaseUri + currentPath, page.Navigation.Uri);
        Assert.True(page.QueryApplied);
    }

    [Fact]
    public async Task InitializeViewModelAsync_SavedStateIsBasePath_DoesNotNavigate()
    {
        var page = new TestPage(BaseUri + "legacy/traces", "traces", "/traces");

        var redirected = await page.InitializeViewModelAsync();

        Assert.False(redirected);
        Assert.Null(page.Navigation.LastNavigationOptions);
        Assert.True(page.QueryApplied);
    }

    private sealed class TestPage(string currentUri, string basePath, string savedUrl) : IPageWithSessionAndUrlState<object, string>
    {
        public TestNavigationManager Navigation { get; } = new(BaseUri, currentUri);
        public bool QueryApplied { get; private set; }

        public string BasePath => basePath;
        public string SessionStorageKey => "page-state";
        public NavigationManager NavigationManager => Navigation;
        public ISessionStorage SessionStorage { get; } = new TestSessionStorage { OnGetAsync = _ => (true, "saved") };
        public object PageViewModel { get; set; } = new();

        public Task UpdateViewModelFromQueryAsync(object viewModel)
        {
            QueryApplied = true;
            return Task.CompletedTask;
        }

        public string GetUrlFromSerializableViewModel(string serializable) => savedUrl;

        public string ConvertViewModelToSerializable() => "saved";
    }
}
