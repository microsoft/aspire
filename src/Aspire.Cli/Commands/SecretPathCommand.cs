// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Secrets;

namespace Aspire.Cli.Commands;

/// <summary>
/// Shows the user secrets file path for an AppHost project.
/// </summary>
internal sealed class SecretPathCommand : BaseCommand
{
    private readonly AspireSecretsStoreResolver _secretsStoreResolver;

    public SecretPathCommand(
        AspireSecretsStoreResolver secretsStoreResolver,
        CommonCommandServices services)
        : base("path", SecretCommandStrings.PathDescription, services)
    {
        _secretsStoreResolver = secretsStoreResolver;

        Options.Add(SecretCommand.s_appHostOption);
        Options.Add(SecretCommand.s_environmentOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);
        var environment = parseResult.GetValue(SecretCommand.s_environmentOption);

        var result = await _secretsStoreResolver.ResolveAsync(projectFile, environment, cancellationToken);
        if (result is null)
        {
            return CommandResult.Failure(CliExitCodes.FailedToFindProject, SecretCommandStrings.CouldNotFindAppHost);
        }

        InteractionService.DisplayRawText(result.AspireSecretsFilePath, consoleOverride: ConsoleOutput.Standard);
        return CommandResult.Success();
    }
}
