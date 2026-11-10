// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides shared helpers for console output formatting.
/// </summary>
internal static class ConsoleHelpers
{
    public static void DisplaySourceDetails(this IInteractionService interactionService, IEnumerable<IReadOnlyList<(string Label, string Value)>> sources)
    {
        var details = sources.Where(source => source.Count > 0).ToArray();
        if (details.Length == 0)
        {
            return;
        }

        var labelWidth = details.SelectMany(source => source).Max(detail => new Segment(detail.Label).CellCount()) + 1;
        interactionService.DisplayEmptyLine();
        foreach (var source in details)
        {
            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.Columns[0].Width = labelWidth;
            grid.Columns[0].NoWrap = true;
            foreach (var (label, value) in source)
            {
                grid.AddRow(
                    new Align(new Markup($"[bold green]{label.EscapeMarkup()}[/]:"), HorizontalAlignment.Right),
                    new Text(value));
            }
            interactionService.DisplayRenderable(new Padder(grid, new Padding(3, 0)));
            interactionService.DisplayEmptyLine();
        }
    }

    public static void DisplayTemplateSources(this IInteractionService interactionService, IEnumerable<(string Name, TemplateSource Source)> sources, CliExecutionContext executionContext)
    {
        var details = sources.Select(source =>
        {
            List<(string Label, string Value)> rows = [(SharedCommandStrings.SourceName, source.Name)];
            if (source.Source.Repository is { } repository)
            {
                rows.Add((SharedCommandStrings.SourceGitRepository, repository));
                if (source.Source.Path is not ("." or "./" or ".\\"))
                {
                    rows.Add((SharedCommandStrings.SourceGitPath, source.Source.Path));
                }
                if (source.Source.RepositoryRef is not null and not "HEAD")
                {
                    rows.Add((SharedCommandStrings.SourceRef, source.Source.RepositoryRef));
                }
            }
            else
            {
                rows.Add((SharedCommandStrings.SourcePath, source.Source.Path));
            }
            return rows;
        }).ToArray();
        if (details.Length == 0)
        {
            return;
        }

        interactionService.DisplayMessage(KnownEmojis.Information, NewCommandStrings.TemplateSourceExplanation);
        interactionService.DisplaySourceDetails(details);
        // The inventory includes its warning; avoid repeating it during Git acquisition.
        executionContext.TryMarkSourceTrustWarningDisplayed();
        interactionService.DisplaySourceTrustWarning();
    }

    public static void DisplayTemplateGitFetchNotice(this IInteractionService interactionService,
        string repository, string path, string? reference, bool mayPromptForAuthentication)
    {
        var source = PackageSourceRedactor.RedactForDisplay(repository);
        List<string> qualifiers = [];
        if (path is not ("." or "./" or ".\\"))
        {
            qualifiers.Add($"{SharedCommandStrings.SourceGitPath}: {path}");
        }
        if (reference is not null and not "HEAD")
        {
            qualifiers.Add($"{SharedCommandStrings.SourceRef}: {reference}");
        }
        if (qualifiers.Count > 0)
        {
            source += $" ({string.Join(", ", qualifiers)})";
        }

        // Identify the source before Git can prompt for credentials. With basic auth,
        // an ambiguous prompt could send a user's password to the wrong Git server,
        // where that server could log it.
        interactionService.DisplayGitFetchNotice(
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.GitFetchTemplatesFromSource, source),
            mayPromptForAuthentication);
    }

    public static void DisplayGitFetchNotice(this IInteractionService interactionService, string content, bool mayPromptForAuthentication)
    {
        var message = mayPromptForAuthentication
            ? SharedCommandStrings.FetchingSourceWithAuthentication
            : SharedCommandStrings.FetchingSource;
        interactionService.DisplayMessage(KnownEmojis.InboxTray,
            $"[blue]{string.Format(CultureInfo.CurrentCulture, message, content).EscapeMarkup()}[/]",
            allowMarkup: true,
            consoleOverride: ConsoleOutput.Error);
    }

    public static string GetUniqueSelectionLabel(string baseLabel, HashSet<string> labels)
    {
        var label = baseLabel;
        var suffix = 2;
        // Flat clients use formatted labels as dictionary keys even when source names collide.
        while (!labels.Add(label))
        {
            label = string.Create(CultureInfo.InvariantCulture, $"{baseLabel} [{suffix++}]");
        }

        return label;
    }

    public static void DisplaySourceTrustWarning(this IInteractionService interactionService, CliExecutionContext executionContext)
    {
        // An init source can lead straight into Git template discovery in "aspire new".
        // Share the warning across both paths for this invocation, without skipping either consent prompt.
        if (!executionContext.TryMarkSourceTrustWarningDisplayed())
        {
            return;
        }

        interactionService.DisplaySourceTrustWarning();
    }

    public static void DisplaySourceTrustWarning(this IInteractionService interactionService)
    {
        interactionService.DisplayMessage(KnownEmojis.PoliceCarLight, $"[yellow]{SharedCommandStrings.SourceTrustWarning.EscapeMarkup()}[/]", allowMarkup: true, consoleOverride: ConsoleOutput.Error);
    }

    public static void DisplayNuGetSearchLimitWarning(this IInteractionService interactionService)
    {
        var message = string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.NuGetSearchLimitWarning,
            NuGetSearchLimits.MaxResults);
        interactionService.DisplayMessage(KnownEmojis.PoliceCarLight, $"[yellow]{message.EscapeMarkup()}[/]", allowMarkup: true, consoleOverride: ConsoleOutput.Error);
    }

    public static void DisplayNuGetSearchFailureWarnings(this IInteractionService interactionService, IEnumerable<string> failedSources)
    {
        foreach (var source in failedSources.Select(PackageSourceRedactor.RedactForDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var message = string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.NuGetSearchSourceFailedWarning, source);
            interactionService.DisplayMessage(KnownEmojis.PoliceCarLight, $"[yellow]{message.EscapeMarkup()}[/]", allowMarkup: true, consoleOverride: ConsoleOutput.Error);
        }
    }

    /// <summary>
    /// Formats an emoji prefix with trailing space for aligned console output.
    /// </summary>
    public static string FormatEmojiPrefix(KnownEmoji emoji, IAnsiConsole console, bool replaceEmoji = false, bool suppressColor = false)
    {
        const int emojiTargetWidth = 3; // 2 for emoji and 1 trailing space

        var cellLength = EmojiWidth.GetCachedCellWidth(emoji.Name, console);
        var padding = Math.Max(1, emojiTargetWidth - cellLength);
        var spectreEmojiText = $":{emoji.Name}:";

        if (replaceEmoji)
        {
            return Emoji.Replace(spectreEmojiText) + new string(' ', padding);
        }

        // Wrap in a color tag so monochrome text-presentation glyphs get a visible tint.
        // Terminals that render full-color emoji glyphs ignore ANSI foreground color, so this is always safe.
        // There is an option to suppress it in scenarios where the emoji is added to text inside an existing color.
        if (!suppressColor && emoji.TextColor is { } color)
        {
            return $"[{color}]{spectreEmojiText}[/]" + new string(' ', padding);
        }

        return spectreEmojiText + new string(' ', padding);
    }

    /// <summary>
    /// Creates a two-column grid with an emoji icon in the first column and content in the second.
    /// The first column is fixed-width so wrapped text in the second column stays aligned
    /// without pushing under the icon.
    /// </summary>
    public static Grid CreateEmojiGrid(KnownEmoji emoji, IAnsiConsole console, IRenderable content)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.Columns[0].NoWrap = true;
        grid.Columns[0].Padding = new Padding(0);
        grid.Columns[1].Padding = new Padding(0);

        grid.AddRow(
            new Markup(FormatEmojiPrefix(emoji, console)),
            content);

        return grid;
    }
}
