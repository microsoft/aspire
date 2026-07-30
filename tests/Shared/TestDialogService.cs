// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;

namespace Aspire.Tests.Shared;

/// <summary>
/// Test double for the Deck dialog engine. When constructed with a callback it intercepts the
/// show/panel/message-box calls (capturing parameters and returning a caller-supplied reference);
/// otherwise it falls back to the real engine behavior.
/// </summary>
public class TestDialogService : DeckDialogService
{
    private readonly Func<object?, DeckDialogParameters, Task<IDeckDialogReference>>? _onShowDialog;

    public TestDialogService(Func<object?, DeckDialogParameters, Task<IDeckDialogReference>>? onShowDialog = null)
    {
        _onShowDialog = onShowDialog;
    }

    public override Task<IDeckDialogReference> ShowDialogAsync<TDialog>(object content, DeckDialogParameters parameters)
        => _onShowDialog is not null ? _onShowDialog(content, parameters) : base.ShowDialogAsync<TDialog>(content, parameters);

    public override Task<IDeckDialogReference> ShowDialogAsync<TDialog>(DeckDialogParameters parameters)
        => _onShowDialog is not null ? _onShowDialog(null, parameters) : base.ShowDialogAsync<TDialog>(parameters);

    public override Task<IDeckDialogReference> ShowPanelAsync<TDialog>(object content, DeckDialogParameters parameters)
        => _onShowDialog is not null ? _onShowDialog(content, parameters) : base.ShowPanelAsync<TDialog>(content, parameters);

    public override Task<IDeckDialogReference> ShowPanelAsync<TDialog>(DeckDialogParameters parameters)
        => _onShowDialog is not null ? _onShowDialog(null, parameters) : base.ShowPanelAsync<TDialog>(parameters);

    public override Task<IDeckDialogReference> ShowMessageBoxAsync(DeckDialogParameters<DeckMessageBoxContent> parameters)
        => _onShowDialog is not null ? _onShowDialog(parameters.Content, parameters) : base.ShowMessageBoxAsync(parameters);
}

/// <summary>A minimal dialog reference test double.</summary>
public sealed class TestDialogReference : IDeckDialogReference
{
    public TestDialogReference(string? id = null)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
    }

    public string Id { get; }

    public Task<DeckDialogResult> Result { get; } = Task.FromResult(DeckDialogResult.Ok());

    public Task CloseAsync() => Task.CompletedTask;

    public Task CloseAsync(DeckDialogResult result) => Task.CompletedTask;
}
