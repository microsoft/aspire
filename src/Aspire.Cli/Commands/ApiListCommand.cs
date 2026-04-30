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
/// Command to list API entries under a specific scope.
/// </summary>
internal sealed class ApiListCommand : BaseCommand
{
    private readonly IApiDocsIndexService _apiDocsIndexService;

    private static readonly Argument<string> s_scopeArgument = new("scope")
    {
        Description = ApiCommandStrings.ScopeArgumentDescription
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = ApiCommandStrings.FormatOptionDescription
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiListCommand"/> class.
    /// </summary>
    /// <param name="interactionService">The interaction service.</param>
    /// <param name="apiDocsIndexService">The API docs index service.</param>
    /// <param name="features">The feature flag service.</param>
    /// <param name="updateNotifier">The update notifier.</param>
    /// <param name="executionContext">The CLI execution context.</param>
    /// <param name="telemetry">The telemetry service.</param>
    public ApiListCommand(
        IInteractionService interactionService,
        IApiDocsIndexService apiDocsIndexService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("list", ApiCommandStrings.ListDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _apiDocsIndexService = apiDocsIndexService;

        Arguments.Add(s_scopeArgument);
        Options.Add(s_formatOption);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var scope = parseResult.GetValue(s_scopeArgument)!;
        var format = parseResult.GetValue(s_formatOption);

        var items = await InteractionService.ShowStatusAsync(
            ApiCommandStrings.LoadingApiDocumentation,
            async () => await _apiDocsIndexService.ListAsync(scope, cancellationToken));

        if (items.Count is 0)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ApiCommandStrings.NoApiEntriesFound, scope));
            return ExitCodeConstants.Success;
        }

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(items.ToArray(), JsonSourceGenerationContext.RelaxedEscaping.ApiListItemArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return ExitCodeConstants.Success;
        }

        InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, ApiCommandStrings.FoundApiEntries, items.Count, scope));

        var table = new Table();
        table.AddBoldColumn(ApiCommandStrings.HeaderName);
        table.AddBoldColumn(ApiCommandStrings.HeaderId);
        table.AddBoldColumn(ApiCommandStrings.HeaderKind);
        table.AddBoldColumn(ApiCommandStrings.HeaderGroup);

        foreach (var item in items)
        {
            table.AddRow(
                Markup.Escape(item.Name),
                Markup.Escape(item.Id),
                Markup.Escape(item.Kind),
                Markup.Escape(item.MemberGroup ?? "-"));
        }

        InteractionService.DisplayRenderable(table);
        return ExitCodeConstants.Success;
    }
}
