// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal abstract class BaseCommand : Command
{
    private static readonly int[] s_suppressErrorLogsMessageExitCodes = [CliExitCodes.Cancelled, CliExitCodes.MissingRequiredArgument];
    private static readonly TimeSpan s_extensionInteractionFlushTimeout = TimeSpan.FromSeconds(10);

    protected virtual bool UpdateNotificationsEnabled { get; }

    /// <summary>
    /// Gets the help group for this command.
    /// When null, the command appears in the "Other Commands:" catch-all section.
    /// </summary>
    internal virtual HelpGroup HelpGroup => HelpGroup.None;

    private readonly CliExecutionContext _executionContext;

    protected CliExecutionContext ExecutionContext => _executionContext;

    protected IInteractionService InteractionService { get; }

    protected AspireCliTelemetry Telemetry { get; }

    protected BaseCommand(string name, string description, CommonCommandServices services) : base(name, description)
    {
        var features = services.Features;
        var updateNotifier = services.UpdateNotifier;

        _executionContext = services.ExecutionContext;
        InteractionService = services.InteractionService;
        Telemetry = services.Telemetry;
        SetAction((Func<ParseResult, CancellationToken, Task<int>>)(async (parseResult, cancellationToken) =>
        {
            // Set the command on the execution context so background services can access it
            _executionContext.Command = this;

            // Route human-readable output to stderr when JSON is requested so
            // that only machine-readable data appears on stdout.
            if (IsJsonFormatRequested(parseResult))
            {
                InteractionService.Console = ConsoleOutput.Error;
            }

            // TODO: SDK install goes here in the future.

            CommandResult result;
            var stoppingMessageShown = false;
            try
            {
                var handlerTask = ExecuteAsync(parseResult, cancellationToken);
                services.CancellationManager.SetStartedHandler(handlerTask);

                // Wait for either the handler to complete or a termination signal to trigger cancellation and timeout.
                var terminationTask = services.CancellationManager.ProcessTerminationCompletionSource.Task;

                // After cancellation is triggered, show "Stopping Aspire..." after 200ms if the
                // handler hasn't completed yet, so the user knows shutdown is in progress.
                var stoppingMessageTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var stoppingMessageRegistration = cancellationToken.Register(() =>
                    Task.Delay(200).ContinueWith(_ => stoppingMessageTcs.TrySetResult(), TaskScheduler.Default));

                var tasksToAwait = new List<Task> { handlerTask, terminationTask, stoppingMessageTcs.Task };
                while (true)
                {
                    var firstCompletedTask = await Task.WhenAny(tasksToAwait);
                    if (firstCompletedTask == handlerTask)
                    {
                        result = await handlerTask;
                        break;
                    }
                    else if (firstCompletedTask == terminationTask)
                    {
                        // ProcessTerminationCompletionSource was signaled — either the graceful-shutdown
                        // timeout elapsed, or a second signal forced immediate termination.
                        // handlerTask is not awaited because the process is shutting down and we assume the task is hung.
                        services.LoggerFactory.CreateLogger<BaseCommand>().LogWarning("Termination signal forced process exit.");
                        var exitCode = await terminationTask;
                        result = CommandResult.FromExitCode(exitCode);
                        break;
                    }
                    else
                    {
                        // 200ms elapsed after cancellation — show stopping message and continue waiting.
                        stoppingMessageShown = true;
                        InteractionService.DisplayCancellationMessage();
                        tasksToAwait.Remove(stoppingMessageTcs.Task);
                    }
                }
            }
            catch (NonInteractiveException)
            {
                // Error messages have already been displayed by the interaction service.
                result = CommandResult.Failure(CliExitCodes.MissingRequiredArgument);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested || ex is ExtensionOperationCanceledException)
            {
                result = CommandResult.Cancelled();
            }
            catch (Exception ex)
            {
                var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
                Telemetry.RecordError(errorMessage, ex);
                result = CommandResult.Failure(CliExitCodes.InvalidCommand, errorMessage);
            }

            var isErrorExitCode = result.ExitCode != CliExitCodes.Success;

            if (result.ErrorMessage is not null)
            {
                InteractionService.DisplayError(result.ErrorMessage);
            }

            if (result.ShouldDisplayHelp)
            {
                new HelpAction().Invoke(parseResult);
                await FlushExtensionInteractionServiceAsync(InteractionService).ConfigureAwait(false);

                return result.ExitCode;
            }

            if (result.ShouldDisplayCancellationMessage && !stoppingMessageShown)
            {
                InteractionService.DisplayCancellationMessage(isErrorExitCode ? ConsoleOutput.Error : null);
            }

            // Display the CLI log file path on non-zero exit codes so the user knows
            // where to find diagnostic details. Suppress for user-input errors where
            // the log wouldn't contain useful context (e.g., missing required arguments).
            if (isErrorExitCode && !s_suppressErrorLogsMessageExitCodes.Contains(result.ExitCode))
            {
                InteractionService.DisplayMessage(
                    KnownEmojis.PageFacingUp,
                    string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, MarkupHelpers.SafeFileLink(InteractionService, _executionContext.LogFilePath)),
                    allowMarkup: true,
                    consoleOverride: ConsoleOutput.Error);

                // If we connected to a running app host, also display the log file path of
                // the CLI process that launched it so users can diagnose issues in both processes.
                if (ExecutionContext.AppHostCliLogFilePath is not null)
                {
                    InteractionService.DisplayMessage(
                        KnownEmojis.MagnifyingGlassTiltedLeft,
                        string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeAppHostLogsAt, MarkupHelpers.SafeFileLink(InteractionService, ExecutionContext.AppHostCliLogFilePath)),
                        allowMarkup: true,
                        consoleOverride: ConsoleOutput.Error);
                }
            }

            if (UpdateNotificationsEnabled && !IsJsonFormatRequested(parseResult) && features.IsFeatureEnabled(KnownFeatures.UpdateNotificationsEnabled, true))
            {
                try
                {
                    updateNotifier.NotifyIfUpdateAvailable();
                }
                catch
                {
                    // Ignore any errors during update check to avoid impacting the main command
                }
            }

            await FlushExtensionInteractionServiceAsync(InteractionService).ConfigureAwait(false);

            return result.ExitCode;
        }));
    }

    protected abstract Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether this command has a --format option whose parsed value is <see cref="OutputFormat.Json"/>.
    /// </summary>
    private bool IsJsonFormatRequested(ParseResult parseResult)
    {
        foreach (var option in Options)
        {
            if (option.Name == "--format" && option is Option<OutputFormat> formatOption)
            {
                return parseResult.GetValue(formatOption) == OutputFormat.Json;
            }
        }

        return false;
    }

    private static async Task FlushExtensionInteractionServiceAsync(IInteractionService interactionService)
    {
        if (interactionService is not IExtensionInteractionService extensionInteractionService)
        {
            return;
        }

        // Command cancellation has already been translated into CommandResult; using a canceled
        // token here would skip the final debug-console drain or throw after the command selected
        // its exit code. Bound the drain separately so a broken extension cannot hang CLI exit.
        using var flushCancellationTokenSource = new CancellationTokenSource(s_extensionInteractionFlushTimeout);
        try
        {
            await extensionInteractionService.FlushAsync(flushCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (flushCancellationTokenSource.IsCancellationRequested)
        {
            // Prefer returning the command's chosen exit code over hanging indefinitely when
            // VS Code has already gone away or stopped responding to backchannel requests.
        }
    }

    internal static CommandResult HandleProjectLocatorException(ProjectLocatorException ex, IInteractionService InteractionService, AspireCliTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(InteractionService);

        var (exitCode, errorMessage) = ProjectLocatorErrorHelper.GetExitCodeAndMessage(ex);

        telemetry.RecordError(errorMessage, ex);
        return CommandResult.Failure(exitCode, errorMessage);
    }

    internal static void AddNonInteractiveRequiresYesValidator(Command command, Option<bool> yesOption)
    {
        command.Validators.Add(result =>
        {
            var nonInteractive = result.GetValue(RootCommand.NonInteractiveOption);
            var yes = result.GetValue(yesOption);
            if (nonInteractive && !yes)
            {
                result.AddError(string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.NonInteractiveRequiresYesFormat, command.Name));
            }
        });
    }
}
