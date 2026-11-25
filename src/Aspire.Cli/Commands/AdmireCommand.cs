// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Commands;

internal sealed class AdmireCommand(CommonCommandServices services, ConsoleEnvironment consoleEnvironment)
    : BaseCommand("admire", RootCommandStrings.AdmireDescription, services)
{
    private const int LeftMargin = 2;
    private const int TitleWidth = 45;
    private static readonly string[] s_logo =
    [
        "             ██",
        "            █▓▓█",
        "           █▓▓▓▓█",
        "          █▓▓▓▓▓▓█",
        "         █▓▓▓▓▓▓▓▓█",
        "        █▒▒▓▓▓▓▓▓▓▓█",
        "       █▒▒▒▒▓▓▓▓▓▓▓▓█",
        "      █▒▒▒▒▒▒▓▓▓▓▓▓▓▓█",
        "     █▒▒▒▒▒▒▒▒▓▓▓▓▓▓▓▓█",
        "    █▄▄▄░░░░░░░▒▒▒▒▒▒▒▒█",
        "   █▄▄▄▄▄░░░░░░░▒▒▒▒▒▒▒▒█",
        "  █▄▄▄▄▄▄▄░░░░░░░▒▒▒▒▒▒▒▒█",
        " █▄▄▄▄▄▄▄▄▄░░░░░░░▒▒▒▒▒▒▒▒█",
        " ██████████████████████████"
    ];

    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // The resolved identity can include a prerelease label and build metadata:
        // "17.0.0-preview.1+abc123" is displayed as "17.0.0".
        var version = ExecutionContext.IdentityVersion.Split(['-', '+'], 2)[0];
        var commit = IdentityVersionAction.GetCommit(ExecutionContext);
        var commitLine = string.IsNullOrEmpty(commit)
            ? null
            : string.Format(CultureInfo.CurrentCulture, RootCommandStrings.AdmireCommitFormat, commit[..Math.Min(commit.Length, 10)]);
        var rows = CreateRows(version, commitLine, consoleEnvironment.Out.Profile.Width);

        // Live rendering needs cursor control; no-ANSI terminals receive one static frame.
        if (!HostEnvironment.SupportsInteractiveOutput || !HostEnvironment.SupportsAnsi)
        {
            // The lightest segment uses a half block as a color key; replace it with a
            // full-height shade so the monochrome logo has no horizontal gaps.
            InteractionService.DisplayRawText(string.Join(Environment.NewLine, rows.Select(row => row.Title + row.Logo.Replace('▄', '░'))));
        }
        else
        {
            await InteractionService.DisplayLiveAsync(new Text(string.Empty), async update =>
            {
                for (var count = 1; count <= rows.Count; count++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    update(new Rows(rows.Take(count).Select(CreateRenderable)));
                    if (count < rows.Count)
                    {
                        await Task.Delay(40, cancellationToken);
                    }
                }
            });
        }

        return CommandResult.Success();
    }

    private static List<(string Title, string Logo, bool IsWordmark)> CreateRows(string version, string? commit, int width)
    {
        var versionLine = $"                 {version}";
        var commitLine = commit is null ? null : $"             {commit}";
        var titleWidth = Math.Max(TitleWidth, Math.Max(versionLine.Length, commitLine?.Length ?? 0));
        var rows = new List<(string Title, string Logo, bool IsWordmark)> { (string.Empty, string.Empty, false) };

        if (width >= LeftMargin + titleWidth + s_logo.Max(line => line.Length))
        {
            for (var i = 0; i < s_logo.Length; i++)
            {
                var isWordmark = i >= 3 && i < 3 + BannerService.AspireLines.Count;
                var title = isWordmark
                    ? BannerService.AspireLines[i - 3].TrimEnd()
                    : i == 10 ? versionLine : i == 11 ? commitLine ?? string.Empty : string.Empty;
                rows.Add((new string(' ', LeftMargin) + title.PadRight(titleWidth), s_logo[i], isWordmark));
            }
        }
        else
        {
            rows.AddRange(s_logo.Select(line => (new string(' ', LeftMargin), line, false)));
            rows.Add((string.Empty, string.Empty, false));
            rows.AddRange(BannerService.AspireLines.Select(line => (new string(' ', LeftMargin) + line.TrimEnd(), string.Empty, true)));
            rows.Add((string.Empty, string.Empty, false));
            rows.Add((new string(' ', LeftMargin) + versionLine, string.Empty, false));
            if (commitLine is not null)
            {
                rows.Add((new string(' ', LeftMargin) + commitLine, string.Empty, false));
            }
        }

        rows.Add((string.Empty, string.Empty, false));
        return rows;
    }

    private static IRenderable CreateRenderable((string Title, string Logo, bool IsWordmark) row)
    {
        if (row.Title.Length == 0 && row.Logo.Length == 0)
        {
            return new Text(" ");
        }

        var logo = row.Logo
            .Replace("█", "[rgb(81,43,212)]█[/]", StringComparison.Ordinal)
            .Replace("▓", "[rgb(116,85,221)]█[/]", StringComparison.Ordinal)
            .Replace("▒", "[rgb(151,128,229)]█[/]", StringComparison.Ordinal)
            .Replace("░", "[rgb(185,170,238)]█[/]", StringComparison.Ordinal)
            .Replace("▄", "[rgb(220,213,246)]█[/]", StringComparison.Ordinal);
        var title = row.Title.EscapeMarkup();
        if (row.IsWordmark)
        {
            title = title.Replace("▀", "[/][rgb(116,85,221)]▀[/][rgb(81,43,212)]", StringComparison.Ordinal);
        }
        var titleColor = row.IsWordmark ? "rgb(81,43,212)" : "white";
        return new Markup($"[{titleColor}]{title}[/]{logo}");
    }
}
