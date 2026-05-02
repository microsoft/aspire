// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class CandidateAppHostDisplayInfo
{
    public required string AppHostPath { get; init; }
}

[JsonSerializable(typeof(List<CandidateAppHostDisplayInfo>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class LsCommandJsonContext : JsonSerializerContext
{
    private static LsCommandJsonContext? s_relaxedEscaping;

    public static LsCommandJsonContext RelaxedEscaping => s_relaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed class LsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IInteractionService _interactionService;
    private readonly IProjectLocator _projectLocator;
    private readonly CliExecutionContext _executionContext;

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = SharedCommandStrings.LsFormatOptionDescription
    };

    public LsCommand(
        IInteractionService interactionService,
        IProjectLocator projectLocator,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("ls", SharedCommandStrings.LsCommandDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _projectLocator = projectLocator;
        _executionContext = executionContext;

        Options.Add(s_formatOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var format = parseResult.GetValue(s_formatOption);
        var appHosts = await _projectLocator.FindAppHostProjectFilesAsync(_executionContext.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        var appHostInfos = appHosts.Select(a => new CandidateAppHostDisplayInfo { AppHostPath = a.FullName }).ToList();

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(appHostInfos, LsCommandJsonContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else if (appHostInfos.Count == 0)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, SharedCommandStrings.LsNoCandidateAppHostsFound);
        }
        else
        {
            DisplayTable(appHostInfos);
        }

        return ExitCodeConstants.Success;
    }

    private void DisplayTable(List<CandidateAppHostDisplayInfo> appHosts)
    {
        var shortPaths = FileSystemHelper.ShortenPaths(appHosts.Select(a => a.AppHostPath).ToList());

        var table = new Table();
        table.AddBoldColumn(SharedCommandStrings.HeaderPath);

        foreach (var appHost in appHosts)
        {
            table.AddRow(Markup.Escape(shortPaths[appHost.AppHostPath]));
        }

        _interactionService.DisplayRenderable(table);
    }
}
