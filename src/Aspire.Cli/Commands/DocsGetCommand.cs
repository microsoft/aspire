// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to get the full content of a documentation page by its slug.
/// </summary>
internal sealed class DocsGetCommand : BaseCommand
{
    private readonly IDocsIndexService _docsIndexService;
    private readonly ILogger<DocsGetCommand> _logger;

    private static readonly Argument<string> s_slugArgument = new("slug")
    {
        Description = DocsCommandStrings.SlugArgumentDescription
    };

    private static readonly Option<string?> s_sectionOption = new("--section")
    {
        Description = DocsCommandStrings.SectionOptionDescription
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = DocsCommandStrings.FormatOptionDescription
    };

    public DocsGetCommand(
        IInteractionService interactionService,
        IDocsIndexService docsIndexService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<DocsGetCommand> logger)
        : base("get", DocsCommandStrings.GetDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _docsIndexService = docsIndexService;
        _logger = logger;

        Arguments.Add(s_slugArgument);
        Options.Add(s_sectionOption);
        Options.Add(s_formatOption);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        System.Diagnostics.Debugger.Launch();

        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var slug = parseResult.GetValue(s_slugArgument)!;
        var section = parseResult.GetValue(s_sectionOption);
        var format = parseResult.GetValue(s_formatOption);

        _logger.LogDebug("Getting documentation for slug '{Slug}' (section: {Section})", slug, section ?? "(all)");

        // Get doc with status indicator
        var doc = await InteractionService.ShowStatusAsync(
            DocsCommandStrings.LoadingDocumentation,
            async () => await _docsIndexService.GetDocumentAsync(slug, section, cancellationToken));

        if (doc is null)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, DocsCommandStrings.DocumentNotFound, slug));
            return ExitCodeConstants.InvalidCommand;
        }

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(doc, JsonSourceGenerationContext.RelaxedEscaping.DocsContent);
            // Structured output always goes to stdout.
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            InteractionService.DisplayMarkdown(
                """
                ## API

                The API section configures authentication for the dashboard’s HTTP API endpoints.
                | Option | Description |
                | ----------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
                | `Dashboard:Api:Enabled` Default: `false` | Enables the Telemetry HTTP API (`/api/telemetry/*`) endpoints. When `false`, the endpoints are not registered. |
                | `Dashboard:Api:AuthMode` Default: `Unsecured` | Can be set to `ApiKey` or `Unsecured`. `Unsecured` should only be used during local development. If an API key is configured and no auth mode is specified, defaults to `ApiKey`. |
                | `Dashboard:Api:PrimaryApiKey` Default: `null` | Specifies the primary API key. The API key can be any text, but a value with at least 128 bits of entropy is recommended. This value is required if auth mode is API key. |
                | `Dashboard:Api:SecondaryApiKey` Default: `null` | Specifies the secondary API key. The API key can be any text, but a value with at least 128 bits of entropy is recommended. This value is optional. |
                """, maxWidth: 100);

            InteractionService.DisplayMarkdown(doc.Content, maxWidth: 100);
        }

        return ExitCodeConstants.Success;
    }
}
