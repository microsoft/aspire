// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for documentation operations. Contains subcommands for listing, searching, and getting docs, including API reference content.
/// </summary>
internal sealed class DocsCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public DocsCommand(
        ApiCommand apiCommand,
        DocsListCommand listCommand,
        DocsSearchCommand searchCommand,
        DocsGetCommand getCommand,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("docs", DocsCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(listCommand);
        Subcommands.Add(searchCommand);
        Subcommands.Add(getCommand);
        Subcommands.Add(apiCommand);
    }
}
