// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to fetch an API reference page by identifier.
/// </summary>
internal sealed class ApiGetCommand : BaseCommand
{
    private readonly IApiDocsIndexService _apiDocsIndexService;

    private static readonly Argument<string> s_idArgument = new("id")
    {
        Description = ApiCommandStrings.IdArgumentDescription
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = ApiCommandStrings.FormatOptionDescription
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiGetCommand"/> class.
    /// </summary>
    /// <param name="apiDocsIndexService">The API docs index service.</param>
    /// <param name="services">Common command services.</param>
    public ApiGetCommand(
        IApiDocsIndexService apiDocsIndexService,
        CommonCommandServices services)
        : base("get", ApiCommandStrings.GetDescription, services)
    {
        _apiDocsIndexService = apiDocsIndexService;

        Arguments.Add(s_idArgument);
        Options.Add(s_formatOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var id = parseResult.GetValue(s_idArgument)!;
        var format = parseResult.GetValue(s_formatOption);

        var item = await InteractionService.ShowStatusAsync(
            ApiCommandStrings.LoadingApiDocumentation,
            async () => await _apiDocsIndexService.GetAsync(id, cancellationToken));

        if (item is null)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, string.Format(CultureInfo.CurrentCulture, ApiCommandStrings.ApiNotFound, id));
        }

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(item, JsonSourceGenerationContext.RelaxedEscaping.ApiContent);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return CommandResult.Success();
        }

        InteractionService.DisplayMarkdown(item.Content);
        return CommandResult.Success();
    }
}
