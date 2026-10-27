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
        Description = SecretCommandStrings.ValueArgumentDescription,
        Arity = ArgumentArity.ZeroOrOne
    };

    private static readonly Option<string> s_envvarSourceOption = new("--envvar")
    {
        Description = SecretCommandStrings.EnvvarSourceOptionDescription,
        HelpName = "variable-name",
        Arity = ArgumentArity.ExactlyOne
    };

    private readonly AspireSecretsStoreResolver _secretsStoreResolver;
    private readonly IEnvironment _environment;

    public SecretSetCommand(
        AspireSecretsStoreResolver secretsStoreResolver,
        IEnvironment environment,
        CommonCommandServices services)
        : base("set", SecretCommandStrings.SetDescription, services)
    {
        _secretsStoreResolver = secretsStoreResolver;
        _environment = environment;

        Arguments.Add(s_keyArgument);
        Arguments.Add(s_valueArgument);
        Options.Add(SecretCommand.s_appHostOption);
        Options.Add(SecretCommand.s_environmentOption);
        Options.Add(s_envvarSourceOption);

        Validators.Add(result =>
        {
            // An explicitly empty positional value still counts as a supplied source.
            var hasValue = result.GetResult(s_valueArgument)?.Tokens.Count > 0;
            var hasEnvvarSource = result.GetResult(s_envvarSourceOption) is { Implicit: false };
            if (hasValue && hasEnvvarSource)
            {
                result.AddError(SecretCommandStrings.ConflictingValueSources);
            }
            else if (!hasValue && !hasEnvvarSource && result.GetValue(RootCommand.NonInteractiveOption))
            {
                result.AddError(SecretCommandStrings.ValueSourceRequired);
            }
            else if (hasEnvvarSource && result.GetResult(s_envvarSourceOption)?.Tokens.Count > 0
                && string.IsNullOrWhiteSpace(result.GetValue(s_envvarSourceOption)))
            {
                result.AddError(SecretCommandStrings.EnvvarSourceNameRequired);
            }
        });
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Argument arity guarantees non-null
        var key = parseResult.GetValue(s_keyArgument)!;
        var sourceName = parseResult.GetValue(s_envvarSourceOption);
        string value;
        if (sourceName is not null)
        {
            var environmentValue = _environment.GetEnvironmentVariable(sourceName);
            if (environmentValue is null)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand,
                    string.Format(CultureInfo.CurrentCulture, SecretCommandStrings.EnvvarSourceNotSet, sourceName));
            }

            value = environmentValue;
        }
        else
        {
            value = parseResult.GetValue(s_valueArgument)
                ?? await InteractionService.PromptForStringAsync(SecretCommandStrings.ValuePrompt,
                    isSecret: true, required: false, cancellationToken: cancellationToken);
        }

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
