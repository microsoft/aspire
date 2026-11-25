// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admire_Interactive_AnimatesToCompleteLogo(bool narrow)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.CliExecutionContextFactory = _ => workspace.CreateExecutionContext(identityVersion: "17.0.0-preview.1", identityCommit: "2d325b13709752ba");
        }).BuildServiceProvider();
        if (narrow)
        {
            provider.GetRequiredService<ConsoleEnvironment>().Out.Profile.Width = 50;
        }

        var result = await provider.GetRequiredService<RootCommand>().Parse("admire").InvokeAsync().DefaultTimeout();

        Assert.Equal(0, result);
        Assert.True(interaction.DisplayedLiveRenderables.Count > 2);
        Assert.Empty(interaction.DisplayedRawText);
        var writer = new StringWriter();
        var console = CliServiceCollectionTestOptions.CreateAnsiConsole(writer, ansi: false);
        console.Write(interaction.DisplayedLiveRenderables[^1]);
        var rendered = string.Join(Environment.NewLine, writer.ToString().TrimEnd('\r', '\n')
            .Split(Environment.NewLine).Select(line => line.TrimEnd()));
        Assert.Equal(ExpectedLogo("17.0.0", wide: !narrow, solid: true, commit: "2d325b1370"), rendered);

        var colorWriter = new StringWriter();
        var colorConsole = CliServiceCollectionTestOptions.CreateAnsiConsole(colorWriter);
        colorConsole.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.TrueColor;
        colorConsole.Write(interaction.DisplayedLiveRenderables[^1]);
        var colorOutput = colorWriter.ToString();
        Assert.Contains("\u001b[38;2;81;43;212m", colorOutput);
        Assert.Contains("\u001b[38;2;116;85;221m", colorOutput);
        Assert.Contains("\u001b[38;2;151;128;229m", colorOutput);
        Assert.Contains("\u001b[38;2;185;170;238m", colorOutput);
        Assert.Contains("\u001b[38;2;220;213;246m", colorOutput);
        Assert.Contains("\u001b[38;2;116;85;221m▀", colorOutput);
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
