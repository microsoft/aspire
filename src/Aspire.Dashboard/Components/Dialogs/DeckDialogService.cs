// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

/// <summary>
/// Deck-native dialog engine, replacing FluentUI's <c>IDialogService</c>/<c>FluentDialogProvider</c>.
/// Tracks the currently open dialogs and raises <see cref="OnChanged"/> so a <c>DeckDialogProvider</c>
/// can render them. The public API mirrors the subset of the Fluent dialog service that the dashboard
/// uses (dialog, panel, message box, confirmation).
/// </summary>
public sealed class DeckDialogService
{
    private readonly List<DeckOpenDialog> _openDialogs = new();

    /// <summary>Raised whenever the set of open dialogs changes so the provider can re-render.</summary>
    public event Func<Task>? OnChanged;

    /// <summary>The currently open dialogs, in display order.</summary>
    public IReadOnlyList<DeckOpenDialog> OpenDialogs => _openDialogs;

    /// <summary>Shows a modal dialog rendering <typeparamref name="TDialog"/> with the given content.</summary>
    public Task<IDeckDialogReference> ShowDialogAsync<TDialog>(object content, DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
        => ShowCoreAsync(typeof(TDialog), content, parameters, DeckDialogType.Dialog);

    /// <summary>Shows a modal dialog rendering <typeparamref name="TDialog"/>.</summary>
    public Task<IDeckDialogReference> ShowDialogAsync<TDialog>(DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
        => ShowCoreAsync(typeof(TDialog), content: null, parameters, DeckDialogType.Dialog);

    /// <summary>Shows a side panel rendering <typeparamref name="TDialog"/> with the given content.</summary>
    public Task<IDeckDialogReference> ShowPanelAsync<TDialog>(object content, DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
        => ShowCoreAsync(typeof(TDialog), content, parameters, DeckDialogType.Panel);

    /// <summary>Shows a side panel rendering <typeparamref name="TDialog"/>.</summary>
    public Task<IDeckDialogReference> ShowPanelAsync<TDialog>(DeckDialogParameters parameters)
        where TDialog : IDeckDialogContentComponent
        => ShowCoreAsync(typeof(TDialog), content: null, parameters, DeckDialogType.Panel);

    /// <summary>Shows a message box using the supplied content and actions.</summary>
    public Task<IDeckDialogReference> ShowMessageBoxAsync(DeckDialogParameters<DeckMessageBoxContent> parameters)
    {
        parameters.DialogType = DeckDialogType.MessageBox;
        return ShowCoreAsync(typeof(DeckMessageBox), parameters.Content, parameters, DeckDialogType.MessageBox);
    }

    /// <summary>Shows a simple confirmation message box with the provided message.</summary>
    public Task<IDeckDialogReference> ShowConfirmationAsync(string message, string primaryText, string secondaryText)
    {
        var parameters = new DeckDialogParameters<DeckMessageBoxContent>
        {
            Content = new DeckMessageBoxContent
            {
                Intent = DeckMessageIntent.Confirmation,
                Message = message,
            },
            DialogType = DeckDialogType.MessageBox,
            PrimaryAction = primaryText,
            SecondaryAction = secondaryText,
        };
        return ShowMessageBoxAsync(parameters);
    }

    /// <summary>Creates a dialog result callback bound to <paramref name="receiver"/>.</summary>
    public EventCallback<DeckDialogResult> CreateDialogCallback(object receiver, Func<DeckDialogResult, Task> callback)
        => EventCallback.Factory.Create(receiver, callback);

    private async Task<IDeckDialogReference> ShowCoreAsync(Type componentType, object? content, DeckDialogParameters parameters, DeckDialogType type)
    {
        parameters.DialogType = type;

        var id = parameters.Id ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<DeckDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        DeckOpenDialog dialog = null!;

        Task CloseAsync(DeckDialogResult result) => CloseDialogAsync(dialog, result);
        async Task SetVisibleAsync(bool visible)
        {
            dialog.IsVisible = visible;
            await NotifyChangedAsync().ConfigureAwait(false);
        }

        var instance = new DeckDialogInstance(id, parameters, CloseAsync, SetVisibleAsync);
        dialog = new DeckOpenDialog(id, componentType, content, parameters, instance, tcs);

        _openDialogs.Add(dialog);
        await NotifyChangedAsync().ConfigureAwait(false);

        return new DeckDialogReference(dialog);
    }

    private async Task CloseDialogAsync(DeckOpenDialog dialog, DeckDialogResult result)
    {
        if (!_openDialogs.Remove(dialog))
        {
            return;
        }

        if (dialog.Parameters.OnDialogClosing.HasDelegate)
        {
            await dialog.Parameters.OnDialogClosing.InvokeAsync(dialog.Instance).ConfigureAwait(false);
        }

        if (dialog.Parameters.OnDialogResult.HasDelegate)
        {
            await dialog.Parameters.OnDialogResult.InvokeAsync(result).ConfigureAwait(false);
        }

        dialog.ResultSource.TrySetResult(result);
        await NotifyChangedAsync().ConfigureAwait(false);
    }

    private async Task NotifyChangedAsync()
    {
        if (OnChanged is { } handler)
        {
            await handler.Invoke().ConfigureAwait(false);
        }
    }

    /// <summary>Book-keeping for a single open dialog.</summary>
    public sealed class DeckOpenDialog
    {
        internal DeckOpenDialog(string id, Type componentType, object? content, DeckDialogParameters parameters, DeckDialogInstance instance, TaskCompletionSource<DeckDialogResult> resultSource)
        {
            Id = id;
            ComponentType = componentType;
            Content = content;
            Parameters = parameters;
            Instance = instance;
            ResultSource = resultSource;
            IsVisible = true;
        }

        /// <summary>The dialog id.</summary>
        public string Id { get; }

        /// <summary>The content component type to render.</summary>
        public Type ComponentType { get; }

        /// <summary>The strongly-typed content object passed to the component (if any).</summary>
        public object? Content { get; }

        /// <summary>The dialog parameters.</summary>
        public DeckDialogParameters Parameters { get; }

        /// <summary>The cascading instance handed to the content component.</summary>
        public DeckDialogInstance Instance { get; }

        internal TaskCompletionSource<DeckDialogResult> ResultSource { get; }

        /// <summary>Whether the dialog is currently visible (toggled by <see cref="DeckDialogInstance.Hide"/>).</summary>
        public bool IsVisible { get; internal set; }
    }

    private sealed class DeckDialogReference(DeckOpenDialog dialog) : IDeckDialogReference
    {
        public string Id => dialog.Id;

        public Task<DeckDialogResult> Result => dialog.ResultSource.Task;

        public Task CloseAsync() => dialog.Instance.CloseAsync();

        public Task CloseAsync(DeckDialogResult result) => dialog.Instance.CloseAsync(result);
    }
}
