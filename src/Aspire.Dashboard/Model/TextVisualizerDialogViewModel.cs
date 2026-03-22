// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// View model for the text visualizer dialog.
/// </summary>
/// <param name="Text">The text content to display.</param>
/// <param name="Description">The description/title for the dialog.</param>
/// <param name="ContainsSecret">Whether the text contains sensitive data.</param>
/// <param name="DownloadFileName">Optional file name for downloading the content. If null, download is disabled.</param>
/// <param name="FixedFormat">If set, the dialog will use this format and hide the format dropdown.</param>
public record TextVisualizerDialogViewModel(string Text, string Description, bool ContainsSecret, string? DownloadFileName = null, string? FixedFormat = null);
