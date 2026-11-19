// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class SelectResourceOptions<TValue>
{
    [Parameter, EditorRequired]
    public required ConcurrentDictionary<TValue, bool> Values { get; set; }

    [Parameter, EditorRequired]
    public required Func<Task> OnAllValuesCheckedChangedAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<TValue, bool, Task> OnValueVisibilityChangedAsync { get; set; }

    [Parameter]
    public string? Id { get; set; }

    [Parameter]
    public Func<TValue, string>? GetLabel { get; set; }

    private async Task OnAllValuesCheckedChangedInternalAsync(bool? newAreAllVisible)
    {
        SetCheckState(newAreAllVisible, Values);
        await OnAllValuesCheckedChangedAsync();
    }

    private Task OnValueVisibilityChangedInternalAsync(TValue value, bool isVisible)
    {
        Values[value] = isVisible;
        return OnValueVisibilityChangedAsync(value, isVisible);
    }

    private static void SetCheckState(bool? newAreAllVisible, ConcurrentDictionary<TValue, bool> values)
    {
        if (newAreAllVisible is null)
        {
            return;
        }

        foreach (var key in values.Keys)
        {
            values[key] = newAreAllVisible.Value;
        }
    }

    private static bool? GetCheckState(ConcurrentDictionary<TValue, bool> values)
    {
        if (values.IsEmpty)
        {
            return true;
        }

        var areAllChecked = true;
        var areAllUnchecked = true;

        foreach (var value in values.Values)
        {
            if (value)
            {
                areAllUnchecked = false;
            }
            else
            {
                areAllChecked = false;
            }
        }

        if (areAllChecked)
        {
            return true;
        }

        if (areAllUnchecked)
        {
            return false;
        }

        return null;
    }
}
