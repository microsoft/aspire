// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for API documentation operations under <c>docs</c>.
/// </summary>
internal sealed class ApiCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiCommand"/> class.
    /// </summary>
    /// <param name="listCommand">The scoped browse command.</param>
    /// <param name="searchCommand">The search command.</param>
    /// <param name="getCommand">The content retrieval command.</param>
    /// <param name="interactionService">The interaction service.</param>
    /// <param name="features">The feature flag service.</param>
    /// <param name="updateNotifier">The update notifier.</param>
    /// <param name="executionContext">The CLI execution context.</param>
    /// <param name="telemetry">The telemetry service.</param>
    public ApiCommand(
        ApiListCommand listCommand,
        ApiSearchCommand searchCommand,
        ApiGetCommand getCommand,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("api", ApiCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(listCommand);
        Subcommands.Add(searchCommand);
        Subcommands.Add(getCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }
}
