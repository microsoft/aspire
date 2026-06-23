// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to search Aspire documentation by keywords.
/// </summary>
internal sealed class DocsSearchCommand : BaseCommand
{
    private readonly IDocsSearchService _docsSearchService;
    private readonly ILogger<DocsSearchCommand> _logger;

    private static readonly Argument<string> s_queryArgument = new("query")
    {
        Description = DocsCommandStrings.QueryArgumentDescription
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = DocsCommandStrings.FormatOptionDescription
    };

    private static readonly Option<int?> s_limitOption = new("--limit", "-n")
    {
        Description = DocsCommandStrings.LimitOptionDescription
    };

    public DocsSearchCommand(
        IDocsSearchService docsSearchService,
        ILogger<DocsSearchCommand> logger,
        CommonCommandServices services)
        : base("search", DocsCommandStrings.SearchDescription, services)
    {
        _docsSearchService = docsSearchService;
        _logger = logger;

        Arguments.Add(s_queryArgument);
        Options.Add(s_formatOption);
        Options.Add(s_limitOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var query = parseResult.GetValue(s_queryArgument)!;
        var format = parseResult.GetValue(s_formatOption);
        var limit = Math.Clamp(parseResult.GetValue(s_limitOption) ?? 5, 1, 10);

        _logger.LogDebug("Searching documentation for '{Query}' (limit: {Limit})", query, limit);

        // Search docs with status indicator
        var response = await InteractionService.ShowStatusAsync(
            DocsCommandStrings.LoadingDocumentation,
            async () => await _docsSearchService.SearchAsync(query, limit, cancellationToken));

        if (response is null || response.Results.Count is 0)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, DocsCommandStrings.NoResultsFound, query));
            return CommandResult.Success(); // Not an error, just no results
        }

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(response.Results.ToArray(), JsonSourceGenerationContext.RelaxedEscaping.SearchResultArray);
            // Structured output always goes to stdout.
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, DocsCommandStrings.FoundSearchResults, response.Results.Count, query));

            // Results are already sorted by score (highest first) from the search service
            var table = new Table();
            table.AddBoldColumn(DocsCommandStrings.HeaderTitle);
            table.AddBoldColumn(DocsCommandStrings.HeaderSlug);
            table.AddBoldColumn(DocsCommandStrings.HeaderSection);
            table.AddBoldColumn(DocsCommandStrings.HeaderScore);

            foreach (var result in response.Results)
            {
                table.AddRow(
                    Markup.Escape(result.Title),
                    Markup.Escape(result.Slug),
                    Markup.Escape(result.Section ?? "-"),
                    result.Score.ToString("F2", CultureInfo.InvariantCulture)); // Two decimal places
            }

            InteractionService.DisplayRenderable(table);
        }

        return CommandResult.Success();
    }
}
