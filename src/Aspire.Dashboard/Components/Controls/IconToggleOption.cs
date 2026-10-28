// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// An option of an <see cref="IconToggleGroup{TValue}"/>: its value, its icon and its name.
/// </summary>
public sealed record IconToggleOption<TValue>(TValue Value, Icon Icon, string Text, string? BadgeText = null);
