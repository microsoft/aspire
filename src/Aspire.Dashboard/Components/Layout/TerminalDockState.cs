// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Layout;

public sealed class TerminalDockState
{
    public event Action? Changed;

    public bool IsVisible { get; private set; }

    public string? ResourceName { get; private set; }

    public bool IsResourceOpen(string resourceName)
        => IsVisible && string.Equals(ResourceName, resourceName, StringComparisons.ResourceName);

    internal void Update(bool isVisible, string? resourceName)
    {
        if (IsVisible == isVisible &&
            string.Equals(ResourceName, resourceName, StringComparisons.ResourceName))
        {
            return;
        }

        IsVisible = isVisible;
        ResourceName = resourceName;
        Changed?.Invoke();
    }
}
