// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to search Aspire API reference documentation.
/// </summary>
internal sealed class ApiSearchCommand : BaseCommand
{
    private readonly IApiDocsIndexService _apiDocsIndexService;

    private static readonly Argument<string> s_queryArgument = new("query")
    {
        Description = ApiCommandStrings.QueryArgumentDescription
    };

    private static readonly Option<string?> s_languageOption = new Option<string?>("--language")
    {
        Description = ApiCommandStrings.LanguageOptionDescription
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = ApiCommandStrings.FormatOptionDescription
    };

    private static readonly Option<int?> s_limitOption = new("--limit", "-n")
    {
        Description = ApiCommandStrings.LimitOptionDescription
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiSearchCommand"/> class.
    /// </summary>
    /// <param name="interactionService">The interaction service.</param>
    /// <param name="apiDocsIndexService">The API docs index service.</param>
    /// <param name="features">The feature flag service.</param>
    /// <param name="updateNotifier">The update notifier.</param>
    /// <param name="executionContext">The CLI execution context.</param>
    /// <param name="telemetry">The telemetry service.</param>
    public ApiSearchCommand(
        IInteractionService interactionService,
        IApiDocsIndexService apiDocsIndexService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("search", ApiCommandStrings.SearchDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _apiDocsIndexService = apiDocsIndexService;

        Arguments.Add(s_queryArgument);
        Options.Add(s_languageOption);
        Options.Add(s_formatOption);
        Options.Add(s_limitOption);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var query = parseResult.GetValue(s_queryArgument)!;
        var language = parseResult.GetValue(s_languageOption);
        var format = parseResult.GetValue(s_formatOption);
        var limit = Math.Clamp(parseResult.GetValue(s_limitOption) ?? 5, 1, 10);

        var items = await InteractionService.ShowStatusAsync(
            ApiCommandStrings.LoadingApiDocumentation,
            async () => await _apiDocsIndexService.SearchAsync(query, language, limit, cancellationToken));

        if (items.Count is 0)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ApiCommandStrings.NoResultsFound, query));
            return ExitCodeConstants.Success;
        }

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize([.. items], JsonSourceGenerationContext.RelaxedEscaping.ApiSearchResultArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return ExitCodeConstants.Success;
        }

        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, ApiCommandStrings.FoundSearchResults, items.Count, query));

        var table = new Table();
        table.AddBoldColumn(ApiCommandStrings.HeaderName);
        table.AddBoldColumn(ApiCommandStrings.HeaderId);
        table.AddBoldColumn(ApiCommandStrings.HeaderLanguage);
        table.AddBoldColumn(ApiCommandStrings.HeaderKind);
        table.AddBoldColumn(ApiCommandStrings.HeaderScore);

        foreach (var item in items)
        {
            table.AddRow(
                Markup.Escape(item.Name),
                Markup.Escape(item.Id),
                Markup.Escape(item.Language),
                Markup.Escape(item.Kind),
                item.Score.ToString("F2", CultureInfo.InvariantCulture));
        }

        InteractionService.DisplayRenderable(table);
        return ExitCodeConstants.Success;
    }
}
