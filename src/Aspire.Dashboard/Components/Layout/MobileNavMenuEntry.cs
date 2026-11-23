// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// An entry in the narrow layout's navigation menu.
/// </summary>
/// <param name="Text">The entry's label.</param>
/// <param name="OnClick">Runs after the menu closes when the entry is chosen.</param>
/// <param name="Icon">The entry's icon.</param>
/// <param name="ActiveIcon">The icon shown while the entry is current, if it differs from <paramref name="Icon"/>.</param>
/// <param name="LinkMatchRegex">Matches the URLs for which the entry is current.</param>
/// <param name="DisabledText">When set, the entry is disabled unless it's current, and this explains why.</param>
/// <param name="IsActive">Whether the entry is current. When set, <paramref name="LinkMatchRegex"/> isn't used.</param>
/// <param name="ErrorCount">A count of unviewed errors shown as a badge on the entry.</param>
public record MobileNavMenuEntry(
    string Text,
    Func<Task> OnClick,
    Icon? Icon = null,
    Icon? ActiveIcon = null,
    Regex? LinkMatchRegex = null,
    string? DisabledText = null,
    bool? IsActive = null,
    int ErrorCount = 0);
