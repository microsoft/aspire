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
/// Manages AppHost user secrets (set, get, list, delete).
/// </summary>
internal sealed class SecretCommand : BaseCommand
{
    internal static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public SecretCommand(
        SecretSetCommand setCommand,
        SecretGetCommand getCommand,
        SecretListCommand listCommand,
        SecretPathCommand pathCommand,
        SecretDeleteCommand deleteCommand,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("secret", SecretCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(getCommand);
        Subcommands.Add(setCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(pathCommand);
        Subcommands.Add(deleteCommand);
    }

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }
}
