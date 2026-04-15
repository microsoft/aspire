// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if DEBUG

using System.CommandLine;
using System.Reflection;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

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
        ["displaystyles"] = "Display error, success, subtle, and cancellation messages",
        ["showstatus"] = "Show status spinner (first 5 emojis)",
        ["showstatus-markup"] = "Show status with markup rendered",
        ["showstatus-escaped"] = "Show status with markup escaped",
        ["choice"] = "Selection prompt with formatted choices",
        ["choice-simple"] = "Selection prompt without formatter",
        ["mixed"] = "Mixed interaction service methods",
        ["publish-summary-all"] = "Publish summary timeline (stress scenarios)",
        ["exit"] = "Exit",
    };

    private static readonly Dictionary<string, string> s_publishSummaryScenarioDescriptions = new(StringComparers.CommandName)
    {
        ["publish-summary-all"] = "Render all publish summary stress scenarios",
        ["publish-summary-deep-nesting"] = "Render deeply nested publish steps",
        ["publish-summary-long-text"] = "Render long step names and timeline fallback",
        ["publish-summary-markup"] = "Render step names and failures containing markup characters",
        ["publish-summary-mixed-hierarchy"] = "Render a mix of rooted, orphaned, and parentless steps",
        ["publish-summary-duration-extremes"] = "Render very short and very long durations together",
    };

    private static readonly Option<string?> s_scenarioOption = new("--scenario")
    {
        Description = "Render a specific scenario without prompting.",
        Hidden = true
    };

    private static readonly Option<int?> s_consoleWidthOption = new("--console-width")
    {
        Description = "Override the console width used while rendering.",
        Hidden = true
    };

    private static readonly Option<bool> s_listScenariosOption = new("--list-scenarios")
    {
        Description = "List supported render scenarios.",
        Hidden = true
    };

    private readonly IAnsiConsole _ansiConsole;
    private readonly ICliHostEnvironment _hostEnvironment;

    public RenderCommand(
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        AspireCliTelemetry telemetry,
        IAnsiConsole ansiConsole,
        ICliHostEnvironment hostEnvironment)
        : base("render", "Smoke test CLI rendering", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _ansiConsole = ansiConsole;
        _hostEnvironment = hostEnvironment;

        Options.Add(s_scenarioOption);
        Options.Add(s_consoleWidthOption);
        Options.Add(s_listScenariosOption);
        Hidden = true;
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (parseResult.GetValue(s_listScenariosOption))
        {
            ListScenarios();
            return ExitCodeConstants.Success;
        }

        var requestedScenario = parseResult.GetValue(s_scenarioOption);
        if (!string.IsNullOrEmpty(requestedScenario))
        {
            return await ExecuteChoiceAsync(requestedScenario, parseResult.GetValue(s_consoleWidthOption), cancellationToken);
        }

        while (true)
        {
            var choice = await InteractionService.PromptForSelectionAsync(
                "What do you want to test?",
                s_choices.Keys,
                key => s_choices[key],
                cancellationToken);

            var exitCode = await ExecuteChoiceAsync(choice, parseResult.GetValue(s_consoleWidthOption), cancellationToken);
            if (choice == "exit" || exitCode != ExitCodeConstants.Success)
            {
                return exitCode;
            }
        }
    }

    private async Task<int> ExecuteChoiceAsync(string choice, int? consoleWidth, CancellationToken cancellationToken)
    {
        var originalWidth = _ansiConsole.Profile.Width;

        if (consoleWidth is > 0 and < int.MaxValue)
        {
            _ansiConsole.Profile.Width = consoleWidth.Value;
        }

        try
        {
            switch (choice)
            {
                case "displaymessage":
                    return TestDisplayMessage();
                case "displaystyles":
                    return TestDisplayStyles();
                case "showstatus":
                    return await TestShowStatusAsync(cancellationToken);
                case "showstatus-markup":
                    return await TestShowStatusWithMarkupAsync(cancellationToken);
                case "showstatus-escaped":
                    return await TestShowStatusEscapedAsync(cancellationToken);
                case "choice":
                    return await TestChoiceWithFormatterAsync(cancellationToken);
                case "choice-simple":
                    return await TestChoiceSimpleAsync(cancellationToken);
                case "mixed":
                    await TestMixedMethodsAsync(cancellationToken);
                    return ExitCodeConstants.Success;
                case "publish-summary-all":
                    return RenderPublishSummaryScenarios(s_publishSummaryScenarioDescriptions.Keys.Where(k => !StringComparers.CommandName.Equals(k, "publish-summary-all")));
                case "exit":
                    return ExitCodeConstants.Success;
                default:
                    if (s_publishSummaryScenarioDescriptions.ContainsKey(choice))
                    {
                        return RenderPublishSummaryScenarios([choice]);
                    }

                    InteractionService.DisplayError($"Unknown render scenario '{choice}'.");
                    InteractionService.DisplayPlainText("Use 'aspire render --list-scenarios' to see supported values.");
                    return ExitCodeConstants.InvalidCommand;
            }
        }
        finally
        {
            _ansiConsole.Profile.Width = originalWidth;
        }
    }

    private void ListScenarios()
    {
        foreach (var choice in s_choices.Where(choice => !s_publishSummaryScenarioDescriptions.ContainsKey(choice.Key)))
        {
            InteractionService.DisplayPlainText($"{choice.Key}: {choice.Value}");
        }

        InteractionService.DisplayEmptyLine();

        foreach (var scenario in s_publishSummaryScenarioDescriptions)
        {
            InteractionService.DisplayPlainText($"{scenario.Key}: {scenario.Value}");
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

    private int TestDisplayStyles()
    {
        InteractionService.DisplayError("This is an error message.");
        InteractionService.DisplaySuccess("Operation completed successfully.");
        InteractionService.DisplaySubtleMessage("This is a subtle hint.");
        InteractionService.DisplayCancellationMessage();
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

    private async Task<int> TestChoiceWithFormatterAsync(CancellationToken cancellationToken)
    {
        var packages = new[]
        {
            ("Aspire.Hosting.Redis", "9.2.0", "[green]stable[/]"),
            ("Aspire.Hosting.PostgreSQL", "9.2.0", "[green]stable[/]"),
            ("Aspire.Hosting.RabbitMQ", "9.1.0", "[yellow]preview[/]"),
            ("Aspire.Hosting.MongoDB [Deprecated]", "9.0.0", "[red]deprecated[/]"),
            ("Aspire.Hosting.Kafka", "9.2.0", "[green]stable[/]"),
            ("Aspire.Hosting.MySql [Preview]", "9.1.0", "[yellow]preview[/]"),
        };

        var selected = await InteractionService.PromptForSelectionAsync(
            "Select a [bold blue]package[/] to install:",
            packages,
            p => $"{p.Item1.EscapeMarkup()} [dim]v{p.Item2}[/] ({p.Item3})",
            cancellationToken);

        InteractionService.DisplayMessage(KnownEmojis.Package, $"Selected: {selected.Item1} v{selected.Item2}");
        return ExitCodeConstants.Success;
    }

    private async Task<int> TestChoiceSimpleAsync(CancellationToken cancellationToken)
    {
        var environments = new[] { "Development", "Staging", "Production" };

        var selected = await InteractionService.PromptForSelectionAsync(
            "Select a target environment:",
            environments,
            e => e,
            cancellationToken);

        InteractionService.DisplayMessage(KnownEmojis.Rocket, $"Deploying to {selected}...");
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

    private int RenderPublishSummaryScenarios(IEnumerable<string> scenarioKeys)
    {
        foreach (var scenarioKey in scenarioKeys)
        {
            var scenario = CreatePublishSummaryScenario(scenarioKey);
            InteractionService.DisplayPlainText($"=== {scenario.Title} ===");

            var logger = new ConsoleActivityLogger(_ansiConsole, _hostEnvironment, forceColor: _hostEnvironment.SupportsAnsi);
            logger.SeedSummaryState(scenario.Records);
            logger.SetStepDurations(scenario.Records);
            logger.SetFinalResult(scenario.Succeeded, scenario.PipelineSummary);
            logger.WriteSummary();
        }

        return ExitCodeConstants.Success;
    }

    private static PublishSummaryRenderScenario CreatePublishSummaryScenario(string scenarioKey) => scenarioKey switch
    {
        "publish-summary-deep-nesting" => new(
            "Deep nesting",
            [
                new("root", "Pipeline", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(14), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(14)),
                new("level-1", "Provision", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(12), null, "root", 1, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(13)),
                new("level-2", "Generate templates", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(10), null, "level-1", 2, 3, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(11.5)),
                new("level-3", "Upload manifests", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(8), null, "level-2", 3, 4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)),
                new("level-4", "Wait for deployment", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(6), null, "level-3", 4, 5, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)),
                new("level-5", "Finalize output", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(2), null, "level-4", 5, 6, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11)),
                new("level-10", "Leaf nested 10 levels deep", ConsoleActivityLogger.ActivityState.Warning, TimeSpan.FromSeconds(1), null, "level-5", 10, 7, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)),
                new("level-11", "This is a very very very very very long name", ConsoleActivityLogger.ActivityState.Warning, TimeSpan.FromSeconds(1), null, "level-10", 11, 8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)),
            ]),
        "publish-summary-long-text" => new(
            "Long text and constrained width",
            [
                new("root", "Publish the application with a deliberately long root step display name", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(20), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(20)),
                new("child", "Generate deployment assets for every resource with an extremely verbose child label", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(12), null, "root", 1, 2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(14)),
                new("grandchild", "Write an unusually long manifest filename that would normally push the timeline off screen", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(3), null, "child", 2, 3, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(11)),
            ]),
        "publish-summary-markup" => new(
            "Markup characters in names and failures",
            [
                new("root", "Build [web] frontend", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromMilliseconds(120), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromMilliseconds(120)),
                new("child", "Deploy [api] service", ConsoleActivityLogger.ActivityState.Failure, TimeSpan.FromMilliseconds(35), "Failure while parsing [[resource]] => [bold]{bad}[/]", "root", 1, 2, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(95)),
                new("sibling", "Notify [observers]", ConsoleActivityLogger.ActivityState.Warning, TimeSpan.FromMilliseconds(12), null, "root", 1, 3, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(112)),
            ], false),
        "publish-summary-mixed-hierarchy" => new(
            "Mixed roots and orphaned parents",
            [
                new("root-a", "Restore", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(5), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
                new("orphan", "Orphaned child falls back to root ordering", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(2), null, "missing-parent", 1, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
                new("root-b", "Publish", ConsoleActivityLogger.ActivityState.Warning, TimeSpan.FromSeconds(4), null, null, 0, 3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9)),
                new("child-b", "Package", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(1), null, "root-b", 1, 4, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7)),
                new("info-step", "Using cached configuration", ConsoleActivityLogger.ActivityState.Info, TimeSpan.FromSeconds(0), null, "root-b", 1, 5, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)),
                new("root-c", "Validate", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(2), null, null, 0, 6, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11)),
            ]),
        "publish-summary-duration-extremes" => new(
            "Duration extremes",
            [
                new("root", "Full pipeline", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromMinutes(3), null, null, 0, 1, TimeSpan.Zero, TimeSpan.FromMinutes(3)),
                new("tiny", "Tiny 0.2ms event", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromMilliseconds(0.2), null, "root", 1, 2, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5.2)),
                new("mid", "HTTP publish", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(18), null, "root", 1, 3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(48)),
                new("late", "Finalize", ConsoleActivityLogger.ActivityState.Success, TimeSpan.FromSeconds(2), null, "root", 1, 4, TimeSpan.FromMinutes(2.5), TimeSpan.FromMinutes(2.53333333333333)),
                new("zero", "Zero event", ConsoleActivityLogger.ActivityState.Success, TimeSpan.Zero, null, "root", 1, 5, TimeSpan.FromMinutes(2.51), TimeSpan.FromMinutes(2.51)),
            ]),
        _ => throw new InvalidOperationException($"Unknown publish summary scenario '{scenarioKey}'.")
    };

    private sealed record PublishSummaryRenderScenario(
        string Title,
        IReadOnlyList<ConsoleActivityLogger.StepDurationRecord> Records,
        bool Succeeded = true,
        IReadOnlyList<BackchannelPipelineSummaryItem>? PipelineSummary = null);
}

#endif
