// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.ResourceGraph;

public sealed class IconDto
{
    public required string Path { get; init; }
    public required string Color { get; init; }
    public required string? Tooltip { get; init; }
}

// Game is null where the graph doesn't offer the Aspire Outage game (the previous dashboard experience). The graph script
// only wires up the Play button and the O key when it receives the game's strings.
internal sealed record GraphIconsDto(GraphMenuIconDto Menu, GraphHiddenIconDto Hidden, GraphGameStringsDto? Game);

internal sealed record GraphMenuIconDto(string Path, string LabelFormat);

internal sealed record GraphHiddenIconDto(string Path, string Tooltip);

internal sealed record GraphGameStringsDto(string Instructions, string Exit, string ExitLabel, string SoundOn, string SoundOff, string ModeNormal, string ModeHardCore, string HardCoreWarning, string HardCoreStopped);
