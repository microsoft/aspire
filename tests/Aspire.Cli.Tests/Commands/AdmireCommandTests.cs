// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tui;
using Hex1b;
using Hex1b.Input;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Tokens;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class AdmireCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("17.0.0", null, "17.0.0", null, false)]
    [InlineData("17.1.2-preview.1+abc123", null, "17.1.2", "abc123", false)]
    [InlineData("17.0.0+123456789abc", "fedcba9876543210", "17.0.0", "fedcba9876", false)]
    [InlineData("17.0.0", null, "17.0.0", null, true)]
    public async Task Admire_WithoutAnimation_PrintsLogoAndVersion(string identityVersion, string? identityCommit, string expectedVersion, string? expectedCommit, bool interactiveWithoutAnsi)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => interactiveWithoutAnsi
                ? new TestCliHostEnvironment(supportsInteractiveOutput: true, supportsAnsi: false)
                : TestHelpers.CreateNonInteractiveHostEnvironment();
            options.CliExecutionContextFactory = _ => workspace.CreateExecutionContext(identityVersion: identityVersion, identityCommit: identityCommit);
        }).BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>().Parse(["admire"]).InvokeAsync().DefaultTimeout();

        Assert.Equal(0, result);
        var output = Assert.Single(interaction.DisplayedRawText);
        Assert.Equal(ExpectedLogo(expectedVersion, wide: true, commit: expectedCommit), output.Text);
        Assert.Empty(interaction.DisplayedLiveRenderables);
    }

    [Theory]
    [InlineData(100, 32)]
    [InlineData(24, 14)]
    [InlineData(8, 2)]
    public async Task Admire_AnimationFormsLogoThenRevealsVersion(int width, int height)
    {
        var app = new AdmireSplashApp("17.0.0");
        var forming = new Surface(width, height);
        app.Render(forming, AdmireSplashApp.FormationDurationMs / 2);
        var formed = new Surface(width, height);
        app.Render(formed, AdmireSplashApp.FormationDurationMs);
        var final = new Surface(width, height);
        app.Render(final, AdmireSplashApp.ExitStartMs);
        var exiting = new Surface(width, height);
        app.Render(exiting, AdmireSplashApp.ExitStartMs + AdmireSplashApp.ExitTravelMs / 2);
        var exited = new Surface(width, height);
        app.Render(exited, AdmireSplashApp.TotalDurationMs);
        await Verifier.Verify($"Forming:\n{GetSurfaceText(forming)}\n\nFormed:\n{GetSurfaceText(formed)}\n\nFinal:\n{GetSurfaceText(final)}\n\nExiting:\n{GetSurfaceText(exiting)}\n\nExited:\n{GetSurfaceText(exited)}", "txt")
            .UseDirectory("../Snapshots").UseParameters(width, height);
    }

    [Fact]
    public void Admire_VersionFadesInAfterFormation()
    {
        var app = new AdmireSplashApp("17.0.0");
        var halfway = new Surface(100, 32);
        app.Render(halfway, AdmireSplashApp.FormationDurationMs + AdmireSplashApp.CaptionFadeDurationMs / 2);
        var final = new Surface(100, 32);
        app.Render(final, AdmireSplashApp.ExitStartMs);
        Assert.Equal("A", halfway[43, 26].Character);
        Assert.Equal(Hex1bColor.FromRgb(115, 115, 115), halfway[43, 26].Foreground);
        Assert.Equal(Hex1bColor.FromRgb(230, 230, 230), final[43, 26].Foreground);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admire_FlowUsesNormalBufferAndEnablesMouse(bool resize)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var recording = Path.Combine(workspace.WorkspaceRoot.FullName, "admire.cast");
        var presentation = new HeadlessPresentationAdapter(100, 32);
        var runTask = new AdmireSplashApp("17.0.0").RunAsync(Hex1bTerminal.CreateBuilder()
            .WithPresentation(presentation).WithDimensions(100, 32).WithAsciinemaRecording(recording), CancellationToken.None);
        if (resize)
        {
            presentation.TriggerResize(60, 24);
        }
        await runTask.DefaultTimeout(AdmireSplashApp.TotalDurationMs + 10000);

        // Asciinema v2 writes a header followed by [seconds, "o", ANSI text].
        var output = new List<string>();
        foreach (var line in File.ReadLines(recording).Skip(1))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement[1].GetString() == "o")
            {
                output.Add(json.RootElement[2].GetString()!);
            }
        }
        Assert.True(output.Count > 2);
        Assert.Empty(Regex.Matches(string.Concat(output), "\u001b\\[\\?(?:47|1047|1049)h"));
        var text = string.Concat(output);
        Assert.Contains("\u001b[?1003h", text);
        Assert.Contains("\u001b[?1006h", text);
        Assert.Contains("Aspire 17.0.0", text);
    }

    [Fact]
    public async Task Admire_FlowCanBeCancelled()
    {
        using var stopping = new CancellationTokenSource();
        stopping.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AdmireSplashApp("17.0.0")
            .RunAsync(Hex1bTerminal.CreateBuilder().WithHeadless(), stopping.Token)).DefaultTimeout();
    }

    [Fact]
    public void Admire_ParticlesSprayFromMouseAndSteerToMovingExit()
    {
        var left = new AdmireSplashApp("17.0.0");
        var right = new AdmireSplashApp("17.0.0");
        left.MoveMouse(10, 5);
        right.MoveMouse(90, 27);
        var leftSpray = new Surface(100, 32);
        var rightSpray = new Surface(100, 32);
        left.Render(leftSpray, 100);
        right.Render(rightSpray, 100);
        Assert.True(GetParticleCentre(leftSpray).X < GetParticleCentre(rightSpray).X);

        var firstExit = AdmireSplashApp.ExitStartMs + AdmireSplashApp.ExitStaggerMs;
        left.Render(new Surface(100, 32), firstExit);
        right.Render(new Surface(100, 32), firstExit);
        left.MoveMouse(90, 27);
        right.MoveMouse(10, 5);
        var leftSteered = new Surface(100, 32);
        var rightSteered = new Surface(100, 32);
        left.Render(leftSteered, firstExit + AdmireSplashApp.ExitTravelMs / 2);
        right.Render(rightSteered, firstExit + AdmireSplashApp.ExitTravelMs / 2);
        var leftCentre = GetParticleCentre(leftSteered);
        var rightCentre = GetParticleCentre(rightSteered);
        Assert.True(leftCentre.X > rightCentre.X);
        Assert.True(leftCentre.Y > rightCentre.Y);

        var exited = new Surface(100, 32);
        left.Render(exited, AdmireSplashApp.TotalDurationMs);
        Assert.All(Enumerable.Range(0, exited.Height), y =>
            Assert.All(Enumerable.Range(0, exited.Width), x => Assert.True(exited[x, y].IsTransparent)));
    }

    [Fact]
    public async Task Admire_MouseInputUpdatesSprayOrigin()
    {
        var app = new AdmireSplashApp("17.0.0");
        var expected = new AdmireSplashApp("17.0.0");
        expected.MoveMouse(10, 5);
        await new AdmireSplashApp.MouseTrackingFilter(app).OnInputAsync(
            [new SgrMouseToken(MouseButton.None, MouseAction.Move, 10, 5, Hex1bModifiers.None, 35)], TimeSpan.Zero);
        var actualSurface = new Surface(100, 32);
        var expectedSurface = new Surface(100, 32);
        app.Render(actualSurface, 100);
        expected.Render(expectedSurface, 100);
        Assert.Equal(GetSurfaceText(expectedSurface), GetSurfaceText(actualSurface));
    }

    private static (double X, double Y) GetParticleCentre(Surface surface)
    {
        var cells = (from y in Enumerable.Range(0, surface.Height)
                     from x in Enumerable.Range(0, surface.Width)
                     where !surface[x, y].IsTransparent && surface[x, y].Character[0] is >= '\u2800' and <= '\u28ff'
                     select (X: x, Y: y)).ToArray();
        Assert.NotEmpty(cells);
        return (cells.Average(cell => cell.X), cells.Average(cell => cell.Y));
    }

    private static string GetSurfaceText(Surface surface)
    {
        return string.Join('\n', Enumerable.Range(0, surface.Height).Select(y =>
            string.Concat(Enumerable.Range(0, surface.Width).Select(x =>
                surface[x, y].IsTransparent ? " " : surface[x, y].Character)).TrimEnd()));
    }

    [Theory]
    [InlineData(50, false)]
    [InlineData(73, false)]
    [InlineData(74, true)]
    public async Task Admire_LayoutFitsTerminalWidth(int width, bool wide)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
            options.CliExecutionContextFactory = _ => workspace.CreateExecutionContext(identityVersion: "17.0.0");
        }).BuildServiceProvider();
        provider.GetRequiredService<ConsoleEnvironment>().Out.Profile.Width = width;

        var result = await provider.GetRequiredService<RootCommand>().Parse("admire").InvokeAsync().DefaultTimeout();

        Assert.Equal(0, result);
        Assert.Equal(ExpectedLogo("17.0.0", wide), Assert.Single(interaction.DisplayedRawText).Text);
    }

    private static string ExpectedLogo(string version, bool wide, bool solid = false, string? commit = null)
    {
        string[] title =
        [
            " █████  ███████ ██████  ██ ██████  ██████",
            "██▀▀▀██ ██▀▀▀▀▀ ██▀▀▀██ ██ ██▀▀▀██ ██▀▀▀▀",
            "███████ ███████ ██████  ██ ██████  █████",
            "██   ██ ▀▀▀▀▀██ ██▀▀▀   ██ ██▀▀██  ██",
            "██   ██ ███████ ██      ██ ██   ██ ██████",
            "▀▀   ▀▀ ▀▀▀▀▀▀▀ ▀▀      ▀▀ ▀▀   ▀▀ ▀▀▀▀▀▀"
        ];
        string[] logo =
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
        if (solid)
        {
            logo = logo.Select(line => line.Replace('▓', '█').Replace('▒', '█').Replace('░', '█').Replace('▄', '█')).ToArray();
        }
        else
        {
            logo = logo.Select(line => line.Replace('▄', '░')).ToArray();
        }
        var versionLine = $"                 {version}";
        var commitLine = commit is null ? null : $"             Commit {commit}";

        var lines = wide
            ? logo.Select((line, i) =>
                (i >= 3 && i < 9 ? title[i - 3] : i == 10 ? versionLine : i == 11 ? commitLine ?? "" : "").PadRight(45) + line)
            : logo.Concat([""]).Concat(title).Concat(["", versionLine]).Concat(commitLine is null ? [] : [commitLine]);

        return string.Join(Environment.NewLine, new[] { "" }
            .Concat(lines.Select(line => line.Length == 0 ? "" : "  " + line))
            .Concat([""]));
    }
}
