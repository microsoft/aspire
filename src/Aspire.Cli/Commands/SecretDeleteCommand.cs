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
using Aspire.Shared.UserSecrets;
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

    private readonly AspireSecretsStoreResolver _secretsStoreResolver;

    public SecretDeleteCommand(
        IInteractionService interactionService,
        AspireSecretsStoreResolver secretsStoreResolver,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("delete", SecretCommandStrings.DeleteDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _secretsStoreResolver = secretsStoreResolver;

        Arguments.Add(s_keyArgument);
        Options.Add(SecretCommand.s_appHostOption);
        Options.Add(SecretCommand.s_environmentOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Argument arity guarantees non-null
        var key = parseResult.GetValue(s_keyArgument)!;
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);
        var environment = parseResult.GetValue(SecretCommand.s_environmentOption);

        var result = await _secretsStoreResolver.ResolveAsync(projectFile, environment, cancellationToken);
        if (result is null)
        {
            return CommandResult.Failure(CliExitCodes.FailedToFindProject, SecretCommandStrings.CouldNotFindAppHost);
        }

        var removed = result.AspireStore.Remove(key);
        if (result.LegacyUserSecretsFilePath is { } legacyUserSecretsFilePath && File.Exists(legacyUserSecretsFilePath))
        {
            var legacyStore = new SecretsStore(legacyUserSecretsFilePath);
            if (legacyStore.Remove(key))
            {
                legacyStore.Save();
                removed = true;
            }
        }

        if (!removed)
        {
            return CommandResult.Failure(CliExitCodes.ConfigNotFound, string.Format(CultureInfo.CurrentCulture, SecretCommandStrings.SecretNotFound, key.EscapeMarkup()));
        }

        result.AspireStore.Save();
        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, SecretCommandStrings.SecretDeleteSuccess, key));
        return CommandResult.Success();
    }
}
