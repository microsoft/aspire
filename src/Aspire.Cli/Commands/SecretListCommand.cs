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

    private readonly SecretStoreResolver _secretStoreResolver;

    public SecretListCommand(
        IInteractionService interactionService,
        SecretStoreResolver secretStoreResolver,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("list", SecretCommandStrings.ListDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _secretStoreResolver = secretStoreResolver;

        Options.Add(SecretCommand.s_appHostOption);
        Options.Add(s_formatOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectFile = parseResult.GetValue(SecretCommand.s_appHostOption);
        var format = parseResult.GetValue(s_formatOption);

        var result = await _secretStoreResolver.ResolveAsync(projectFile, autoInit: false, cancellationToken);
        if (result is null)
        {
            InteractionService.DisplayError(SecretCommandStrings.CouldNotFindAppHost);
            return ExitCodeConstants.FailedToFindProject;
        }

        var secrets = result.Store.ToList();

        if (format == OutputFormat.Json)
        {
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

        return ExitCodeConstants.Success;
    }
}
