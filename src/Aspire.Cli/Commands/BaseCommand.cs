// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal abstract class BaseCommand : Command
{
    private static readonly int[] s_suppressErrorLogsMessageExitCodes = [CliExitCodes.Cancelled, CliExitCodes.MissingRequiredArgument];

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

    protected BaseCommand(string name, string description, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, IInteractionService interactionService, AspireCliTelemetry telemetry) : base(name, description)
    {
        _executionContext = executionContext;
        InteractionService = interactionService;
        Telemetry = telemetry;
        SetAction((Func<ParseResult, CancellationToken, Task<int>>)(async (parseResult, cancellationToken) =>
        {
            // Set the command on the execution context so background services can access it
            _executionContext.Command = this;

            // Route human-readable output to stderr when JSON is requested so
            // that only machine-readable data appears on stdout.
            if (IsJsonFormatRequested(parseResult))
            {
                interactionService.Console = ConsoleOutput.Error;
            }

            // TODO: SDK install goes here in the future.

            CommandResult result;
            try
            {
                result = await ExecuteAsync(parseResult, cancellationToken);
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
                telemetry.RecordError(errorMessage, ex);
                result = CommandResult.Failure(CliExitCodes.InvalidCommand, errorMessage);
            }

            var isErrorExitCode = result.ExitCode != CliExitCodes.Success;

            if (result.ErrorMessage is not null)
            {
                interactionService.DisplayError(result.ErrorMessage);
            }

            if (result.ShouldDisplayHelp)
            {
                new HelpAction().Invoke(parseResult);
                return result.ExitCode;
            }

            if (result.ShouldDisplayCancellationMessage)
            {
                interactionService.DisplayCancellationMessage(isErrorExitCode ? ConsoleOutput.Error : null);
            }

            // Display the CLI log file path on non-zero exit codes so the user knows
            // where to find diagnostic details. Suppress for user-input errors where
            // the log wouldn't contain useful context (e.g., missing required arguments).
            if (isErrorExitCode && !s_suppressErrorLogsMessageExitCodes.Contains(result.ExitCode))
            {
                interactionService.DisplayMessage(
                    KnownEmojis.PageFacingUp,
                    string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, MarkupHelpers.SafeFileLink(interactionService, executionContext.LogFilePath)),
                    allowMarkup: true,
                    consoleOverride: ConsoleOutput.Error);

                // If we connected to a running app host, also display the log file path of
                // the CLI process that launched it so users can diagnose issues in both processes.
                if (executionContext.AppHostCliLogFilePath is not null)
                {
                    interactionService.DisplayMessage(
                        KnownEmojis.MagnifyingGlassTiltedLeft,
                        string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeAppHostLogsAt, MarkupHelpers.SafeFileLink(interactionService, executionContext.AppHostCliLogFilePath)),
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

    internal static CommandResult HandleProjectLocatorException(ProjectLocatorException ex, IInteractionService interactionService, AspireCliTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(interactionService);

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
