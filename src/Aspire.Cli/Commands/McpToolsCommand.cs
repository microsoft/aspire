// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Lists MCP tools exposed by running Aspire resources.
/// </summary>
internal sealed class McpToolsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);
    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = McpCommandStrings.ToolsCommand_FormatOptionDescription
    };

    public McpToolsCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<McpToolsCommand> logger)
        : base("tools", McpCommandStrings.ToolsCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);

        Options.Add(s_appHostOption);
        Options.Add(s_formatOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var format = parseResult.GetValue(s_formatOption);

        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, "list MCP tools for"),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, result.ErrorMessage);
            return ExitCodeConstants.Success;
        }

        var connection = result.Connection!;
        var snapshots = await connection.GetResourceSnapshotsAsync(cancellationToken);
        var resourcesWithTools = snapshots.Where(r => r.McpServer is not null).ToList();

        if (resourcesWithTools.Count == 0)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, "No resources with MCP tools found.");
            return ExitCodeConstants.Success;
        }

        if (format == OutputFormat.Json)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                foreach (var r in resourcesWithTools)
                {
                    var resourceName = r.DisplayName ?? r.Name;
                    foreach (var t in r.McpServer!.Tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("resource", resourceName);
                        writer.WriteString("tool", t.Name);
                        writer.WriteString("description", t.Description ?? "");
                        writer.WritePropertyName("inputSchema");
                        t.InputSchema.WriteTo(writer);
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();
            }

            _interactionService.DisplayRawText(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        else
        {
            var table = new Table();
            table.AddColumn("Resource");
            table.AddColumn("Tool");
            table.AddColumn("Description");

            foreach (var resource in resourcesWithTools)
            {
                var resourceName = resource.DisplayName ?? resource.Name;
                foreach (var tool in resource.McpServer!.Tools)
                {
                    table.AddRow(
                        resourceName.EscapeMarkup(),
                        tool.Name.EscapeMarkup(),
                        (tool.Description ?? "").EscapeMarkup());
                }
            }

            _interactionService.DisplayRenderable(table);
        }

        return ExitCodeConstants.Success;
    }
}
