// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Edits the tags of a resource. Tags are displayed as chips, and new tags are typed in a combobox that suggests the
/// tags of other resources so tags are reused rather than duplicated with a different spelling.
/// </summary>
public sealed partial class ResourceTagEditor : ComponentBase, IDisposable
{
    private readonly string _id = $"tag-editor-{Guid.NewGuid():N}";
    private IReadOnlyList<string> _tags = [];
    private List<TagOption> _options = [];
    private string _text = string.Empty;
    private bool _isEditing;
    private int _activeIndex = -1;
    private FocusTarget _pendingFocus;
    private ElementReference _input;
    private ElementReference _addButton;

    /// <summary>
    /// Gets or sets the display name of the resource, which tags are keyed by.
    /// </summary>
    [Parameter, EditorRequired]
    public required string ResourceName { get; set; }

    /// <summary>Gets or sets the AppHost-defined tags that cannot be removed from the dashboard.</summary>
    [Parameter]
    public IReadOnlyList<string> AppHostTags { get; set; } = [];

    [Parameter]
    public IReadOnlyList<string> AvailableTags { get; set; } = [];

    [Inject]
    public required ResourceTagStore TagStore { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Resources> Loc { get; init; }

    private string ListboxId => $"{_id}-listbox";

    protected override void OnInitialized()
    {
        TagStore.Changed += OnTagsChanged;
    }

    protected override void OnParametersSet()
    {
        UpdateTags();
        UpdateOptions();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var pendingFocus = _pendingFocus;
        _pendingFocus = FocusTarget.None;

        try
        {
            if (pendingFocus == FocusTarget.Input && _isEditing)
            {
                await _input.FocusAsync();
            }
            else if (pendingFocus == FocusTarget.AddButton && !_isEditing)
            {
                await _addButton.FocusAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit disconnected. There is nothing to focus.
        }
    }

    private string GetOptionId(int index) => $"{_id}-option-{index}";

    private void OnTagsChanged()
    {
        // Tags can change from another browser tab or the resource list, and the event can be raised on any thread.
        _ = InvokeAsync(() =>
        {
            UpdateTags();
            UpdateOptions();
            StateHasChanged();
        });
    }

    private void StartEditing()
    {
        _isEditing = true;
        _text = string.Empty;
        _pendingFocus = FocusTarget.Input;
        UpdateOptions();
    }

    private void CloseEditor()
    {
        _isEditing = false;
        _text = string.Empty;
        UpdateOptions();
    }

    private void UpdateOptions()
    {
        if (!_isEditing)
        {
            _options = [];
            _activeIndex = -1;
            return;
        }

        var text = ResourceTagStore.NormalizeTag(_text);
        var allTags = AvailableTags.Concat(AppHostTags).Concat(TagStore.GetAllTags())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

        // Existing tags are suggested first, with the closest matches at the top, so typing part of a tag and
        // pressing Enter reuses it.
        var options = allTags
            .Where(t => !_tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Where(t => text is null || t.Contains(text, StringComparisons.UserTextSearch))
            .OrderBy(t => text is not null && string.Equals(t, text, StringComparison.OrdinalIgnoreCase) ? 0 : text is not null && t.StartsWith(text, StringComparisons.UserTextSearch) ? 1 : 2)
            .Select(t => new TagOption(t, IsNew: false))
            .ToList();

        if (text is not null && !allTags.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new TagOption(text, IsNew: true));
        }

        _options = options;

        // Nothing is active until the user types or uses the arrow keys, so pressing Enter in an empty input
        // doesn't add a suggestion the user didn't choose.
        _activeIndex = text is null || _options.Count == 0 ? -1 : 0;
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown" when _options.Count > 0:
                _activeIndex = (_activeIndex + 1) % _options.Count;
                break;
            case "ArrowUp" when _options.Count > 0:
                _activeIndex = _activeIndex <= 0 ? _options.Count - 1 : _activeIndex - 1;
                break;
            case "Enter":
                if (_activeIndex >= 0 && _activeIndex < _options.Count)
                {
                    AddTag(_options[_activeIndex].Tag);
                }
                else if (ResourceTagStore.NormalizeTag(_text) is { } tag)
                {
                    AddTag(tag);
                }
                break;
            case "Escape":
                CloseEditor();
                _pendingFocus = FocusTarget.AddButton;
                break;
            case "Backspace" when _text.Length == 0 && _tags.Count > 0:
                // Like other token inputs, Backspace in an empty input removes the last tag.
                if (_tags.LastOrDefault(t => !IsAppHostTag(t)) is { } removableTag)
                {
                    RemoveTag(removableTag);
                }
                break;
        }
    }

    private void AddTag(string tag)
    {
        if (!IsAppHostTag(tag))
        {
            TagStore.AddTag(ResourceName, tag);
        }
        UpdateTags();
        _text = string.Empty;
        _pendingFocus = FocusTarget.Input;
        UpdateOptions();
    }

    private void RemoveTag(string tag)
    {
        if (IsAppHostTag(tag))
        {
            return;
        }
        TagStore.RemoveTag(ResourceName, tag);
        UpdateTags();
        UpdateOptions();
    }

    private bool IsAppHostTag(string tag) => AppHostTags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    private void UpdateTags() =>
        _tags = AppHostTags.Concat(TagStore.GetTags(ResourceName)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Dispose()
    {
        TagStore.Changed -= OnTagsChanged;
    }

    private enum FocusTarget
    {
        None,
        Input,
        AddButton
    }

    private sealed record TagOption(string Tag, bool IsNew);
}
