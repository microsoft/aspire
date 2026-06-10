// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// A service for showing dialogs in the dashboard with automatic localization of common UI elements.
/// </summary>
public sealed class DashboardDialogService(
    IDialogService dialogService,
    DimensionManager dimensionManager)
{
    /// <summary>
    /// Gets the current viewport information from the dimension manager.
    /// </summary>
    public ViewportInformation ViewportInformation => dimensionManager.ViewportInformation;

    /// <summary>
    /// Gets a value indicating whether the viewport is in desktop mode.
    /// </summary>
    public bool IsDesktop => dimensionManager.ViewportInformation.IsDesktop;

    /// <summary>
    /// Shows a dialog with the specified content and options.
    /// </summary>
    public async Task<DialogResult> ShowDialogAsync<TDialog>(object content, DialogOptions options)
        where TDialog : ComponentBase
    {
        options.Data = content;
        return await dialogService.ShowDialogAsync<TDialog>(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a dialog with the specified options.
    /// </summary>
    public async Task<DialogResult> ShowDialogAsync<TDialog>(DialogOptions options)
        where TDialog : ComponentBase
    {
        return await dialogService.ShowDialogAsync<TDialog>(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a panel/drawer dialog with the specified content and options.
    /// </summary>
    public async Task<DialogResult> ShowPanelAsync<TDialog>(object content, DialogOptions options)
        where TDialog : ComponentBase
    {
        options.Data = content;
        return await dialogService.ShowDrawerAsync<TDialog>(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a panel/drawer dialog with the specified options.
    /// </summary>
    public async Task<DialogResult> ShowPanelAsync<TDialog>(DialogOptions options)
        where TDialog : ComponentBase
    {
        return await dialogService.ShowDrawerAsync<TDialog>(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a confirmation dialog with the specified message.
    /// </summary>
    public async Task<DialogResult> ShowConfirmationAsync(string message)
    {
        return await dialogService.ShowConfirmationAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a message box dialog with the specified options.
    /// </summary>
    public async Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions options)
    {
        return await dialogService.ShowMessageBoxAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a dialog with the specified content and parameters (v4-style).
    /// </summary>
    public Task<DialogResult> ShowDialogAsync<TDialog>(object content, DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowDialogAsync<TDialog>(content, parameters.ToDialogOptions());
    }

    /// <summary>
    /// Shows a dialog with the specified parameters (v4-style).
    /// </summary>
    public Task<DialogResult> ShowDialogAsync<TDialog>(DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowDialogAsync<TDialog>(parameters.ToDialogOptions());
    }

    /// <summary>
    /// Shows a panel/drawer dialog with the specified content and parameters (v4-style).
    /// </summary>
    public Task<DialogResult> ShowPanelAsync<TDialog>(object content, DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowPanelAsync<TDialog>(content, parameters.ToDialogOptions());
    }

    /// <summary>
    /// Shows a panel/drawer dialog with the specified parameters (v4-style).
    /// </summary>
    public Task<DialogResult> ShowPanelAsync<TDialog>(DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowPanelAsync<TDialog>(parameters.ToDialogOptions());
    }

    /// <summary>
    /// Shows a message box dialog using v4-style parameters.
    /// </summary>
    public async Task<DialogResult> ShowMessageBoxAsync(DialogParameters<MessageBoxContent> parameters)
    {
        var options = new MessageBoxOptions
        {
            Title = parameters.Title,
            Message = parameters.Content?.MarkupMessage?.ToString(),
        };
        return await dialogService.ShowMessageBoxAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a dialog callback for handling dialog results.
    /// </summary>
    public EventCallback<DialogResult> CreateDialogCallback(object receiver, Func<DialogResult, Task> callback)
    {
        return new EventCallbackFactory().Create(receiver, callback);
    }

    /// <summary>
    /// Opens a dialog and returns the dialog instance for tracking/closing externally.
    /// In v5, ShowDialogAsync blocks until the dialog is closed. This method fires
    /// the dialog open in the background and captures the instance via OnStateChange.
    /// </summary>
    public async Task<IDialogInstance> OpenDialogInstanceAsync<TDialog>(object content, DialogOptions options)
        where TDialog : ComponentBase
    {
        var instanceTcs = new TaskCompletionSource<IDialogInstance>(TaskCreationOptions.RunContinuationsAsynchronously);

        var existingOnStateChange = options.OnStateChange;
        options.OnStateChange = args =>
        {
            if (args.State == DialogState.Open && args.Instance is not null)
            {
                instanceTcs.TrySetResult(args.Instance);
            }

            existingOnStateChange?.Invoke(args);
        };

        options.Data = content;

        // Fire the dialog open without awaiting the result (it blocks until close).
        _ = dialogService.ShowDialogAsync<TDialog>(options);

        return await instanceTcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a panel/drawer dialog and returns the dialog instance for tracking/closing externally.
    /// </summary>
    public async Task<IDialogInstance> OpenPanelInstanceAsync<TDialog>(object content, DialogOptions options)
        where TDialog : ComponentBase
    {
        var instanceTcs = new TaskCompletionSource<IDialogInstance>(TaskCreationOptions.RunContinuationsAsynchronously);

        var existingOnStateChange = options.OnStateChange;
        options.OnStateChange = args =>
        {
            if (args.State == DialogState.Open && args.Instance is not null)
            {
                instanceTcs.TrySetResult(args.Instance);
            }

            existingOnStateChange?.Invoke(args);
        };

        options.Data = content;

        // Fire the panel open without awaiting the result (it blocks until close).
        _ = dialogService.ShowDrawerAsync<TDialog>(options);

        return await instanceTcs.Task.ConfigureAwait(false);
    }
}
