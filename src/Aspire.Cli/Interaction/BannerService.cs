// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Interaction;

/// <summary>
/// Provides functionality to display an animated Aspire CLI banner using Spectre.Console.
/// </summary>
internal sealed class BannerService : IBannerService
{
    // Aspire brand colors
    private static readonly Color s_purpleAccent = new(81, 43, 212);    // #512BD4 - Aspire brand purple
    private static readonly Color s_purpleDark = new(116, 85, 221);     // #7455DD
    private static readonly Color s_purpleLight = new(203, 191, 242);   // #CBBFF2
    private static readonly Color s_textColor = Color.White;
    private static readonly Color s_borderColor = Color.Grey;

    // Custom thick block ASPIRE text
    private static readonly string[] s_aspireLines =
    [
        " █████  ███████ ██████  ██ ██████  ██████  ",
        "██▀▀▀██ ██▀▀▀▀▀ ██▀▀▀██ ██ ██▀▀▀██ ██▀▀▀▀  ",
        "███████ ███████ ██████  ██ ██████  █████   ",
        "██   ██ ▀▀▀▀▀██ ██▀▀▀   ██ ██▀▀██  ██      ",
        "██   ██ ███████ ██      ██ ██   ██ ██████  ",
        "▀▀   ▀▀ ▀▀▀▀▀▀▀ ▀▀      ▀▀ ▀▀   ▀▀ ▀▀▀▀▀▀  ",
    ];

    // Letter start positions for animation (A, S, P, I, R, E columns)
    private static readonly int[] s_letterPositions = [0, 8, 16, 24, 27, 34];

    private readonly IAnsiConsole _console;

    /// <summary>
    /// Initializes a new instance of the <see cref="BannerService"/> class.
    /// </summary>
    /// <param name="consoleEnvironment">The console environment providing access to console output.</param>
    public BannerService(ConsoleEnvironment consoleEnvironment)
    {
        ArgumentNullException.ThrowIfNull(consoleEnvironment);
        _console = consoleEnvironment.Error; // Use stderr to avoid interfering with command output
    }

    /// <inheritdoc />
    public async Task DisplayBannerAsync(CancellationToken cancellationToken = default)
    {
        var cliVersion = VersionHelper.GetDefaultTemplateVersion();

        // Strip build metadata (everything after '+') for display purposes.
        var plusIndex = cliVersion.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            cliVersion = cliVersion[..plusIndex];
        }

        var aspireWidth = s_aspireLines[0].TrimEnd().Length;
        var welcomeText = RootCommandStrings.BannerWelcomeText;
        var versionText = string.Format(CultureInfo.CurrentCulture, RootCommandStrings.BannerVersionFormat, cliVersion);
        var versionPadding = Math.Max(0, aspireWidth - versionText.Length);

        await _console.Live(new Panel(new Text("")).Border(BoxBorder.Rounded).BorderColor(s_borderColor).Padding(2, 1))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                // Frame 1: Empty panel
                ctx.UpdateTarget(CreatePanel(CreateBanner(welcomeText, false, false, null)));
                await DelayAsync(80, cancellationToken);

                // Frame 2: Welcome text types in
                for (var i = 1; i <= welcomeText.Length; i += 3)
                {
                    var partial = welcomeText[..Math.Min(i, welcomeText.Length)];
                    ctx.UpdateTarget(CreatePanel(CreatePartialWelcome(partial)));
                    await DelayAsync(40, cancellationToken);
                }

                // Frame 3: ASPIRE appears letter by letter
                for (var letterIdx = 0; letterIdx <= s_letterPositions.Length; letterIdx++)
                {
                    var visibleCols = letterIdx < s_letterPositions.Length ? s_letterPositions[letterIdx] : s_aspireLines[0].Length;
                    ctx.UpdateTarget(CreatePanel(CreatePartialAspire(welcomeText, visibleCols)));
                    await DelayAsync(70, cancellationToken);
                }

                // Frame 4: Version slides in from right
                for (var i = 1; i <= 8; i++)
                {
                    var visibleChars = (int)Math.Ceiling((double)versionText.Length * i / 8);
                    var partialVer = versionText[(versionText.Length - visibleChars)..];
                    ctx.UpdateTarget(CreatePanel(CreateBanner(welcomeText, true, true, partialVer)));
                    await DelayAsync(50, cancellationToken);
                }

                // Frame 5: Shine sweeps across ASPIRE
                for (var shineCol = 0; shineCol <= aspireWidth; shineCol += 3)
                {
                    ctx.UpdateTarget(CreatePanel(CreateBannerWithShine(welcomeText, versionText, versionPadding, shineCol)));
                    await DelayAsync(35, cancellationToken);
                }

                // Final frame
                ctx.UpdateTarget(CreatePanel(CreateBanner(welcomeText, true, true, versionText)));
            });

        _console.WriteLine();
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // Animation cancelled, just return
        }
    }

    private static Panel CreatePanel(IRenderable content)
    {
        return new Panel(content)
            .Border(BoxBorder.Rounded)
            .BorderColor(s_borderColor)
            .Padding(2, 1);
    }

    private static Rows CreateBanner(string welcomeText, bool showWelcome, bool showAspire, string? partialVersion)
    {
        var elements = new List<IRenderable>();

        if (showWelcome)
        {
            elements.Add(new Markup($"[rgb({s_textColor.R},{s_textColor.G},{s_textColor.B})]{welcomeText.EscapeMarkup()}[/]"));
        }
        else
        {
            elements.Add(new Text(""));
        }

        if (showAspire)
        {
            elements.Add(CreateAspireText(-1));
        }
        else
        {
            // Empty space for ASPIRE
            foreach (var _ in s_aspireLines)
            {
                elements.Add(new Text(""));
            }
        }

        var aspireWidth = s_aspireLines[0].TrimEnd().Length;
        if (partialVersion is not null)
        {
            var padding = Math.Max(0, aspireWidth - partialVersion.Length);
            elements.Add(new Markup($"[rgb({s_textColor.R},{s_textColor.G},{s_textColor.B})]{new string(' ', padding)}{partialVersion}[/]"));
        }
        else
        {
            elements.Add(new Text(""));
        }

        return new Rows(elements);
    }

    private static Rows CreatePartialWelcome(string partial)
    {
        var elements = new List<IRenderable>
        {
            new Markup($"[rgb({s_textColor.R},{s_textColor.G},{s_textColor.B})]{partial.EscapeMarkup()}[/]")
        };

        // Empty space for ASPIRE and version
        foreach (var _ in s_aspireLines)
        {
            elements.Add(new Text(""));
        }
        elements.Add(new Text(""));

        return new Rows(elements);
    }

    private static Rows CreatePartialAspire(string welcomeText, int visibleCols)
    {
        var elements = new List<IRenderable>
        {
            new Markup($"[rgb({s_textColor.R},{s_textColor.G},{s_textColor.B})]{welcomeText.EscapeMarkup()}[/]")
        };

        foreach (var line in s_aspireLines)
        {
            var partialLine = line[..Math.Min(visibleCols, line.Length)].PadRight(line.Length);
            var markup = BuildLineMarkup(partialLine, -1);
            elements.Add(new Markup(markup));
        }

        elements.Add(new Text(""));

        return new Rows(elements);
    }

    private static Rows CreateBannerWithShine(string welcomeText, string versionText, int versionPadding, int shineCol)
    {
        var elements = new List<IRenderable>
        {
            new Markup($"[rgb({s_textColor.R},{s_textColor.G},{s_textColor.B})]{welcomeText.EscapeMarkup()}[/]")
        };

        foreach (var line in s_aspireLines)
        {
            var markup = BuildLineMarkup(line, shineCol);
            elements.Add(new Markup(markup));
        }

        elements.Add(new Markup($"[rgb({s_textColor.R},{s_textColor.G},{s_textColor.B})]{new string(' ', versionPadding)}{versionText}[/]"));

        return new Rows(elements);
    }

    private static Rows CreateAspireText(int shineCol)
    {
        var rows = new List<IRenderable>();
        foreach (var line in s_aspireLines)
        {
            var markup = BuildLineMarkup(line, shineCol);
            rows.Add(new Markup(markup));
        }
        return new Rows(rows);
    }

    private static string BuildLineMarkup(string line, int shineCol)
    {
        var markup = "";
        for (var col = 0; col < line.Length; col++)
        {
            var c = line[col];
            if (c is ' ')
            {
                markup += " ";
                continue;
            }

            var color = s_purpleAccent;
            if (shineCol >= 0 && col >= shineCol && col < shineCol + 3)
            {
                color = s_purpleLight;
            }
            else if (c == '▀')
            {
                color = s_purpleDark;
            }

            var charStr = c switch
            {
                '[' => "[[",
                ']' => "]]",
                _ => c.ToString()
            };

            markup += $"[rgb({color.R},{color.G},{color.B})]{charStr}[/]";
        }

        return markup;
    }
}
