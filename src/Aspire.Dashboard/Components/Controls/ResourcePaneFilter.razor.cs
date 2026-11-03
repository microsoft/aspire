// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ResourcePaneFilter
{
    // Types and states are what most filtering needs, so they start open. Health and tags open on their own once
    // they filter something. The user's choice to open or close a group wins after that.
    private static readonly string[] s_defaultExpandedGroups = ["types", "states"];

    private readonly Dictionary<string, bool> _expandedOverrides = new(StringComparer.Ordinal);
    private bool _isOpen;

    /// <summary>
    /// A set of values the pane can be filtered by, such as the resource types.
    /// </summary>
    public sealed record FilterGroup(
        string Id,
        string Header,
        ConcurrentDictionary<string, bool> Values,
        Func<ResourceViewModel, IEnumerable<string>> GetKeys,
        Func<string, string>? GetText = null);

    [Parameter, EditorRequired]
    public required string ButtonId { get; set; }

    [Parameter, EditorRequired]
    public required string Title { get; set; }

    [Parameter, EditorRequired]
    public required Func<IReadOnlyList<FilterGroup>> Groups { get; set; }

    /// <summary>
    /// Gets or sets the resources that value counts are taken from, whatever the other filters hide.
    /// </summary>
    [Parameter, EditorRequired]
    public required Func<IEnumerable<ResourceViewModel>> Resources { get; set; }

    /// <summary>
    /// Raised after a filter value changes, so the pane applies it.
    /// </summary>
    [Parameter, EditorRequired]
    public required EventCallback OnChanged { get; set; }

    private bool IsFiltered => Groups().Any(group => group.Values.Values.Any(isVisible => !isVisible));

    private void TogglePopover() => _isOpen = !_isOpen;

    private void Close() => _isOpen = false;

    private void OnOpenedChanged(bool opened) => _isOpen = opened;

    private bool IsExpanded(FilterGroup group, List<KeyValuePair<string, bool>> options) =>
        _expandedOverrides.TryGetValue(group.Id, out var expanded)
            ? expanded
            : s_defaultExpandedGroups.Contains(group.Id) || options.Any(pair => !pair.Value);

    private void ToggleGroup(string groupId, bool isExpanded) => _expandedOverrides[groupId] = !isExpanded;

    private Dictionary<string, Dictionary<string, int>> GetCounts(IReadOnlyList<FilterGroup> groups)
    {
        // Each value shows how many resources it matches, whatever the other filters, so hiding a value doesn't change
        // the counts. The keys are those the filter itself uses, so the counts use the dictionary's comparer.
        var resources = Resources().ToList();
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var counts = new Dictionary<string, int>(group.Values.Comparer);
            foreach (var resource in resources)
            {
                foreach (var key in group.GetKeys(resource))
                {
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }

            result[group.Id] = counts;
        }

        return result;
    }

    private async Task SetValueAsync(FilterGroup group, string key, bool isVisible)
    {
        group.Values[key] = isVisible;
        await OnChanged.InvokeAsync();
    }

    private async Task SetAllAsync(FilterGroup group, bool isVisible)
    {
        foreach (var key in group.Values.Keys)
        {
            group.Values[key] = isVisible;
        }

        await OnChanged.InvokeAsync();
    }

    private async Task ResetAsync()
    {
        foreach (var group in Groups())
        {
            foreach (var key in group.Values.Keys)
            {
                group.Values[key] = true;
            }
        }

        await OnChanged.InvokeAsync();
    }
}
