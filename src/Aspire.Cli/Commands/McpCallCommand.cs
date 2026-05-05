// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Commands;

/// <summary>
/// Calls an MCP tool on a running Aspire resource.
/// </summary>
internal sealed class McpCallCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;

    private static readonly Argument<string> s_resourceArgument = new("resource")
    {
        Description = McpCommandStrings.CallCommand_ResourceArgumentDescription
    };

    private static readonly Argument<string> s_toolArgument = new("tool")
    {
        Description = McpCommandStrings.CallCommand_ToolArgumentDescription
    };

    private static readonly Option<string?> s_inputOption = new("--input", "-i")
    {
        Description = McpCommandStrings.CallCommand_InputOptionDescription
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    public McpCallCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IProjectLocator projectLocator,
        AspireCliTelemetry telemetry,
        ILogger<McpCallCommand> logger)
        : base("call", McpCommandStrings.CallCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, projectLocator, executionContext, logger);

        Arguments.Add(s_resourceArgument);
        Arguments.Add(s_toolArgument);
        Options.Add(s_inputOption);
        Options.Add(s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var toolName = parseResult.GetValue(s_toolArgument)!;
        var inputJson = parseResult.GetValue(s_inputOption);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);

        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, "call MCP tool on"),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            return AppHostConnectionResultHandler.DisplayFailureAsError(result, _interactionService, ExitCodeConstants.FailedToDotnetRunAppHost);
        }

        var connection = result.Connection!;

        // Parse input JSON into arguments dictionary
        IReadOnlyDictionary<string, JsonElement>? arguments = null;
        if (!string.IsNullOrEmpty(inputJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(inputJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _interactionService.DisplayError("Invalid JSON input: expected a JSON object.");
                    return ExitCodeConstants.InvalidCommand;
                }
                var dict = new Dictionary<string, JsonElement>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
                arguments = dict;
            }
            catch (JsonException ex)
            {
                _interactionService.DisplayError($"Invalid JSON input: {ex.Message}");
                return ExitCodeConstants.InvalidCommand;
            }
        }

        try
        {
            var callResult = await connection.CallResourceMcpToolAsync(
                resourceName,
                toolName,
                arguments,
                cancellationToken);

            // Output the result content
            if (callResult.Content is { Count: > 0 })
            {
                foreach (var content in callResult.Content)
                {
                    if (content is TextContentBlock textContent)
                    {
                        _interactionService.DisplayRawText(textContent.Text);
                    }
                    else
                    {
                        using var stream = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", content.Type);
                            writer.WriteEndObject();
                        }
                        _interactionService.DisplayRawText(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
                    }
                }
            }

            if (callResult.IsError == true)
            {
                return ExitCodeConstants.InvalidCommand;
            }

            return ExitCodeConstants.Success;
        }
        catch (Exception ex)
        {
            _interactionService.DisplayError($"Failed to call tool '{toolName}' on resource '{resourceName}': {ex.Message}");
            return ExitCodeConstants.InvalidCommand;
        }
    }
}
