// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Projects;

internal abstract record UpdateStep(string Description, Func<Task> Callback)
{
    /// <summary>
    /// Gets the formatted display text using Spectre Console markup for enhanced visual presentation.
    /// </summary>
    public virtual string GetFormattedDisplayText() => Description;
}

/// <summary>
/// Represents an update step for a package reference, containing package and project information.
/// </summary>
internal record PackageUpdateStep(
    string Description,
    Func<Task> Callback,
    string PackageId,
    string CurrentVersion,
    string NewVersion,
    FileInfo ProjectFile) : UpdateStep(Description, Callback)
{
    public override string GetFormattedDisplayText()
    {
        return $"[bold yellow]{PackageId.EscapeMarkup()}[/] [bold green]{CurrentVersion.EscapeMarkup()}[/] to [bold green]{NewVersion.EscapeMarkup()}[/]";
    }
}

/// <summary>
/// Represents an update step that rewrites <c>aspire.config.json#channel</c> when the
/// resolved update channel differs from the project's currently-pinned channel.
/// </summary>
internal record ChannelUpdateStep(
    string Description,
    Func<Task> Callback,
    string? CurrentChannel,
    string? NewChannel,
    string? CurrentChannelDisplay = null,
    string? NewChannelDisplay = null) : UpdateStep(Description, Callback)
{
    public static ChannelUpdateStep? Create(
        string? currentChannel,
        PackageChannel resolvedChannel,
        Func<string?, Task> callback)
    {
        var newChannel = resolvedChannel.GetPersistedChannelName();
        if (string.Equals(currentChannel, newChannel, StringComparisons.CliInputOrOutput))
        {
            return null;
        }

        var currentChannelDisplay = currentChannel ?? PackageChannelNames.Default;
        var newChannelDisplay = resolvedChannel.Name;
        var description = string.Format(
            CultureInfo.InvariantCulture,
            UpdateCommandStrings.UpdateChannelStepDescriptionFormat,
            currentChannelDisplay,
            newChannelDisplay);

        return new ChannelUpdateStep(
            description,
            () => callback(newChannel),
            currentChannel,
            newChannel,
            currentChannelDisplay,
            newChannelDisplay);
    }

    public override string GetFormattedDisplayText()
    {
        var currentText = CurrentChannelDisplay ?? CurrentChannel;
        var newText = NewChannelDisplay ?? NewChannel;
        var current = string.IsNullOrEmpty(currentText)
            ? $"[grey]{UpdateCommandStrings.ChannelNonePlaceholder.EscapeMarkup()}[/]"
            : $"[bold green]{currentText.EscapeMarkup()}[/]";
        var next = string.IsNullOrEmpty(newText)
            ? $"[grey]{UpdateCommandStrings.ChannelNonePlaceholder.EscapeMarkup()}[/]"
            : $"[bold green]{newText.EscapeMarkup()}[/]";
        return $"[bold yellow]aspire.config.json#channel[/] {current} to {next}";
    }
}
