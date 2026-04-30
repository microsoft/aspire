// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal abstract class BaseCommand : Command
{
    protected virtual bool UpdateNotificationsEnabled { get; } = true;

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
        SetAction(async (parseResult, cancellationToken) =>
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

            int exitCode;
            try
            {
                exitCode = await ExecuteAsync(parseResult, cancellationToken);
            }
            catch (NonInteractiveException)
            {
                // Error messages have already been displayed by the interaction service.
                exitCode = ExitCodeConstants.MissingRequiredArgument;
            }

            if (UpdateNotificationsEnabled && features.IsFeatureEnabled(KnownFeatures.UpdateNotificationsEnabled, true))
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

            return exitCode;
        });
    }

    protected abstract Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken);

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

    internal static int HandleProjectLocatorException(ProjectLocatorException ex, IInteractionService interactionService, AspireCliTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(interactionService);

        var (exitCode, errorMessage) = ProjectLocatorErrorHelper.GetExitCodeAndMessage(ex);

        telemetry.RecordError(errorMessage, ex);
        interactionService.DisplayError(errorMessage);
        return exitCode;
    }
}
