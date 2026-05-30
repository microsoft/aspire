// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json.Nodes;
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
/// Lists all secrets for an AppHost project.
/// </summary>
internal sealed class SecretListCommand : BaseCommand
{
    private static readonly Option<OutputFormat?> s_formatOption = new("--format")
    {
        Description = SecretCommandStrings.FormatOptionDescription
    };

    private static readonly Option<bool> s_allEnvironmentsOption = new("--all")
    {
        Description = SecretCommandStrings.AllEnvironmentsOptionDescription,
        Aliases = { "--all-environments" }
    };

    private readonly AspireSecretsStoreResolver _secretsStoreResolver;

    public SecretListCommand(
        IInteractionService interactionService,
        AspireSecretsStoreResolver secretsStoreResolver,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("list", SecretCommandStrings.ListDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _secretsStoreResolver = secretsStoreResolver;

        Options.Add(SecretCommand.s_appHostOption);
        Options.Add(SecretCommand.s_environmentOption);
        Options.Add(s_allEnvironmentsOption);
        Options.Add(s_formatOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);
        var environment = parseResult.GetValue(SecretCommand.s_environmentOption);
        var format = parseResult.GetValue(s_formatOption);
        var allEnvironments = parseResult.GetValue(s_allEnvironmentsOption);

        if (allEnvironments)
        {
            if (!string.IsNullOrWhiteSpace(environment))
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, SecretCommandStrings.AllEnvironmentsCannotUseEnvironment);
            }

            var results = await _secretsStoreResolver.ResolveAllExistingAsync(projectFile, cancellationToken);
            if (results is null)
            {
                return CommandResult.Failure(CliExitCodes.FailedToFindProject, SecretCommandStrings.CouldNotFindAppHost);
            }

            DisplayAllEnvironments(results, format);
            return CommandResult.Success();
        }

        var result = await _secretsStoreResolver.ResolveAsync(projectFile, environment, cancellationToken);
        if (result is null)
        {
            return CommandResult.Failure(CliExitCodes.FailedToFindProject, SecretCommandStrings.CouldNotFindAppHost);
        }

        var secrets = result.GetReadStore().ToList();

        if (format == OutputFormat.Json)
        {
            // `aspire secret list --format json` uses a dynamic object keyed by secret name;
            // keep docs/specs/cli-output-formats.md in sync when changing this shape.
            var obj = new JsonObject();
            foreach (var (key, value) in secrets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            {
                obj[key] = value;
            }

            var json = obj.ToJsonString(SecretsStore.s_jsonOptions);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            if (secrets.Count == 0)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, SecretCommandStrings.NoSecretsConfigured);
            }
            else
            {
                var table = new Table();
                table.AddBoldColumn(SecretCommandStrings.KeyColumnHeader, noWrap: true);
                table.AddBoldColumn(SecretCommandStrings.ValueColumnHeader);

                foreach (var (key, value) in secrets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
                {
                    table.AddRow(
                        $"[cyan]{key.EscapeMarkup()}[/]",
                        $"[yellow]{value.EscapeMarkup()}[/]");
                }

                InteractionService.DisplayRenderable(table);
            }
        }

        return CommandResult.Success();
    }

    private void DisplayAllEnvironments(IReadOnlyList<AspireSecretsStoreResult> results, OutputFormat? format)
    {
        var environments = results
            .Select(result => new
            {
                result.EnvironmentName,
                Secrets = result.GetReadStore().ToList()
            })
            .Where(result => result.Secrets.Count > 0)
            .OrderBy(result => result.EnvironmentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (format == OutputFormat.Json)
        {
            var root = new JsonObject();
            foreach (var environment in environments)
            {
                var obj = new JsonObject();
                foreach (var (key, value) in environment.Secrets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
                {
                    obj[key] = value;
                }

                root[environment.EnvironmentName] = obj;
            }

            var json = root.ToJsonString(SecretsStore.s_jsonOptions);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return;
        }

        if (environments.Count == 0)
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, SecretCommandStrings.NoSecretsConfigured);
            return;
        }

        var table = new Table();
        table.AddBoldColumn(SecretCommandStrings.EnvironmentColumnHeader, noWrap: true);
        table.AddBoldColumn(SecretCommandStrings.KeyColumnHeader, noWrap: true);
        table.AddBoldColumn(SecretCommandStrings.ValueColumnHeader);

        foreach (var environment in environments)
        {
            foreach (var (key, value) in environment.Secrets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(
                    $"[green]{environment.EnvironmentName.EscapeMarkup()}[/]",
                    $"[cyan]{key.EscapeMarkup()}[/]",
                    $"[yellow]{value.EscapeMarkup()}[/]");
            }
        }

        InteractionService.DisplayRenderable(table);
    }
}
