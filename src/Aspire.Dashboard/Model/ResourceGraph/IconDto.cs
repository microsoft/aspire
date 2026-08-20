// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.ResourceGraph;

public sealed class IconDto
{
    public required string Path { get; init; }
    public required string Color { get; init; }
    public required string? Tooltip { get; init; }
}

internal sealed record GraphIconsDto(GraphMenuIconDto Menu);

internal sealed record GraphMenuIconDto(string Path, string LabelFormat);
