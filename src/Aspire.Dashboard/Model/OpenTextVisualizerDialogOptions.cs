// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Options for opening the text visualizer dialog.
/// </summary>
public sealed class OpenTextVisualizerDialogOptions
{
    public required DashboardDialogService DialogService { get; init; }
    public required string ValueDescription { get; init; }
    public required string Value { get; init; }
    public bool ContainsSecret { get; init; }
    public string? DownloadFileName { get; init; }

    /// <summary>
    /// If set, the dialog will use this format and hide the format dropdown.
    /// </summary>
    public string? FixedFormat { get; init; }
}
