// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that launches Aspire Deck — a preview, native (Tauri-based) alternative
/// to the Blazor dashboard.
/// </summary>
/// <remarks>
/// Aspire Deck is a drop-in replacement for the dashboard's inter-process
/// communication: it hosts the same OTLP ingestion endpoints and speaks the same
/// resource-service gRPC protocol, configured by the same environment variables.
/// This command resolves the Deck executable, wires those environment variables,
/// launches the native app, and waits for it to exit.
/// </remarks>
internal sealed class DeckCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    protected override bool UpdateNotificationsEnabled => true;

    private readonly LayoutProcessRunner _layoutProcessRunner;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly ILogger<DeckCommand> _logger;

    private static readonly Option<string?> s_otlpGrpcUrlOption = new("--otlp-grpc-url")
    {
        Description = DeckCommandStrings.OtlpGrpcUrlOptionDescription
    };

    private static readonly Option<string?> s_otlpHttpUrlOption = new("--otlp-http-url")
    {
        Description = DeckCommandStrings.OtlpHttpUrlOptionDescription
    };

    private static readonly Option<string?> s_resourceServiceUrlOption = new("--resource-service-url")
    {
        Description = DeckCommandStrings.ResourceServiceUrlOptionDescription
    };

    private static readonly Option<string?> s_deckPathOption = new("--deck-path")
    {
        Description = DeckCommandStrings.DeckPathOptionDescription
    };

    public DeckCommand(
        LayoutProcessRunner layoutProcessRunner,
        FileLoggerProvider fileLoggerProvider,
        ILogger<DeckCommand> logger,
        CommonCommandServices services)
        : base("deck", DeckCommandStrings.Description, services)
    {
        _layoutProcessRunner = layoutProcessRunner;
        _fileLoggerProvider = fileLoggerProvider;
        _logger = logger;

        Options.Add(s_otlpGrpcUrlOption);
        Options.Add(s_otlpHttpUrlOption);
        Options.Add(s_resourceServiceUrlOption);
        Options.Add(s_deckPathOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var deckPath = ResolveDeckExecutable(parseResult);
        if (deckPath is null)
        {
            InteractionService.DisplayError(DeckCommandStrings.DeckExecutableNotFound);
            return CommandResult.Failure(CliExitCodes.DashboardFailure);
        }

        var otlpGrpcUrl = parseResult.GetValue(s_otlpGrpcUrlOption)
            ?? ExecutionContext.GetEnvironmentVariable(KnownConfigNames.DashboardOtlpGrpcEndpointUrl)
            ?? "http://localhost:4317";
        var otlpHttpUrl = parseResult.GetValue(s_otlpHttpUrlOption)
            ?? ExecutionContext.GetEnvironmentVariable(KnownConfigNames.DashboardOtlpHttpEndpointUrl)
            ?? "http://localhost:4318";
        var resourceServiceUrl = parseResult.GetValue(s_resourceServiceUrlOption)
            ?? ExecutionContext.GetEnvironmentVariable(KnownConfigNames.ResourceServiceEndpointUrl);

        // The Deck reads all of its configuration from the environment (the same
        // variables the dashboard reads), so the CLI's job is to populate them and
        // launch the process. Start from the current environment so existing
        // DASHBOARD__* settings (auth modes, API keys) flow through unchanged.
        var environmentVariables = new Dictionary<string, string>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = otlpGrpcUrl,
            [KnownConfigNames.DashboardOtlpHttpEndpointUrl] = otlpHttpUrl,
        };

        if (!string.IsNullOrEmpty(resourceServiceUrl))
        {
            environmentVariables[KnownConfigNames.ResourceServiceEndpointUrl] = resourceServiceUrl;
        }

        var info = new DeckInfo(otlpGrpcUrl, otlpHttpUrl, resourceServiceUrl);
        return await ExecuteForegroundAsync(deckPath, environmentVariables, info, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the Aspire Deck executable, in priority order:
    /// the <c>--deck-path</c> option, the <c>ASPIRE_DECK_PATH</c> environment
    /// variable, then a conventional build output under the repository.
    /// </summary>
    private string? ResolveDeckExecutable(ParseResult parseResult)
    {
        var fromOption = parseResult.GetValue(s_deckPathOption);
        if (!string.IsNullOrEmpty(fromOption) && File.Exists(fromOption))
        {
            return fromOption;
        }

        var fromEnv = ExecutionContext.GetEnvironmentVariable("ASPIRE_DECK_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        return ResolveDevBuildExecutable();
    }

    /// <summary>
    /// Locates a locally built Deck binary by walking up from the CLI's base
    /// directory looking for <c>src/Aspire.Deck/src-tauri/target</c> (the Cargo
    /// output directory), preferring a release build over a debug build. This
    /// makes <c>aspire deck</c> work out of the box from a repo checkout after
    /// the Deck has been built, without requiring <c>ASPIRE_DECK_PATH</c>.
    /// </summary>
    private static string? ResolveDevBuildExecutable()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "aspire-deck.exe"
            : "aspire-deck";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var target = Path.Combine(dir.FullName, "src", "Aspire.Deck", "src-tauri", "target");
            if (Directory.Exists(target))
            {
                foreach (var profile in new[] { "release", "debug" })
                {
                    var candidate = Path.Combine(target, profile, exeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    private async Task<CommandResult> ExecuteForegroundAsync(string deckPath, IDictionary<string, string> environmentVariables, DeckInfo info, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting Aspire Deck: {DeckPath}", deckPath);

        var outputCollector = new OutputCollector(_fileLoggerProvider, CliLogFormat.Categories.Dashboard);
        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = outputCollector.AppendOutput,
            StandardErrorCallback = outputCollector.AppendError,
        };

        IProcessExecution process;
        try
        {
            process = _layoutProcessRunner.Start(deckPath, [], environmentVariables: environmentVariables, options: options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Aspire Deck process: {DeckPath}", deckPath);
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, DeckCommandStrings.DeckFailedToStart, ex.Message));
            return CommandResult.Failure(CliExitCodes.DashboardFailure);
        }

        using var _ = process;

        RenderDeckSummary(InteractionService, info, ExecutionContext.LogFilePath);
        InteractionService.DisplayEmptyLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            InteractionService.DisplayMessage(KnownEmojis.StopSign, $"[teal bold]{DeckCommandStrings.StoppingDeck}[/]", allowMarkup: true);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            // The command is designed to be cancellable (e.g. Ctrl+C) at any time.
            // Treat cancellation as a successful exit since the user intentionally
            // closed the deck.
            return CommandResult.Cancelled(CliExitCodes.Success);
        }

        if (process.ExitCode != 0)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, DeckCommandStrings.DeckExitedWithError, process.ExitCode));
            return CommandResult.Failure(CliExitCodes.DashboardFailure);
        }

        return CommandResult.Success();
    }

    private static void RenderDeckSummary(IInteractionService interactionService, DeckInfo info, string logFilePath)
    {
        interactionService.DisplayEmptyLine();
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        var otlpGrpcLabel = DeckCommandStrings.OtlpGrpcLabel;
        var otlpHttpLabel = DeckCommandStrings.OtlpHttpLabel;
        var resourceServiceLabel = DeckCommandStrings.ResourceServiceLabel;
        var logsLabel = DeckCommandStrings.LogsLabel;

        var labels = new List<string> { otlpGrpcLabel, otlpHttpLabel, resourceServiceLabel, logsLabel };
        var longestLabelLength = labels.Max(s => s.Length) + 1; // +1 for colon
        grid.Columns[0].Width = longestLabelLength;

        grid.AddRow(
            new Align(new Markup($"[bold green]{otlpGrpcLabel}[/]:"), HorizontalAlignment.Right),
            new Text(info.OtlpGrpcUrl));
        grid.AddRow(Text.Empty, Text.Empty);

        grid.AddRow(
            new Align(new Markup($"[bold green]{otlpHttpLabel}[/]:"), HorizontalAlignment.Right),
            new Text(info.OtlpHttpUrl));
        grid.AddRow(Text.Empty, Text.Empty);

        grid.AddRow(
            new Align(new Markup($"[bold green]{resourceServiceLabel}[/]:"), HorizontalAlignment.Right),
            new Text(info.ResourceServiceUrl ?? DeckCommandStrings.ResourceServiceNotConfigured));
        grid.AddRow(Text.Empty, Text.Empty);

        grid.AddRow(
            new Align(new Markup($"[bold green]{logsLabel}[/]:"), HorizontalAlignment.Right),
            new Markup(MarkupHelpers.SafeFileLink(interactionService, logFilePath)));

        var padder = new Padder(grid, new Padding(3, 0));
        interactionService.DisplayRenderable(padder);
    }

    private sealed record DeckInfo(string OtlpGrpcUrl, string OtlpHttpUrl, string? ResourceServiceUrl);
}
