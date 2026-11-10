// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.OpenCode;
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
        interactionService.DisplaySourceTrustWarning(executionContext);
    }

    public static void DisplaySkillSources(this IInteractionService interactionService, IReadOnlyList<SkillSource> sources, IReadOnlyList<IAgentClient> clients, CliExecutionContext executionContext)
    {
        var usesPlugins = clients.Any(client => client.Id != OpenCodeAgentClient.ClientId);
        var usesOpenCode = clients.Any(client => client.Id == OpenCodeAgentClient.ClientId);
        List<IReadOnlyList<(string Label, string Value)>> sourceDetails = [];
        foreach (var skillSource in sources)
        {
            var sourceName = skillSource.GetName();
            if (sourceName.Equals(SkillSource.DefaultName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            List<(string Label, string Value)> details = [(SharedCommandStrings.SourceName, sourceName)];
            if (usesPlugins)
            {
                details.Add((SharedCommandStrings.SourceGitRepository, skillSource.GetRepositoryUrl()));
                if (skillSource.Ref is not null)
                {
                    details.Add((SharedCommandStrings.SourceRef, skillSource.Ref));
                }
                if (!string.Equals(skillSource.Marketplace, sourceName, StringComparison.OrdinalIgnoreCase))
                {
                    details.Add((SharedCommandStrings.SourceMarketplace, skillSource.Marketplace!));
                }
                details.Add((SharedCommandStrings.SourcePlugins, string.Join(", ", skillSource.GetPluginNames())));
            }
            if (usesOpenCode)
            {
                if (skillSource.OpenCodeV1Catalog is not null)
                {
                    details.Add((SharedCommandStrings.SourceOpenCodeV1Catalog, skillSource.OpenCodeV1Catalog));
                }
                if (skillSource.OpenCodeV2Catalog is not null)
                {
                    details.Add((SharedCommandStrings.SourceOpenCodeV2Catalog, skillSource.OpenCodeV2Catalog));
                }
            }
            if (details.Count > 1)
            {
                sourceDetails.Add(details);
            }
        }

        if (sourceDetails.Count > 0)
        {
            interactionService.DisplayMessage(KnownEmojis.Information, AgentCommandStrings.InitCommand_SkillSourcesExplanation);
            interactionService.DisplaySourceDetails(sourceDetails);
            if (interactionService.DisplaySourceTrustWarning(executionContext))
            {
                interactionService.DisplayEmptyLine();
            }
        }
    }

    // Returns true when any target failed to register the skill sources.
    public static bool DisplayRemoteConfigurationResults(this IInteractionService interactionService,
        IReadOnlyList<AgentTargetResult> results, DirectoryInfo workspaceRoot, CliExecutionContext executionContext)
    {
        var hasErrors = false;
        foreach (var resultGroup in results.GroupBy(result => result.Status switch
        {
            AgentConfigurationStatus.Configured or AgentConfigurationStatus.Unchanged => false,
            AgentConfigurationStatus.Failed => true,
            _ => throw new UnreachableException()
        }))
        {
            if (resultGroup.Key)
            {
                var failedTargets = resultGroup.SelectMany(result => result.Clients, (result, client) =>
                {
                    var target = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_ConfigurationTarget,
                        client.DisplayName, AgentPath.GetDisplayPath(result.TargetPath, workspaceRoot, executionContext));

                    return result.Message is null ? target : $"{target}{Environment.NewLine}  {result.Message}";
                });
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedTarget,
                    string.Join($"{Environment.NewLine}  ", failedTargets)));
                interactionService.DisplayEmptyLine();
                hasErrors = true;
                continue;
            }

            var targets = resultGroup.Select(result =>
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocationWithAgents,
                    AgentPath.GetDisplayPath(result.TargetPath, workspaceRoot, executionContext),
                    string.Join(", ", result.Clients.Select(static client => client.DisplayName))));
            interactionService.DisplayMessage(KnownEmojis.Robot,
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_ConfiguredTargets,
                    string.Join(", ", targets)));
            interactionService.DisplayEmptyLine();
        }

        return hasErrors;
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
            SharedCommandStrings.GitFetchTemplates, source, mayPromptForAuthentication);
    }

    public static void DisplayGitFetchNotice(this IInteractionService interactionService, string content, string source, bool mayPromptForAuthentication)
    {
        var message = mayPromptForAuthentication
            ? SharedCommandStrings.FetchingSourceWithAuthentication
            : SharedCommandStrings.FetchingSource;
        interactionService.DisplayMessage(KnownEmojis.InboxTray,
            $"[blue]{string.Format(CultureInfo.CurrentCulture, message, content, source).EscapeMarkup()}[/]",
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

    public static bool DisplaySourceTrustWarning(this IInteractionService interactionService, CliExecutionContext executionContext)
    {
        if (!executionContext.TryMarkSourceTrustWarningDisplayed())
        {
            return false;
        }

        interactionService.DisplayMessage(KnownEmojis.PoliceCarLight, $"[yellow]{SharedCommandStrings.SourceTrustWarning.EscapeMarkup()}[/]", allowMarkup: true, consoleOverride: ConsoleOutput.Error);
        return true;
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
