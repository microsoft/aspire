// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies the icon to use when displaying a resource in the dashboard.
/// </summary>
/// <param name="iconName">The name of the FluentUI icon to use.</param>
/// <param name="iconVariant">The variant of the icon (Regular or Filled).</param>
[DebuggerDisplay("Type = {GetType().Name,nq}, IconName = {IconName}, IconVariant = {IconVariant}")]
public sealed class ResourceIconAnnotation(string iconName, IconVariant iconVariant = IconVariant.Filled) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the FluentUI icon to use for the resource.
    /// </summary>
    /// <remarks>
    /// The icon name should be a valid FluentUI icon name. 
    /// See https://aka.ms/fluentui-system-icons for available icons.
    /// </remarks>
    public string IconName { get; } = iconName ?? throw new ArgumentNullException(nameof(iconName));

    /// <summary>
    /// Gets the variant of the icon (Regular or Filled).
    /// </summary>
    public IconVariant IconVariant { get; } = iconVariant;

    /// <summary>
    /// Gets the custom image as a base64 image data URI. When set, it takes precedence over <see cref="IconName"/>.
    /// </summary>
    public string? IconData { get; init; }
}