// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

/// <summary>
/// A service for showing dialogs in the dashboard with automatic localization of common UI elements.
/// Wraps the Deck-native <see cref="DeckDialogService"/>.
/// </summary>
public sealed class DashboardDialogService(
    DeckDialogService dialogService,
    IStringLocalizer<Dialogs> dialogsLoc,
    DimensionManager dimensionManager)
{
    private string CloseButtonText => dialogsLoc[nameof(Dialogs.DialogCloseButtonText)];

    /// <summary>
    /// Gets the current viewport information from the dimension manager.
    /// </summary>
    public ViewportInformation ViewportInformation => dimensionManager.ViewportInformation;

    /// <summary>
    /// Gets a value indicating whether the viewport is in desktop mode.
    /// </summary>
    public bool IsDesktop => dimensionManager.ViewportInformation.IsDesktop;

    /// <summary>
    /// Shows a dialog with the specified content and parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    public async Task<IDeckDialogReference> ShowDialogAsync<TDialog>(object content, DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowDialogAsync<TDialog>(content, parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a dialog with the specified parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    public async Task<IDeckDialogReference> ShowDialogAsync<TDialog>(DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowDialogAsync<TDialog>(parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a panel dialog with the specified content and parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    public async Task<IDeckDialogReference> ShowPanelAsync<TDialog>(object content, DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowPanelAsync<TDialog>(content, parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a panel dialog with the specified parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    public async Task<IDeckDialogReference> ShowPanelAsync<TDialog>(DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowPanelAsync<TDialog>(parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a confirmation dialog with the specified message and localized Yes/No actions.
    /// </summary>
    public async Task<IDeckDialogReference> ShowConfirmationAsync(string message)
    {
        return await dialogService.ShowConfirmationAsync(
            message,
            dialogsLoc[nameof(Dialogs.ConfirmationDialogConfirmButtonText)],
            dialogsLoc[nameof(Dialogs.ConfirmationDialogCancelButtonText)]).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a message box dialog with the specified content and parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    public async Task<IDeckDialogReference> ShowMessageBoxAsync(DeckDialogParameters<DeckMessageBoxContent> parameters)
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowMessageBoxAsync(parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a dialog callback for handling dialog results.
    /// </summary>
    public EventCallback<DeckDialogResult> CreateDialogCallback(object receiver, Func<DeckDialogResult, Task> callback)
    {
        return dialogService.CreateDialogCallback(receiver, callback);
    }

    private void SetDefaultDismissTitle(DeckDialogParameters parameters)
    {
        parameters.DismissTitle ??= CloseButtonText;
    }
}
