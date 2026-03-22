// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal abstract class BaseConfigSubCommand(string name, string description, IFeatures features, ICliUpdateNotifier updateNotifier, IConfigurationService configurationService, CliExecutionContext executionContext, IInteractionService interactionService, AspireCliTelemetry telemetry) : BaseCommand(name, description, features, updateNotifier, executionContext, interactionService, telemetry)
{
    protected IConfigurationService ConfigurationService { get; } = configurationService;

    /// <summary>
    /// Extension-compatible method to execute the subcommand. Prompts for input if necessary.
    /// </summary>
    public abstract Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken);
}
