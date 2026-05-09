// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
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
        Description = ResourceCommandStrings.CommandResourceArgumentDescription,
        Arity = ArgumentArity.ExactlyOne,
        DefaultValueFactory = _ => string.Empty
    };

    private static readonly Argument<string> s_commandArgument = new("command")
    {
        Description = ResourceCommandStrings.CommandNameArgumentDescription,
        Arity = ArgumentArity.ExactlyOne,
        DefaultValueFactory = _ => string.Empty
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    /// <summary>
    /// Well-known commands with their display metadata.
    /// The command name is used directly (no mapping needed since the user-facing names match the actual command names).
    /// </summary>
    private static readonly Dictionary<string, (string ProgressVerb, string BaseVerb, string PastTenseVerb)> s_wellKnownCommands = new(StringComparers.CommandName)
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
        Options.Add(new HelpOption { Action = new ResourceCommandHelpAction(this) });
        TreatUnmatchedTokensAsErrors = false;

        Validators.Add(result =>
        {
            var resourceName = result.GetValue(s_resourceArgument);
            if (string.IsNullOrEmpty(resourceName) || IsOptionLikeToken(resourceName))
            {
                result.AddError("The 'resource' argument is required.");
                return;
            }

            var commandName = result.GetValue(s_commandArgument);
            if (string.IsNullOrEmpty(commandName) || IsOptionLikeToken(commandName))
            {
                result.AddError("The 'command' argument is required.");
            }
        });
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var commandName = parseResult.GetValue(s_commandArgument)!;
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var capturedArguments = parseResult.UnmatchedTokens.ToArray();

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

        var connection = result.Connection!;
        var command = await GetCommandMetadataAsync(connection, resourceName, commandName, includeHidden: false, cancellationToken).ConfigureAwait(false);
        var commandArgumentsResult = CreateCommandArguments(command, capturedArguments);
        if (commandArgumentsResult.ErrorMessage is { } errorMessage)
        {
            _interactionService.DisplayError(errorMessage);
            return ExitCodeConstants.InvalidCommand;
        }

        var commandArguments = commandArgumentsResult.Arguments;

        // Map well-known friendly names (start/stop/restart) to their display metadata
        if (s_wellKnownCommands.TryGetValue(commandName, out var knownCommand))
        {
            return await ResourceCommandHelper.ExecuteResourceCommandAsync(
                connection,
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
            connection,
            _interactionService,
            _logger,
            resourceName,
            commandName,
            commandArguments,
            cancellationToken);
    }

    private static async Task<ResourceSnapshotCommand?> GetCommandMetadataAsync(IAppHostAuxiliaryBackchannel connection, string resourceName, string commandName, bool includeHidden, CancellationToken cancellationToken)
    {
        var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden, cancellationToken).ConfigureAwait(false);
        var resources = ResourceSnapshotMapper.ResolveResources(resourceName, snapshots);

        return resources
            .SelectMany(static resource => resource.Commands)
            .FirstOrDefault(command => string.Equals(command.Name, commandName, StringComparisons.CommandName));
    }

    private static (JsonNode? Arguments, string? ErrorMessage) CreateCommandArguments(ResourceSnapshotCommand? command, string[] capturedArguments)
    {
        capturedArguments = RemoveDelimiter(capturedArguments);

        if (capturedArguments.Length == 0)
        {
            if (command?.ArgumentInputs is { Length: > 0 } inputs)
            {
                return CreateCommandArguments(inputs, capturedArguments);
            }

            return (null, null);
        }

        if (command?.ArgumentInputs is not { Length: > 0 } argumentInputs)
        {
            // Without command metadata there are no options to give System.CommandLine, so do not infer any values.
            // Forward tokens as unknown names and let hosting-side validation reject them.
            return (CreateUnknownArguments(capturedArguments), null);
        }

        return CreateCommandArguments(argumentInputs, capturedArguments);
    }

    private static (JsonObject Arguments, string? ErrorMessage) CreateCommandArguments(ResourceSnapshotCommandArgument[] argumentInputs, string[] capturedArguments)
    {
        var arguments = new JsonObject();
        var options = new Dictionary<ResourceSnapshotCommandArgument, Option>();
        var parserCommand = new Command("resource-command")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        foreach (var argument in argumentInputs)
        {
            var option = CreateCommandArgumentOption(argument);
            options.Add(argument, option);
            parserCommand.Options.Add(option);
        }

        parserCommand.Validators.Add(result =>
        {
            var missingRequiredOptions = argumentInputs
                .Where(argument => argument.Required && string.IsNullOrEmpty(argument.Value) && result.GetResult(options[argument]) is not { Implicit: false })
                .Select(argument => $"--{ToKebabCase(argument.Name)}")
                .ToArray();

            if (missingRequiredOptions.Length == 1)
            {
                result.AddError($"Required option '{missingRequiredOptions[0]}' was not provided.");
            }
            else if (missingRequiredOptions.Length > 1)
            {
                result.AddError($"Required options were not provided: {string.Join(", ", missingRequiredOptions.Select(static optionName => $"'{optionName}'"))}.");
            }
        });

        // Parse the resource command tail with System.CommandLine as a second pass. The first pass parses Aspire CLI
        // options and leaves resource command tokens in ParseResult.UnmatchedTokens; this pass parses those remaining
        // tokens against options generated from ResourceSnapshotCommand.ArgumentInputs.
        var parseResult = parserCommand.Parse(capturedArguments);
        if (parseResult.Errors.Count > 0)
        {
            var unrecognizedCommandOptions = GroupUnrecognizedCommandOptions(parseResult.UnmatchedTokens);
            if (unrecognizedCommandOptions.Length > 0)
            {
                return (arguments, FormatUnrecognizedCommandOptions(unrecognizedCommandOptions));
            }

            return (arguments, string.Join(Environment.NewLine, parseResult.Errors.Select(static error => error.Message)));
        }

        foreach (var argument in argumentInputs)
        {
            var option = options[argument];
            if (parseResult.GetResult(option) is not { Implicit: false })
            {
                continue;
            }

            if (option is Option<bool> boolOption)
            {
                arguments[argument.Name] = parseResult.GetValue(boolOption).ToString().ToLowerInvariant();
            }
            else if (option is Option<double?> numberOption)
            {
                var value = parseResult.GetValue(numberOption);
                arguments[argument.Name] = value?.ToString(CultureInfo.InvariantCulture);
            }
            else if (option is Option<string?> stringOption)
            {
                arguments[argument.Name] = parseResult.GetValue(stringOption);
            }
        }

        foreach (var unmatchedToken in parseResult.UnmatchedTokens)
        {
            // Metadata-backed command inputs are options only. Any leftover token is forwarded as an unknown argument
            // name so hosting-side validation reports it instead of binding it positionally.
            // Example: `#name` becomes `{ "#name": null }`, which is not a declared command input.
            arguments[unmatchedToken] = null;
        }

        return (arguments, null);
    }

    private static string[] RemoveDelimiter(string[] capturedArguments)
    {
        if (capturedArguments.Length == 0 || capturedArguments[0] is not "--")
        {
            return capturedArguments;
        }

        return capturedArguments[1..];
    }

    private static JsonObject CreateUnknownArguments(string[] capturedArguments)
    {
        var arguments = new JsonObject();
        foreach (var token in GroupOptionLikeArguments(capturedArguments))
        {
            arguments[token] = null;
        }

        return arguments;
    }

    private static Option CreateCommandArgumentOption(ResourceSnapshotCommandArgument argument)
    {
        // Resource command input names are exposed as both exact-name and kebab-case System.CommandLine options:
        // - "timeoutMilliseconds" accepts "--timeoutMilliseconds" and "--timeout-milliseconds"
        // - "LogLevel" accepts "--LogLevel" and "--log-level"
        // - "url" accepts "--url"
        var optionName = ToKebabCase(argument.Name);
        Option option = (IsBooleanInput(argument), IsNumberInput(argument)) switch
        {
            (true, _) => new Option<bool>($"--{optionName}")
            {
                DefaultValueFactory = _ => bool.TryParse(argument.Value, out var value) && value
            },
            (_, true) => new Option<double?>($"--{optionName}")
            {
                Arity = ArgumentArity.ExactlyOne,
                AllowMultipleArgumentsPerToken = false,
                DefaultValueFactory = _ => double.TryParse(argument.Value, CultureInfo.InvariantCulture, out var value) ? value : null
            },
            _ => new Option<string?>($"--{optionName}")
            {
                Arity = ArgumentArity.ExactlyOne,
                AllowMultipleArgumentsPerToken = false,
                DefaultValueFactory = _ => argument.Value
            }
        };

        if (option is Option<bool> boolOption)
        {
            boolOption.Arity = ArgumentArity.ZeroOrOne;
            boolOption.AllowMultipleArgumentsPerToken = false;
        }

        option.Description = argument.Description ?? argument.Label;
        option.Required = argument.Required && string.IsNullOrEmpty(argument.Value);

        if (!argument.AllowCustomChoice && argument.Options is { Count: > 0 } options)
        {
            option.Validators.Add(result =>
            {
                var value = result.GetValueOrDefault<string?>();
                if (value is not null && !options.ContainsKey(value))
                {
                    result.AddError($"Option '--{optionName}' only accepts the following values: {string.Join(", ", options.Keys)}.");
                }
            });
        }

        var exactName = $"--{argument.Name}";
        if (!string.Equals(exactName, $"--{optionName}", StringComparison.Ordinal))
        {
            option.Aliases.Add(exactName);
        }

        return option;
    }

    private static bool IsBooleanInput(ResourceSnapshotCommandArgument argument)
    {
        return string.Equals(argument.InputType, "Boolean", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumberInput(ResourceSnapshotCommandArgument argument)
    {
        return string.Equals(argument.InputType, "Number", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOptionLikeToken(string value)
    {
        return value is not "--" && value.StartsWith("-", StringComparison.Ordinal);
    }

    private static string[] GroupOptionLikeArguments(IReadOnlyList<string> arguments)
    {
        var groupedArguments = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (IsOptionLikeToken(argument) &&
                !argument.Contains('=') &&
                i + 1 < arguments.Count &&
                !IsOptionLikeToken(arguments[i + 1]))
            {
                groupedArguments.Add($"{argument} {arguments[i + 1]}");
                i++;
            }
            else
            {
                groupedArguments.Add(argument);
            }
        }

        return [.. groupedArguments];
    }

    private static string[] GroupUnrecognizedCommandOptions(IReadOnlyList<string> arguments)
    {
        var groupedArguments = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (!IsOptionLikeToken(argument))
            {
                continue;
            }

            if (!argument.Contains('=') &&
                i + 1 < arguments.Count &&
                !IsOptionLikeToken(arguments[i + 1]))
            {
                groupedArguments.Add($"{argument} {arguments[i + 1]}");
                i++;
            }
            else
            {
                groupedArguments.Add(argument);
            }
        }

        return [.. groupedArguments];
    }

    private static string FormatUnrecognizedCommandOptions(string[] optionNames)
    {
        return optionNames.Length == 1
            ? $"Unrecognized command option '{optionNames[0]}'."
            : $"Unrecognized command options: {string.Join(", ", optionNames.Select(static optionName => $"'{optionName}'"))}.";
    }

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsUpper(ch))
            {
                if (i > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private sealed class ResourceCommandHelpAction(ResourceCommand command) : AsynchronousCommandLineAction
    {
        private readonly HelpAction _defaultHelpAction = new();

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var request = ResourceCommandHelpParser.Parse(parseResult, s_resourceArgument, s_commandArgument, s_appHostOption);
            if (request is null)
            {
                return _defaultHelpAction.Invoke(parseResult);
            }

            var result = await command._connectionResolver.ResolveConnectionAsync(
                request.AppHostProjectFile,
                SharedCommandStrings.ScanningForRunningAppHosts,
                string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, ResourceCommandStrings.SelectAppHostAction),
                SharedCommandStrings.AppHostNotRunning,
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return _defaultHelpAction.Invoke(parseResult);
            }

            var resourceCommand = await GetCommandMetadataAsync(result.Connection!, request.ResourceName, request.CommandName, includeHidden: false, cancellationToken).ConfigureAwait(false);
            if (resourceCommand is null)
            {
                return _defaultHelpAction.Invoke(parseResult);
            }

            WriteResourceCommandHelp(parseResult.InvocationConfiguration.Output, parseResult.CommandResult, request.ResourceName, resourceCommand);
            return ExitCodeConstants.Success;
        }

        private static void WriteResourceCommandHelp(TextWriter writer, CommandResult commandResult, string resourceName, ResourceSnapshotCommand command)
        {
            var cliOptionNames = GetCliOptionNames(commandResult);

            writer.WriteLine(command.Description is { Length: > 0 } ? command.Description : command.DisplayName ?? command.Name);
            writer.WriteLine();
            writer.WriteLine("Usage:");
            writer.WriteLine($"  aspire resource {resourceName} {command.Name} [command-options] [options]");
            writer.WriteLine($"  aspire resource {resourceName} {command.Name} -- [command-options]");

            if (command.ArgumentInputs.Length > 0)
            {
                writer.WriteLine();
                writer.WriteLine("Command options:");
                foreach (var argument in command.ArgumentInputs)
                {
                    var optionName = ToKebabCase(argument.Name);
                    writer.Write("  --");
                    writer.Write(optionName);
                    if (!IsBooleanInput(argument))
                    {
                        writer.Write(" <value>");
                    }

                    var description = GetArgumentDescription(argument);
                    if (description.Length > 0)
                    {
                        writer.Write("  ");
                        writer.Write(description);
                    }

                    if (cliOptionNames.Contains(optionName))
                    {
                        writer.Write(" Use `-- --");
                        writer.Write(optionName);
                        if (!IsBooleanInput(argument))
                        {
                            writer.Write(" <value>");
                        }

                        writer.Write("` to pass this command option.");
                    }

                    writer.WriteLine();
                }
            }

            writer.WriteLine();
            writer.WriteLine("Options:");
            foreach (var option in GetVisibleCliOptions(commandResult))
            {
                var description = option.Description ?? string.Empty;
                writer.Write("  ");
                writer.Write(GetOptionUsage(option));
                if (description.Length > 0)
                {
                    writer.Write("  ");
                    writer.Write(description);
                }

                writer.WriteLine();
            }
        }

        private static IEnumerable<Option> GetVisibleCliOptions(CommandResult commandResult)
        {
            var seenOptionNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var option in commandResult.Command.Options)
            {
                if (!option.Hidden && seenOptionNames.Add(option.Name))
                {
                    yield return option;
                }
            }

            var current = commandResult.Parent;
            while (current is CommandResult parentCommandResult)
            {
                foreach (var option in parentCommandResult.Command.Options)
                {
                    if (option.Recursive && !option.Hidden && seenOptionNames.Add(option.Name))
                    {
                        yield return option;
                    }
                }

                current = parentCommandResult.Parent;
            }
        }

        private static string GetOptionUsage(Option option)
        {
            var names = GetDisplayOptionNames(option);
            var suffix = IsBooleanOption(option) ? string.Empty : $" <{GetOptionValueName(option)}>";

            return $"{string.Join(", ", names)}{suffix}";
        }

        private static string[] GetDisplayOptionNames(Option option)
        {
            return option.Aliases
                .Prepend(option.Name)
                .Where(static name => name.StartsWith("-", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name.StartsWith("--", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsBooleanOption(Option option)
        {
            return option is Option<bool> or HelpOption;
        }

        private static string GetOptionValueName(Option option)
        {
            var name = GetDisplayOptionNames(option).Last(static name => name.StartsWith("--", StringComparison.Ordinal));
            return name[2..];
        }

        private static HashSet<string> GetCliOptionNames(CommandResult commandResult)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddOptionNames(commandResult.Command.Options, includeOnlyRecursive: false, names);

            var current = commandResult.Parent;
            while (current is CommandResult parentCommandResult)
            {
                AddOptionNames(parentCommandResult.Command.Options, includeOnlyRecursive: true, names);
                current = parentCommandResult.Parent;
            }

            return names;
        }

        private static void AddOptionNames(IEnumerable<Option> options, bool includeOnlyRecursive, HashSet<string> names)
        {
            foreach (var option in options)
            {
                if (includeOnlyRecursive && !option.Recursive)
                {
                    continue;
                }

                AddLongOptionName(option.Name, names);
                foreach (var alias in option.Aliases)
                {
                    AddLongOptionName(alias, names);
                }
            }
        }

        private static void AddLongOptionName(string optionName, HashSet<string> names)
        {
            if (optionName.StartsWith("--", StringComparison.Ordinal))
            {
                names.Add(optionName[2..]);
            }
            else if (!optionName.StartsWith("-", StringComparison.Ordinal))
            {
                names.Add(optionName);
            }
        }

        private static string GetArgumentDescription(ResourceSnapshotCommandArgument argument)
        {
            var parts = new List<string>();
            if (argument.Description is { Length: > 0 })
            {
                parts.Add(argument.Description);
            }
            else if (argument.Label is { Length: > 0 })
            {
                parts.Add(argument.Label);
            }

            if (argument.Required && string.IsNullOrEmpty(argument.Value))
            {
                parts.Add("Required.");
            }

            if (argument.Options is { Count: > 0 } options)
            {
                parts.Add($"Allowed values: {string.Join(", ", options.Keys)}.");
            }

            if (argument.Value is { Length: > 0 } value)
            {
                parts.Add($"Default: {value}.");
            }

            return string.Join(" ", parts);
        }
    }
}
