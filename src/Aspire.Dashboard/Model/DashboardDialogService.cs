// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

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
    public Task<DashboardDialogReference> ShowDialogAsync<TDialog>(object content, DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowAsync<TDialog>(content, parameters, drawer: false);
    }

    /// <summary>
    /// Shows a dialog with the specified parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    /// <typeparam name="TDialog">The type of dialog component to show.</typeparam>
    /// <param name="parameters">The dialog parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public Task<DashboardDialogReference> ShowDialogAsync<TDialog>(DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowAsync<TDialog>(content: null, parameters, drawer: false);
    }

    /// <summary>
    /// Shows a panel dialog with the specified content and parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    /// <typeparam name="TDialog">The type of dialog component to show.</typeparam>
    /// <param name="content">The content to pass to the dialog.</param>
    /// <param name="parameters">The dialog parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public Task<DashboardDialogReference> ShowPanelAsync<TDialog>(object content, DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowAsync<TDialog>(content, parameters, drawer: true);
    }

    /// <summary>
    /// Shows a panel dialog with the specified parameters.
    /// Automatically sets the dismiss title to the localized close button text if not specified.
    /// </summary>
    /// <typeparam name="TDialog">The type of dialog component to show.</typeparam>
    /// <param name="parameters">The dialog parameters.</param>
    /// <returns>A reference to the opened dialog.</returns>
    public Task<DashboardDialogReference> ShowPanelAsync<TDialog>(DialogParameters parameters)
        where TDialog : ComponentBase
    {
        return ShowAsync<TDialog>(content: null, parameters, drawer: true);
    }

    /// <summary>
    /// Shows a confirmation dialog with the specified message.
    /// </summary>
    /// <param name="message">The confirmation message to display.</param>
    /// <returns>The dialog result.</returns>
    public async Task<DialogResult> ShowConfirmationAsync(string message)
    {
        return await dialogService.ShowConfirmationAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a dialog callback for handling dialog results.
    /// </summary>
    /// <param name="receiver">The component that will receive the callback.</param>
    /// <param name="callback">The callback function to execute when the dialog closes.</param>
    /// <returns>An event callback for the dialog result.</returns>
    public EventCallback<DialogResult> CreateDialogCallback(object receiver, Func<DialogResult, Task> callback)
    {
        return EventCallback.Factory.Create(receiver, callback);
    }

    private async Task<DashboardDialogReference> ShowAsync<TDialog>(object? content, DialogParameters parameters, bool drawer)
        where TDialog : ComponentBase
    {
        var opened = new TaskCompletionSource<IDialogInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateOptions(content, parameters, opened, drawer);
        var reference = new DashboardDialogReference(parameters.Id, completion.Task);

        var resultTask = drawer
            ? dialogService.ShowDrawerAsync<TDialog>(options)
            : dialogService.ShowDialogAsync<TDialog>(options);

        _ = CompleteAsync(resultTask, parameters, reference, completion);

        var firstCompleted = await Task.WhenAny(opened.Task, resultTask).ConfigureAwait(false);
        if (firstCompleted == opened.Task)
        {
            reference.SetInstance(await opened.Task.ConfigureAwait(false));
        }
        else
        {
            await resultTask.ConfigureAwait(false);
        }

        return reference;
    }

    private DialogOptions CreateOptions(object? content, DialogParameters parameters, TaskCompletionSource<IDialogInstance> opened, bool drawer)
    {
        var options = new DialogOptions
        {
            Id = parameters.Id,
            Width = parameters.Width,
            Height = parameters.Height,
            Data = parameters,
            // Fluent UI v5 uses "modal" for light-dismiss dialogs and "alert" for dialogs that
            // ignore overlay clicks. Drawers retain the conventional modal/non-modal behavior.
            Modal = drawer ? parameters.Modal : parameters.PreventDismissOnOverlayClick,
            Alignment = parameters.Alignment switch
            {
                HorizontalAlignment.Left => DialogAlignment.Start,
                HorizontalAlignment.Right => DialogAlignment.End,
                _ => DialogAlignment.Default
            },
            OnStateChange = args =>
            {
                if (args.Instance is { } instance && args.State is DialogState.Opening or DialogState.Open)
                {
                    opened.TrySetResult(instance);
                }
            }
        };

        if (content is not null)
        {
            options.Parameters["Content"] = content;
        }

        options.Header.Title = parameters.Title;
        options.Header.CloseAction.Visible = parameters.ShowDismiss;
        options.Header.CloseAction.Title = parameters.DismissTitle ?? CloseButtonText;
        options.Header.CloseAction.Tooltip = parameters.DismissTitle ?? CloseButtonText;
        options.Header.CloseAction.Icon = new Icons.Regular.Size20.Dismiss();
        options.Header.CloseAction.Label = null;

        options.Footer.PrimaryAction.Label = parameters.PrimaryAction;
        options.Footer.PrimaryAction.Visible = !parameters.UseCustomFooter && !string.IsNullOrEmpty(parameters.PrimaryAction);
        options.Footer.PrimaryAction.Disabled = !parameters.PrimaryActionEnabled;
        options.Footer.PrimaryAction.OnClickAsync = instance => instance.CloseAsync(DialogResult.Ok());

        options.Footer.SecondaryAction.Label = parameters.SecondaryAction;
        options.Footer.SecondaryAction.Visible = !parameters.UseCustomFooter && !string.IsNullOrEmpty(parameters.SecondaryAction);
        options.Footer.SecondaryAction.OnClickAsync = instance => instance.CloseAsync(DialogResult.Cancel(true));

        return options;
    }

    private static async Task CompleteAsync(
        Task<DialogResult> resultTask,
        DialogParameters parameters,
        DashboardDialogReference reference,
        TaskCompletionSource<DialogResult> completion)
    {
        try
        {
            var result = await resultTask.ConfigureAwait(true);

            if (parameters.OnDialogClosing.HasDelegate)
            {
                var instance = await GetInstanceAsync(reference).ConfigureAwait(true);
                if (instance is not null)
                {
                    await parameters.OnDialogClosing.InvokeAsync(instance).ConfigureAwait(true);
                }
            }

            if (parameters.OnDialogResult.HasDelegate)
            {
                await parameters.OnDialogResult.InvokeAsync(result).ConfigureAwait(true);
            }

            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        static Task<IDialogInstance?> GetInstanceAsync(DashboardDialogReference reference)
        {
            return Task.FromResult(reference.Instance);
        }
    }
}
