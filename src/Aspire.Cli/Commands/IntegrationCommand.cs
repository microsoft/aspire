// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class IntegrationCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    public IntegrationCommand(
        AddCommand addCommand,
        IntegrationListCommand listCommand,
        IntegrationSearchCommand searchCommand,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("integration", AddCommandStrings.IntegrationCommandDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(addCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(searchCommand);
    }
}
