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

namespace Aspire.Cli.Commands;

internal sealed class ResourceCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ResourceManagement;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<ResourceCommand> _logger;

    private static readonly Argument<string> s_resourceArgument = new("resource")
    {
        Description = ResourceCommandStrings.CommandResourceArgumentDescription
    };

    private static readonly Argument<string> s_commandArgument = new("command")
    {
        Description = ResourceCommandStrings.CommandNameArgumentDescription
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);
    private static readonly Option<string?> s_argumentsJsonOption = new("--args-json")
    {
        Description = ResourceCommandStrings.CommandArgumentsJsonOptionDescription
    };

    /// <summary>
    /// Well-known commands with their display metadata.
    /// The command name is used directly (no mapping needed since the user-facing names match the actual command names).
    /// </summary>
    private static readonly Dictionary<string, (string ProgressVerb, string BaseVerb, string PastTenseVerb)> s_wellKnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = ("Starting", "start", "started"),
        ["stop"] = ("Stopping", "stop", "stopped"),
        ["restart"] = ("Restarting", "restart", "restarted"),
    };

    public ResourceCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IProjectLocator projectLocator,
        ILogger<ResourceCommand> logger,
        AspireCliTelemetry telemetry)
        : base("resource", ResourceCommandStrings.CommandDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, projectLocator, executionContext, logger);
        _logger = logger;

        Arguments.Add(s_resourceArgument);
        Arguments.Add(s_commandArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_argumentsJsonOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var commandName = parseResult.GetValue(s_commandArgument)!;
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var commandArguments = ParseArguments(parseResult.GetValue(s_argumentsJsonOption));
        if (commandArguments.ParseError is { } parseError)
        {
            _interactionService.DisplayError(parseError);
            return ExitCodeConstants.InvalidCommand;
        }

        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, ResourceCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            return AppHostConnectionResultHandler.DisplayFailureAsError(result, _interactionService, ExitCodeConstants.FailedToFindProject);
        }

        // Map well-known friendly names (start/stop/restart) to their display metadata
        if (s_wellKnownCommands.TryGetValue(commandName, out var knownCommand))
        {
            return await ResourceCommandHelper.ExecuteResourceCommandAsync(
                result.Connection!,
                _interactionService,
                _logger,
                resourceName,
                commandName,
                knownCommand.ProgressVerb,
                knownCommand.BaseVerb,
                knownCommand.PastTenseVerb,
                commandArguments.Arguments,
                cancellationToken);
        }

        return await ResourceCommandHelper.ExecuteGenericCommandAsync(
            result.Connection!,
            _interactionService,
            _logger,
            resourceName,
            commandName,
            commandArguments.Arguments,
            cancellationToken);
    }

    private static (JsonElement? Arguments, string? ParseError) ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return (null, null);
        }

        try
        {
            // Command arguments JSON is expected to be an object, for example: { "selector": "#submit" }.
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, ResourceCommandStrings.CommandArgumentsJsonInvalidObject);
            }

            return (document.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return (null, string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsJsonInvalid, ex.Message));
        }
    }
}
