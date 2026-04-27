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
    /// <param name="interactionService">The interaction service.</param>
    /// <param name="apiDocsIndexService">The API docs index service.</param>
    /// <param name="features">The feature flag service.</param>
    /// <param name="updateNotifier">The update notifier.</param>
    /// <param name="executionContext">The CLI execution context.</param>
    /// <param name="telemetry">The telemetry service.</param>
    public ApiGetCommand(
        IInteractionService interactionService,
        IApiDocsIndexService apiDocsIndexService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("get", ApiCommandStrings.GetDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _apiDocsIndexService = apiDocsIndexService;

        Arguments.Add(s_idArgument);
        Options.Add(s_formatOption);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var id = parseResult.GetValue(s_idArgument)!;
        var format = parseResult.GetValue(s_formatOption);

        var item = await InteractionService.ShowStatusAsync(
            ApiCommandStrings.LoadingApiDocumentation,
            async () => await _apiDocsIndexService.GetAsync(id, cancellationToken));

        if (item is null)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ApiCommandStrings.ApiNotFound, id));
            return ExitCodeConstants.InvalidCommand;
        }

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(item, JsonSourceGenerationContext.RelaxedEscaping.ApiContent);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return ExitCodeConstants.Success;
        }

        InteractionService.DisplayMarkdown(item.Content);
        return ExitCodeConstants.Success;
    }
}
