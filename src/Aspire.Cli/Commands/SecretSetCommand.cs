// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Cli.Secrets;

namespace Aspire.Cli.Commands;

/// <summary>
/// Sets a secret value for an AppHost project.
/// </summary>
internal sealed class SecretSetCommand : BaseCommand
{
    private static readonly Argument<string> s_keyArgument = new("key")
    {
        Description = SecretCommandStrings.KeyArgumentDescription
    };

    private static readonly Argument<string> s_valueArgument = new("value")
    {
        Description = SecretCommandStrings.ValueArgumentDescription
    };

    private readonly AspireSecretsStoreResolver _secretsStoreResolver;

    public SecretSetCommand(
        AspireSecretsStoreResolver secretsStoreResolver,
        CommonCommandServices services)
        : base("set", SecretCommandStrings.SetDescription, services)
    {
        _secretsStoreResolver = secretsStoreResolver;

        Arguments.Add(s_keyArgument);
        Arguments.Add(s_valueArgument);
        Options.Add(SecretCommand.s_appHostOption);
        Options.Add(SecretCommand.s_environmentOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Argument arity guarantees non-null
        var key = parseResult.GetValue(s_keyArgument)!;
        var value = parseResult.GetValue(s_valueArgument)!;
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);
        var environment = parseResult.GetValue(SecretCommand.s_environmentOption);

        var result = await _secretsStoreResolver.ResolveAsync(projectFile, environment, autoInitDevelopmentUserSecrets: true, cancellationToken);
        if (result is null)
        {
            return CommandResult.Failure(CliExitCodes.FailedToFindProject, SecretCommandStrings.CouldNotFindAppHost);
        }

        result.AspireStore.Set(key, value);
        result.AspireStore.Save();

        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, SecretCommandStrings.SecretSetSuccess, key));
        return CommandResult.Success();
    }
}
