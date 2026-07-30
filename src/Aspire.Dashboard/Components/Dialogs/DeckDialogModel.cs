// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Extensions;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

/// <summary>
/// Deck-native dialog framework primitives, replacing the the previous UI library dialog service/provider used by
/// the dashboard for the Deck redesign. Distinct Deck-prefixed type names are used so this
/// framework can coexist with the (still-present) <c>Microsoft.the previous UI library.AspNetCore.Components</c>
/// namespace that other, not-yet-converted components import through <c>_Imports.razor</c>.
/// </summary>
public enum DeckDialogAlignment
{
    /// <summary>Centered modal.</summary>
    Default,
    /// <summary>Panel docked to the start (left) edge.</summary>
    Start,
    /// <summary>Panel docked to the end (right) edge.</summary>
    End,
}

/// <summary>How a dialog is presented.</summary>
public enum DeckDialogType
{
    /// <summary>A centered modal dialog.</summary>
    Dialog,
    /// <summary>A full-height side panel.</summary>
    Panel,
    /// <summary>A message box with primary/secondary actions.</summary>
    MessageBox,
}

/// <summary>The intent/severity of a message box.</summary>
public enum DeckMessageIntent
{
    /// <summary>Informational.</summary>
    Info,
    /// <summary>Warning.</summary>
    Warning,
    /// <summary>Error.</summary>
    Error,
    /// <summary>Success.</summary>
    Success,
    /// <summary>Confirmation prompt.</summary>
    Confirmation,
}

/// <summary>Parameters describing how a dialog is shown.</summary>
public class DeckDialogParameters
{
    /// <summary>The dialog title, rendered in the default header when the content supplies none.</summary>
    public string? Title { get; set; }

    /// <summary>Optional explicit width (CSS length).</summary>
    public string? Width { get; set; }

    /// <summary>Optional explicit height (CSS length).</summary>
    public string? Height { get; set; }

    /// <summary>Whether the dialog is modal (renders a blocking overlay). Defaults to <see langword="true"/>.</summary>
    public bool Modal { get; set; } = true;

    /// <summary>Whether focus is trapped within the dialog. Defaults to <see langword="true"/>.</summary>
    public bool TrapFocus { get; set; } = true;

    /// <summary>Placement of the dialog. <see cref="DeckDialogAlignment.End"/> renders a right-docked panel.</summary>
    public DeckDialogAlignment Alignment { get; set; } = DeckDialogAlignment.Default;

    /// <summary>Whether to prevent scrolling of the page behind the dialog.</summary>
    public bool PreventScroll { get; set; }

    /// <summary>Whether clicking the overlay dismisses the dialog. Defaults to allowing dismissal.</summary>
    public bool PreventDismissOnOverlayClick { get; set; }

    /// <summary>An optional stable id for the dialog (used to prevent duplicate page dialogs).</summary>
    public string? Id { get; set; }

    /// <summary>Accessible title for the dismiss button.</summary>
    public string? DismissTitle { get; set; }

    /// <summary>Whether the default header shows a dismiss button. Defaults to <see langword="true"/>.</summary>
    public bool ShowDismiss { get; set; } = true;

    /// <summary>Text for the primary action button (null/empty hides it).</summary>
    public string? PrimaryAction { get; set; }

    /// <summary>Whether the primary action button is enabled.</summary>
    public bool PrimaryActionEnabled { get; set; } = true;

    /// <summary>Text for the secondary action button (null/empty hides it).</summary>
    public string? SecondaryAction { get; set; }

    /// <summary>Whether the secondary action button is enabled.</summary>
    public bool SecondaryActionEnabled { get; set; } = true;

    /// <summary>How the dialog is presented.</summary>
    public DeckDialogType DialogType { get; set; } = DeckDialogType.Dialog;

    /// <summary>Invoked just before the dialog closes.</summary>
    public EventCallback<DeckDialogInstance> OnDialogClosing { get; set; }

    /// <summary>Invoked with the result after the dialog closes.</summary>
    public EventCallback<DeckDialogResult> OnDialogResult { get; set; }
}

/// <summary>Parameters that also carry strongly-typed content passed to the dialog component.</summary>
/// <typeparam name="TContent">The content type passed to the dialog's <c>Content</c> parameter.</typeparam>
public class DeckDialogParameters<TContent> : DeckDialogParameters
{
    /// <summary>The content passed to the dialog component.</summary>
    public TContent Content { get; set; } = default!;
}

/// <summary>The content model for a message box dialog.</summary>
public class DeckMessageBoxContent
{
    /// <summary>The message box intent/severity.</summary>
    public DeckMessageIntent Intent { get; set; }

    /// <summary>Optional leading icon.</summary>
    public DeckIconName? Icon { get; set; }

    /// <summary>Optional CSS color/class applied to the icon.</summary>
    public string? IconColor { get; set; }

    /// <summary>Optional title.</summary>
    public string? Title { get; set; }

    /// <summary>Plain-text message (used when <see cref="MarkupMessage"/> is not set).</summary>
    public string? Message { get; set; }

    /// <summary>HTML message content.</summary>
    public MarkupString? MarkupMessage { get; set; }
}

/// <summary>The result of a dialog once it closes.</summary>
public sealed class DeckDialogResult
{
    private DeckDialogResult(object? data, bool cancelled)
    {
        Data = data;
        Cancelled = cancelled;
    }

    /// <summary>The data returned by the dialog (if any).</summary>
    public object? Data { get; }

    /// <summary>Whether the dialog was cancelled/dismissed rather than confirmed.</summary>
    public bool Cancelled { get; }

    /// <summary>Creates a successful result carrying <paramref name="data"/>.</summary>
    public static DeckDialogResult Ok(object? data = null) => new(data, cancelled: false);

    /// <summary>Creates a cancelled result.</summary>
    public static DeckDialogResult Cancel(object? data = null) => new(data, cancelled: true);
}

/// <summary>A handle to an opened dialog, used to await its result or close it programmatically.</summary>
public interface IDeckDialogReference
{
    /// <summary>The dialog's id.</summary>
    string Id { get; }

    /// <summary>Completes with the dialog's result when it closes.</summary>
    Task<DeckDialogResult> Result { get; }

    /// <summary>Closes the dialog as cancelled.</summary>
    Task CloseAsync();

    /// <summary>Closes the dialog with the specified result.</summary>
    Task CloseAsync(DeckDialogResult result);
}

/// <summary>Marker interface implemented by dialog content components.</summary>
public interface IDeckDialogContentComponent
{
}

/// <summary>Implemented by dialog content components that receive strongly-typed content.</summary>
/// <typeparam name="TContent">The content type.</typeparam>
public interface IDeckDialogContentComponent<TContent> : IDeckDialogContentComponent
{
    /// <summary>The content passed to the dialog.</summary>
    TContent Content { get; set; }
}

/// <summary>Read-only view of a dialog instance's identity and parameters.</summary>
public sealed record DeckDialogInstanceInfo(string Id, DeckDialogParameters Parameters);

/// <summary>
/// The cascading value supplied to a dialog's content component (named <c>Dialog</c> by convention).
/// Exposes the instance's parameters and lets the content close/show/hide itself.
/// </summary>
public sealed class DeckDialogInstance
{
    private readonly Func<DeckDialogResult, Task> _close;
    private readonly Func<bool, Task> _setVisible;

    internal DeckDialogInstance(string id, DeckDialogParameters parameters, Func<DeckDialogResult, Task> close, Func<bool, Task> setVisible)
    {
        Id = id;
        Parameters = parameters;
        _close = close;
        _setVisible = setVisible;
    }

    /// <summary>The dialog id.</summary>
    public string Id { get; }

    /// <summary>The dialog parameters.</summary>
    public DeckDialogParameters Parameters { get; }

    /// <summary>
    /// A stable HTML element id for the dialog's title region (the <c>DeckDialogHeader</c> content).
    /// The provider points the dialog's <c>aria-labelledby</c> at this id and the header stamps it on
    /// its visible title content so the accessible name comes from the on-screen heading. Derived from
    /// <see cref="Id"/> (sanitized for use in an id) so both sides agree without extra plumbing.
    /// </summary>
    public string TitleElementId => $"deck-dialog-title-{Id.SanitizeHtmlId()}";

    /// <summary>An info view of this instance (mirrors the <c>Dialog.Instance.Parameters</c> access pattern).</summary>
    public DeckDialogInstanceInfo Instance => new(Id, Parameters);

    /// <summary>Closes the dialog as cancelled.</summary>
    public Task CloseAsync() => _close(DeckDialogResult.Cancel());

    /// <summary>Closes the dialog as cancelled (alias for <see cref="CloseAsync()"/>).</summary>
    public Task CancelAsync() => _close(DeckDialogResult.Cancel());

    /// <summary>Closes the dialog with the specified result.</summary>
    public Task CloseAsync(DeckDialogResult result) => _close(result);

    /// <summary>Makes the dialog visible again after <see cref="Hide"/>.</summary>
    public Task Show() => _setVisible(true);

    /// <summary>Temporarily hides the dialog without closing it.</summary>
    public Task Hide() => _setVisible(false);
}
