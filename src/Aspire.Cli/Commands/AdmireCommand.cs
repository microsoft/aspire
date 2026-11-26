// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tui;
using Hex1b;

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
        // Redirected and no-ANSI terminals cannot host a flow app.
        if (!HostEnvironment.SupportsInteractiveOutput || !HostEnvironment.SupportsAnsi)
        {
            var rows = CreateRows(version, commitLine, consoleEnvironment.Out.Profile.Width);
            // The lightest segment uses a half block as a color key; replace it with a
            // full-height shade so the monochrome logo has no horizontal gaps.
            InteractionService.DisplayRawText(string.Join(Environment.NewLine, rows.Select(row => row.Title + row.Logo.Replace('▄', '░'))));
        }
        else
        {
            try
            {
                await new AdmireSplashApp(version).RunAsync(Hex1bTerminal.CreateBuilder()
                    .WithDimensions(consoleEnvironment.Out.Profile.Width, consoleEnvironment.Out.Profile.Height), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C in raw mode cancels the flow, not the command's outer token.
                return CommandResult.Cancelled();
            }
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
}
