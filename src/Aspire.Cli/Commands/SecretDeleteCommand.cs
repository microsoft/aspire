// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Secrets;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Deletes a secret from an AppHost project.
/// </summary>
internal sealed class SecretDeleteCommand : BaseCommand
{
    private static readonly Argument<string> s_keyArgument = new("key")
    {
        Description = SecretCommandStrings.KeyDeleteArgumentDescription
    };

    private readonly SecretStoreResolver _secretStoreResolver;

    public SecretDeleteCommand(
        IInteractionService interactionService,
        SecretStoreResolver secretStoreResolver,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("delete", SecretCommandStrings.DeleteDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _secretStoreResolver = secretStoreResolver;

        Arguments.Add(s_keyArgument);
        Options.Add(SecretCommand.s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Argument arity guarantees non-null
        var key = parseResult.GetValue(s_keyArgument)!;
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);

        var result = await _secretStoreResolver.ResolveAsync(projectFile, autoInit: false, cancellationToken);
        if (result is null)
        {
            InteractionService.DisplayError(SecretCommandStrings.CouldNotFindAppHost);
            return ExitCodeConstants.FailedToFindProject;
        }

        if (!result.Store.Remove(key))
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, SecretCommandStrings.SecretNotFound, key.EscapeMarkup()));
            return ExitCodeConstants.ConfigNotFound;
        }

        result.Store.Save();
        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, SecretCommandStrings.SecretDeleteSuccess, key));
        return ExitCodeConstants.Success;
    }
}
