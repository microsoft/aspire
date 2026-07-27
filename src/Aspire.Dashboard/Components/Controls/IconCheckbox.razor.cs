// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

/// <summary>
/// An icon-only checkbox that exposes proper checkbox semantics to assistive tech.
/// </summary>
/// <remarks>
/// The checkbox semantics live on a focusable <c>span</c> with <c>role=checkbox</c> (rather
/// than a native <c>input</c>) so the icon glyph can fully control the rendered appearance.
/// A small JS helper handles the Space key here so Tab/Shift+Tab keep their native focus
/// behavior while Space cannot scroll the page or bubble to an enclosing grid's row activation.
/// </remarks>
public partial class IconCheckbox : ComponentBase, IAsyncDisposable
{
    private const string JsModulePath = "./Components/Controls/IconCheckbox.razor.js";

    private ElementReference _element;
    private IJSObjectReference? _jsModule;
    private bool _keyboardInitialized;

    [Inject]
    public required IJSRuntime JS { get; init; }

    /// <summary>
    /// The checked state of the checkbox. Determines the rendered icon and the exposed
    /// <c>aria-checked</c> value.
    /// </summary>
    [Parameter]
    public required IconCheckboxState CheckState { get; set; }

    /// <summary>
    /// The accessible name used for both <c>title</c> and <c>aria-label</c>.
    /// </summary>
    [Parameter]
    public required string AccessibleLabel { get; set; }

    /// <summary>
    /// Invoked when the checkbox is activated (click or Space).
    /// </summary>
    [Parameter]
    public EventCallback OnClick { get; set; }

    /// <summary>
    /// When <c>true</c>, exposes the checkbox as disabled, removes it from the tab order, and
    /// skips the Space-key handler.
    /// </summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>
    /// Additional CSS classes appended to the root element.
    /// </summary>
    [Parameter]
    public string? CssClass { get; set; }

    /// <summary>
    /// Whether the click event should be prevented from propagating to ancestors.
    /// Defaults to <c>true</c> so the checkbox does not also activate a surrounding row.
    /// </summary>
    [Parameter]
    public bool StopPropagation { get; set; } = true;

    // The control owns the mapping from state to icon so callers only describe the
    // checked state via CheckState rather than wiring up icons and aria values themselves.
    private DeckIconName CurrentIcon => CheckState switch
    {
        IconCheckboxState.Checked => DeckIconName.CheckboxChecked,
        IconCheckboxState.Unchecked => DeckIconName.CheckboxUnchecked,
        _ => DeckIconName.CheckboxIndeterminate
    };

    // The indeterminate state maps to aria-checked="mixed" per the ARIA checkbox spec.
    // See: https://www.w3.org/TR/wai-aria-1.2/#checkbox
    private string AriaChecked => CheckState switch
    {
        IconCheckboxState.Checked => "true",
        IconCheckboxState.Unchecked => "false",
        _ => "mixed"
    };

    // Render aria-disabled only when disabled so the attribute is omitted in the common case,
    // which also keeps the CSS/keyboard handler checks (aria-disabled="true") working.
    private string? AriaDisabled => Disabled ? "true" : null;

    // Disabled checkboxes are removed from the tab order but remain focusable via pointer.
    private string TabIndex => Disabled ? "-1" : "0";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", JsModulePath);
            await _jsModule.InvokeVoidAsync("initializeIconCheckboxKeyboard", _element);
            _keyboardInitialized = true;
        }
    }

    private async Task HandleClickAsync()
    {
        if (Disabled)
        {
            return;
        }

        await OnClick.InvokeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_jsModule is not null)
        {
            if (_keyboardInitialized)
            {
                try
                {
                    await _jsModule.InvokeVoidAsync("disposeIconCheckboxKeyboard", _element);
                }
                catch (JSDisconnectedException)
                {
                    // The browser may already be gone when the component is disposed.
                }
                catch (OperationCanceledException)
                {
                    // The browser may already be gone when the component is disposed.
                }
            }

            await JSInteropHelpers.SafeDisposeAsync(_jsModule);
        }
    }
}

/// <summary>
/// The checked state of an <see cref="IconCheckbox"/>.
/// </summary>
public enum IconCheckboxState
{
    /// <summary>
    /// The checkbox is unchecked (aria-checked="false").
    /// </summary>
    Unchecked,

    /// <summary>
    /// The checkbox is checked (aria-checked="true").
    /// </summary>
    Checked,

    /// <summary>
    /// The checkbox is partially checked (aria-checked="mixed").
    /// </summary>
    Indeterminate
}
