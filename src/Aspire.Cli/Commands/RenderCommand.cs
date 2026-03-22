// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if DEBUG

using System.CommandLine;
using System.Reflection;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Debug-only command for smoke testing CLI rendering (emoji alignment, status spinners, etc.).
/// </summary>
internal sealed class RenderCommand : BaseCommand
{
    /// <summary>
    /// All emojis defined in <see cref="KnownEmojis"/>, discovered via reflection.
    /// </summary>
    private static readonly KnownEmoji[] s_allEmojis = typeof(KnownEmojis)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(KnownEmoji))
        .Select(f => (KnownEmoji)f.GetValue(null)!)
        .ToArray();

    private static readonly Dictionary<string, string> s_choices = new()
    {
        ["displaymessage"] = "Display message (all emojis)",
        ["showstatus"] = "Show status spinner (first 5 emojis)",
        ["showstatus-markup"] = "Show status with markup rendered",
        ["showstatus-escaped"] = "Show status with markup escaped",
        ["mixed"] = "Mixed interaction service methods",
        ["exit"] = "Exit",
    };

    public RenderCommand(
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        AspireCliTelemetry telemetry)
        : base("render", "Smoke test CLI rendering", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Hidden = true;
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        while (true)
        {
            var choice = await InteractionService.PromptForSelectionAsync(
                "What do you want to test?",
                s_choices.Keys,
                key => s_choices[key],
                cancellationToken);

            switch (choice)
            {
                case "displaymessage":
                    TestDisplayMessage();
                    break;
                case "showstatus":
                    await TestShowStatusAsync(cancellationToken);
                    break;
                case "showstatus-markup":
                    await TestShowStatusWithMarkupAsync(cancellationToken);
                    break;
                case "showstatus-escaped":
                    await TestShowStatusEscapedAsync(cancellationToken);
                    break;
                case "mixed":
                    await TestMixedMethodsAsync(cancellationToken);
                    break;
                case "exit":
                    return ExitCodeConstants.Success;
                default:
                    return ExitCodeConstants.InvalidCommand;
            }
        }
    }

    private int TestDisplayMessage()
    {
        foreach (var emoji in s_allEmojis)
        {
            InteractionService.DisplayMessage(emoji, $"DisplayMessage with {emoji.Name}");
        }

        return ExitCodeConstants.Success;
    }

    private async Task<int> TestShowStatusAsync(CancellationToken cancellationToken)
    {
        foreach (var emoji in s_allEmojis.Take(5))
        {
            await InteractionService.ShowStatusAsync(
                $"ShowStatus with {emoji.Name} for 2 seconds...",
                async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    return ExitCodeConstants.Success;
                },
                emoji: emoji);
        }

        return ExitCodeConstants.Success;
    }

    private async Task<int> TestShowStatusWithMarkupAsync(CancellationToken cancellationToken)
    {
        await InteractionService.ShowStatusAsync(
            "[bold]Installing[/] packages with [green]markup[/]...",
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return ExitCodeConstants.Success;
            },
            emoji: KnownEmojis.Package,
            allowMarkup: true);

        return ExitCodeConstants.Success;
    }

    private async Task<int> TestShowStatusEscapedAsync(CancellationToken cancellationToken)
    {
        await InteractionService.ShowStatusAsync(
            "[bold]Installing[/] packages with [green]markup[/] escaped...",
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return ExitCodeConstants.Success;
            },
            emoji: KnownEmojis.Package);

        return ExitCodeConstants.Success;
    }

    private async Task TestMixedMethodsAsync(CancellationToken cancellationToken)
    {
        InteractionService.DisplayMessage(KnownEmojis.Rocket, "Starting mixed methods test...");
        InteractionService.DisplayEmptyLine();

        InteractionService.DisplaySuccess("Step 1 complete!");
        InteractionService.DisplaySubtleMessage("This is a subtle hint.");
        InteractionService.DisplayMessage(KnownEmojis.MagnifyingGlassTiltedLeft, "Searching for [packages]...");
        InteractionService.DisplayEmptyLine();

        InteractionService.DisplayMarkupLine("[bold green]Bold green markup[/] and [dim]dim text[/]");
        InteractionService.DisplayPlainText("Plain text with [brackets] that should appear literally.");
        InteractionService.DisplayEmptyLine();

        await InteractionService.ShowStatusAsync(
            "Running a quick task...",
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return 42;
            },
            emoji: KnownEmojis.Gear);

        InteractionService.ShowStatus(
            "Synchronous status spinner...",
            () => Thread.Sleep(TimeSpan.FromSeconds(1)),
            emoji: KnownEmojis.Hammer);

        InteractionService.DisplayEmptyLine();

        var name = await InteractionService.PromptForStringAsync(
            "Enter a test value",
            defaultValue: "hello",
            cancellationToken: cancellationToken);

        InteractionService.DisplayMessage(KnownEmojis.CheckMark, $"You entered: {name}");

        var confirmed = await InteractionService.ConfirmAsync(
            "Do you want to continue?",
            defaultValue: true,
            cancellationToken: cancellationToken);

        if (confirmed)
        {
            InteractionService.DisplaySuccess("Confirmed!");
        }
        else
        {
            InteractionService.DisplayError("Cancelled.");
        }

        InteractionService.DisplayEmptyLine();
        InteractionService.DisplayMessage(KnownEmojis.StopSign, "Mixed methods test complete.");
    }
}

#endif
