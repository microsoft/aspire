// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Represents a page that can contain state both in the url and in localstorage.
/// Navigating back to the page will restore the previous page state
/// </summary>
/// <typeparam name="TViewModel">The view model containing live state</typeparam>
/// <typeparam name="TSerializableViewModel">A serializable version of <typeparamref name="TViewModel"/> that will be saved in session storage and restored from</typeparam>
public interface IPageWithSessionAndUrlState<TViewModel, TSerializableViewModel>
    where TSerializableViewModel : class
{
    /// <summary>
    /// The base relative path of the page (ie, Metrics for /Metrics)
    /// </summary>
    public string BasePath { get; }

    /// <summary>
    /// The key to save page state to
    /// </summary>
    public string SessionStorageKey { get; }

    public NavigationManager NavigationManager { get; }
    public ISessionStorage SessionStorage { get; }

    /// <summary>
    /// The view model containing live state, to be instantiated in OnInitialized.
    /// </summary>
    public TViewModel PageViewModel { get; set; }

    /// <summary>
    /// Computes the initial view model state based on query param values
    /// </summary>
    public Task UpdateViewModelFromQueryAsync(TViewModel viewModel);

    /// <summary>
    /// Translates the <param name="serializable">serializable form of the view model</param> to a relative URL associated
    /// with that state
    /// </summary>
    public string GetUrlFromSerializableViewModel(TSerializableViewModel serializable);

    /// <summary>
    /// Maps <typeparamref name="TViewModel"/> to <typeparamref name="TSerializableViewModel"/>, which should contain simple types.
    /// </summary>
    public TSerializableViewModel ConvertViewModelToSerializable();
}

public static class PageExtensions
{
    /// <summary>
    /// Called after a change in the view model that will affect the url associated with new page state
    /// to navigate to the new url and save new state in localstorage.
    /// <param name="page"></param>
    /// <param name="layout"></param>
    /// <param name="waitToApplyMobileChange">Whether we should avoid applying this change immediately on mobile, and instead
    /// only once the toolbar has been closed.</param>
    /// </summary>
    public static async Task AfterViewModelChangedAsync<TViewModel, TSerializableViewModel>(this IPageWithSessionAndUrlState<TViewModel, TSerializableViewModel> page, AspirePageContentLayout? layout, bool waitToApplyMobileChange) where TSerializableViewModel : class
    {
        // Only defer while the mobile filter dialog is open. Other mobile controls (such as the
        // Resources tabs) must update the URL immediately, not leave a navigation queued until
        // the dialog closes or the page is disposed.
        if (layout is { IsToolbarPanelOpen: true } && !layout.ViewportInformation.IsDesktop && waitToApplyMobileChange)
        {
            layout.DialogCloseListeners[nameof(AfterViewModelChangedAsync)] = SetStateAndNavigateAsync;
            return;
        }

        await SetStateAndNavigateAsync();
        return;

        async Task SetStateAndNavigateAsync()
        {
            var serializableViewModel = page.ConvertViewModelToSerializable();
            var pathWithParameters = page.GetUrlFromSerializableViewModel(serializableViewModel);

            page.NavigationManager.NavigateTo(pathWithParameters);
            await page.SessionStorage.SetAsync(page.SessionStorageKey, serializableViewModel).ConfigureAwait(false);
        }
    }

    public static async Task RefreshIfMobileAsync<TViewModel, TSerializableViewModel>(this IPageWithSessionAndUrlState<TViewModel, TSerializableViewModel> page, AspirePageContentLayout? layout) where TSerializableViewModel : class
    {
        if (layout is not null && !layout.ViewportInformation.IsDesktop)
        {
            await AfterViewModelChangedAsync(page, layout, false);
        }
    }

    /// <summary>
    /// If first visiting the page then initialize page state from storage and redirect using page state.
    /// </summary>
    /// <returns>
    /// A value indicating whether there was a page redirect. Further page initialization should check the return value
    /// and wait until parameters are updated if there was a page redirect.
    /// </returns>
    public static async Task<bool> InitializeViewModelAsync<TViewModel, TSerializableViewModel>(this IPageWithSessionAndUrlState<TViewModel, TSerializableViewModel> page) where TSerializableViewModel : class
    {
        if (string.Equals(page.BasePath, page.NavigationManager.ToBaseRelativePath(page.NavigationManager.Uri)))
        {
            var result = await page.SessionStorage.GetAsync<TSerializableViewModel>(page.SessionStorageKey).ConfigureAwait(false);
            if (result is { Success: true, Value: not null })
            {
                var newUrl = page.GetUrlFromSerializableViewModel(result.Value).ToString();

                // Don't navigate if the URL redirects to itself.
                if (newUrl != "/" + page.BasePath)
                {
                    // Replace the initial address with this navigation.
                    // We do this because the visit to "/{BasePath}" then redirect to the final address is automatic from the user perspective.
                    // Replacing the visit to "/{BasePath}" is good because we want to take the user back to where they started, not an intermediary address.
                    page.NavigationManager.NavigateTo(newUrl, new NavigationOptions { ReplaceHistoryEntry = true });
                    return true;
                }
            }
        }

        ArgumentNullException.ThrowIfNull(page.PageViewModel, nameof(page.PageViewModel));
        await page.UpdateViewModelFromQueryAsync(page.PageViewModel);
        return false;
    }
}
