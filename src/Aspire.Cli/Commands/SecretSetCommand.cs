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

    private readonly SecretStoreResolver _secretStoreResolver;

    public SecretSetCommand(
        IInteractionService interactionService,
        SecretStoreResolver secretStoreResolver,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("set", SecretCommandStrings.SetDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _secretStoreResolver = secretStoreResolver;

        Arguments.Add(s_keyArgument);
        Arguments.Add(s_valueArgument);
        Options.Add(SecretCommand.s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Argument arity guarantees non-null
        var key = parseResult.GetValue(s_keyArgument)!;
        var value = parseResult.GetValue(s_valueArgument)!;
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);

        // autoInit: true — when setting a secret, automatically initialize user secrets
        // if not yet configured (e.g., run 'dotnet user-secrets init' for csproj projects)
        var result = await _secretStoreResolver.ResolveAsync(projectFile, autoInit: true, cancellationToken);
        if (result is null)
        {
            InteractionService.DisplayError(SecretCommandStrings.CouldNotFindAppHost);
            return ExitCodeConstants.FailedToFindProject;
        }

        result.Store.Set(key, value);
        result.Store.Save();

        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, SecretCommandStrings.SecretSetSuccess, key));
        return ExitCodeConstants.Success;
    }
}
