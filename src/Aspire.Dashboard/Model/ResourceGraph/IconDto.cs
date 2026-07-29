// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.ResourceGraph;

public sealed class IconDto
{
    /// <summary>
    /// Inner SVG markup (paths/rects/circles) for a Deck stroke icon, rendered into an SVG group as
    /// <c>innerHTML</c> with <c>stroke</c> = <see cref="Color"/> and no fill. Used by the resource type icon.
    /// </summary>
    public string? Svg { get; init; }

    public required string Color { get; init; }
    public required string? Tooltip { get; init; }
}
