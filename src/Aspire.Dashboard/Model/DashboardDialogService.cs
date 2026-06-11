// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// A service for showing dialogs in the dashboard with automatic localization of common UI elements.
/// </summary>
public sealed class DashboardDialogService(
    IDialogService dialogService,
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
    /// <typeparam name="TDialog">The type of dialog component to show.</typeparam>
    /// <param name="content">The content to pass to the dialog.</param>
    /// <param name="parameters">The dialog parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public async Task<IDialogReference> ShowDialogAsync<TDialog>(object content, DialogParameters parameters)
        where TDialog : IDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowDialogAsync<TDialog>(content, parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a dialog with the specified parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    /// <typeparam name="TDialog">The type of dialog component to show.</typeparam>
    /// <param name="parameters">The dialog parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public async Task<IDialogReference> ShowDialogAsync<TDialog>(DialogParameters parameters)
        where TDialog : IDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowDialogAsync<TDialog>(parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a panel dialog with the specified content and parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    /// <typeparam name="TDialog">The type of dialog component to show.</typeparam>
    /// <param name="content">The content to pass to the dialog.</param>
    /// <param name="parameters">The dialog parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public async Task<IDialogReference> ShowPanelAsync<TDialog>(object content, DialogParameters parameters)
        where TDialog : IDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowPanelAsync<TDialog>(content, parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a panel dialog with the specified parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    /// <typeparam name="TDialog">The type of dialog component to show.</typeparam>
    /// <param name="parameters">The dialog parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public async Task<IDialogReference> ShowPanelAsync<TDialog>(DialogParameters parameters)
        where TDialog : IDialogContentComponent
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowPanelAsync<TDialog>(parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a confirmation dialog with the specified message.
    /// </summary>
    /// <param name="message">The confirmation message to display.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public async Task<IDialogReference> ShowConfirmationAsync(string message)
    {
        return await dialogService.ShowConfirmationAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a message box dialog with the specified content and parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    /// <param name="parameters">The message box parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public async Task<IDialogReference> ShowMessageBoxAsync(DialogParameters<MessageBoxContent> parameters)
    {
        SetDefaultDismissTitle(parameters);
        return await dialogService.ShowMessageBoxAsync(parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a dialog callback for handling dialog results.
    /// </summary>
    /// <param name="receiver">The component that will receive the callback.</param>
    /// <param name="callback">The callback function to execute when the dialog closes.</param>
    /// <returns>An event callback for the dialog result.</returns>
    public EventCallback<DialogResult> CreateDialogCallback(object receiver, Func<DialogResult, Task> callback)
    {
        return dialogService.CreateDialogCallback(receiver, callback);
    }

    private void SetDefaultDismissTitle(DialogParameters parameters)
    {
        parameters.DismissTitle ??= CloseButtonText;
    }
}
