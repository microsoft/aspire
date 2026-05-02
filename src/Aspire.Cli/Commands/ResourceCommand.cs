// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
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

    private static readonly Argument<string[]> s_commandArgumentsArgument = new("arguments")
    {
        Description = ResourceCommandStrings.CommandArgumentsArgumentDescription,
        Arity = ArgumentArity.ZeroOrMore
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

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
        Arguments.Add(s_commandArgumentsArgument);
        Options.Add(s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var commandName = parseResult.GetValue(s_commandArgument)!;
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var capturedArguments = parseResult.GetValue(s_commandArgumentsArgument) ?? [];

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

        JsonElement? commandArguments = null;
        if (capturedArguments.Length > 0)
        {
            var parsedArguments = await CreateArgumentsFromCapturedArgumentsAsync(
                result.Connection!,
                resourceName,
                commandName,
                capturedArguments,
                cancellationToken).ConfigureAwait(false);

            if (parsedArguments.ParseError is { } capturedArgumentsError)
            {
                _interactionService.DisplayError(capturedArgumentsError);
                return ExitCodeConstants.InvalidCommand;
            }

            commandArguments = parsedArguments.Arguments;
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
                commandArguments,
                cancellationToken);
        }

        return await ResourceCommandHelper.ExecuteGenericCommandAsync(
            result.Connection!,
            _interactionService,
            _logger,
            resourceName,
            commandName,
            commandArguments,
            cancellationToken);
    }

    private static async Task<(JsonElement? Arguments, string? ParseError)> CreateArgumentsFromCapturedArgumentsAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        string commandName,
        string[] capturedArguments,
        CancellationToken cancellationToken)
    {
        var firstArgument = capturedArguments[0].TrimStart();
        if (capturedArguments.Length == 1 && firstArgument.StartsWith('{'))
        {
            return ParseArguments(capturedArguments[0]);
        }

        var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
        var resolvedResources = ResourceSnapshotMapper.ResolveResources(resourceName, snapshots);
        var resource = resolvedResources.Count > 0 ? resolvedResources[0] : null;
        var command = resource?.Commands.FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparisons.CommandName));
        if (command?.ArgumentInputs.Length is not > 0)
        {
            return (null, string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsNoMetadata, commandName, resourceName));
        }

        return CreateArgumentsFromInputs(commandName, command.ArgumentInputs, capturedArguments);
    }

    private static (JsonElement? Arguments, string? ParseError) CreateArgumentsFromInputs(
        string commandName,
        ResourceSnapshotCommandArgument[] inputs,
        string[] capturedArguments)
    {
        var inputsByName = inputs.ToDictionary(i => i.Name, StringComparers.InteractionInputName);
        var hasNamedArguments = capturedArguments.Any(argument => TryGetNamedArgument(argument, inputsByName, out _, out _));
        if (hasNamedArguments)
        {
            return CreateArgumentsFromNamedInputs(inputs, inputsByName, capturedArguments);
        }

        if (capturedArguments.Length > inputs.Length)
        {
            return (null, string.Format(
                CultureInfo.CurrentCulture,
                ResourceCommandStrings.CommandArgumentsTooMany,
                commandName,
                inputs.Length,
                string.Join(", ", inputs.Select(i => i.Name))));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            for (var i = 0; i < capturedArguments.Length; i++)
            {
                var input = inputs[i];
                if (WriteArgumentValue(writer, input, capturedArguments[i]) is { } error)
                {
                    return (null, error);
                }
            }

            writer.WriteEndObject();
        }

        var providedInputs = inputs.Take(capturedArguments.Length).Select(i => i.Name).ToHashSet(StringComparers.InteractionInputName);
        if (FindMissingRequiredInput(inputs, providedInputs) is { } missingRequiredInput)
        {
            return (null, string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsMissingRequired, missingRequiredInput.Name));
        }

        return ParseArgumentsObject(stream);
    }

    private static (JsonElement? Arguments, string? ParseError) CreateArgumentsFromNamedInputs(
        ResourceSnapshotCommandArgument[] inputs,
        Dictionary<string, ResourceSnapshotCommandArgument> inputsByName,
        string[] capturedArguments)
    {
        var providedInputs = new HashSet<string>(StringComparers.InteractionInputName);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var capturedArgument in capturedArguments)
            {
                if (!TryGetNamedArgument(capturedArgument, inputsByName, out var input, out var value))
                {
                    var separatorIndex = capturedArgument.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        return (null, string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsInvalidNamed, capturedArgument));
                    }

                    return (null, string.Format(
                        CultureInfo.CurrentCulture,
                        ResourceCommandStrings.CommandArgumentsUnknownNamed,
                        capturedArgument[..separatorIndex],
                        string.Join(", ", inputs.Select(i => i.Name))));
                }

                if (WriteArgumentValue(writer, input, value) is { } error)
                {
                    return (null, error);
                }

                providedInputs.Add(input.Name);
            }

            writer.WriteEndObject();
        }

        if (FindMissingRequiredInput(inputs, providedInputs) is { } missingRequiredInput)
        {
            return (null, string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsMissingRequired, missingRequiredInput.Name));
        }

        return ParseArgumentsObject(stream);
    }

    private static bool TryGetNamedArgument(
        string argument,
        Dictionary<string, ResourceSnapshotCommandArgument> inputsByName,
        [NotNullWhen(true)] out ResourceSnapshotCommandArgument? input,
        [NotNullWhen(true)] out string? value)
    {
        input = null;
        value = null;

        var separatorIndex = argument.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return false;
        }

        if (!inputsByName.TryGetValue(argument[..separatorIndex], out input))
        {
            return false;
        }

        value = argument[(separatorIndex + 1)..];
        return true;
    }

    private static ResourceSnapshotCommandArgument? FindMissingRequiredInput(ResourceSnapshotCommandArgument[] inputs, HashSet<string> providedInputs)
    {
        return inputs.FirstOrDefault(i => i.Required && !providedInputs.Contains(i.Name));
    }

    private static string? WriteArgumentValue(Utf8JsonWriter writer, ResourceSnapshotCommandArgument input, string value)
    {
        if (input.MaxLength is { } maxLength && value.Length > maxLength)
        {
            return string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsInvalidTextLength, input.Name, maxLength);
        }

        switch (input.InputType)
        {
            case "Boolean":
                if (!bool.TryParse(value, out var boolValue))
                {
                    return string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsInvalidBoolean, value, input.Name);
                }

                writer.WriteBoolean(input.Name, boolValue);
                return null;

            case "Number":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue))
                {
                    return string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandArgumentsInvalidNumber, value, input.Name);
                }

                writer.WriteNumber(input.Name, numberValue);
                return null;

            case "Choice":
                if (input.Options is { Count: > 0 } options && !input.AllowCustomChoice && !options.ContainsKey(value))
                {
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        ResourceCommandStrings.CommandArgumentsInvalidChoice,
                        value,
                        input.Name,
                        string.Join(", ", options.Keys));
                }

                writer.WriteString(input.Name, value);
                return null;

            default:
                writer.WriteString(input.Name, value);
                return null;
        }
    }

    private static (JsonElement? Arguments, string? ParseError) ParseArgumentsObject(MemoryStream stream)
    {
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        return (document.RootElement.Clone(), null);
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
