// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Secrets;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Shows the user secrets file path for an AppHost project.
/// </summary>
internal sealed class SecretPathCommand : BaseCommand
{
    private readonly SecretStoreResolver _secretStoreResolver;

    public SecretPathCommand(
        IInteractionService interactionService,
        SecretStoreResolver secretStoreResolver,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("path", SecretCommandStrings.PathDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _secretStoreResolver = secretStoreResolver;

        Options.Add(SecretCommand.s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);

        var result = await _secretStoreResolver.ResolveAsync(projectFile, autoInit: false, cancellationToken);
        if (result is null)
        {
            InteractionService.DisplayError(SecretCommandStrings.CouldNotFindAppHost);
            return ExitCodeConstants.FailedToFindProject;
        }

        InteractionService.DisplayPlainText(result.Store.FilePath);
        return ExitCodeConstants.Success;
    }
}
