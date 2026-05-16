// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
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
    private readonly IAuxiliaryBackchannelMonitor _backchannelMonitor;
    private readonly IProjectLocator _projectLocator;
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
    private static readonly Option<bool> s_includeHiddenOption = new("--include-hidden")
    {
        Description = SharedCommandStrings.IncludeHiddenOptionDescription
    };

    /// <summary>
    /// Well-known commands with their display metadata.
    /// The command names are passed through unchanged; entries only customize progress, success, and error text.
    /// </summary>
    private static readonly Dictionary<string, (string ProgressVerb, string BaseVerb, string PastTenseVerb)> s_wellKnownCommands = new(StringComparers.CommandName)
    {
        ["start"] = ("Starting", "start", "started"),
        ["stop"] = ("Stopping", "stop", "stopped"),
        ["restart"] = ("Restarting", "restart", "restarted"),
        ["rebuild"] = ("Rebuilding", "rebuild", "rebuilt"),
        ["set-parameter"] = ("Setting parameter for", "set parameter for", "set"),
        ["delete-parameter"] = ("Deleting parameter for", "delete parameter for", "deleted"),
        ["parameter-set"] = ("Setting parameter for", "set parameter for", "set"),
        ["parameter-delete"] = ("Deleting parameter for", "delete parameter for", "deleted"),
    };

    private static readonly Dictionary<string, string> s_legacyCommandNameMap = new(StringComparers.CommandName)
    {
        ["parameter-set"] = "set-parameter",
        ["parameter-delete"] = "delete-parameter",
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
        _backchannelMonitor = backchannelMonitor;
        _projectLocator = projectLocator;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, projectLocator, executionContext, logger);
        _logger = logger;

        Arguments.Add(s_resourceArgument);
        Arguments.Add(s_commandArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_includeHiddenOption);
        Options.Add(new HelpOption { Action = new ResourceCommandHelpAction(this) });
        TreatUnmatchedTokensAsErrors = false;

        Validators.Add(result =>
        {
            var resourceName = result.GetValue(s_resourceArgument);
            if (string.IsNullOrEmpty(resourceName) || IsOptionLikeToken(resourceName))
            {
                result.AddError(string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.ArgumentRequired, s_resourceArgument.Name));
                return;
            }

            var commandName = result.GetValue(s_commandArgument);
            if (string.IsNullOrEmpty(commandName) || IsOptionLikeToken(commandName))
            {
                result.AddError(string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.ArgumentRequired, s_commandArgument.Name));
            }
        });
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var commandName = parseResult.GetValue(s_commandArgument)!;
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var includeHidden = parseResult.GetValue(s_includeHiddenOption);
        var capturedArguments = parseResult.UnmatchedTokens.ToArray();

        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, ResourceCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            return CommandResult.FromExitCode(AppHostConnectionResultHandler.DisplayFailureAsError(result, _interactionService, ExitCodeConstants.FailedToFindProject));
        }

        var connection = result.Connection!;
        var command = await GetCommandMetadataAsync(connection, resourceName, commandName, includeHidden, cancellationToken).ConfigureAwait(false);
        var commandArgumentsResult = CreateCommandArguments(command, capturedArguments);
        if (commandArgumentsResult.ErrorMessage is { } errorMessage)
        {
            return CommandResult.Failure(ExitCodeConstants.InvalidCommand, errorMessage);
        }

        var commandArguments = commandArgumentsResult.Arguments;

        // Use display metadata for well-known command names.
        if (s_wellKnownCommands.TryGetValue(commandName, out var knownCommand))
        {
            return CommandResult.FromExitCode(await ResourceCommandHelper.ExecuteResourceCommandAsync(
                connection,
                _interactionService,
                _logger,
                resourceName,
                commandName,
                knownCommand.ProgressVerb,
                knownCommand.BaseVerb,
                knownCommand.PastTenseVerb,
                commandArguments,
                cancellationToken));
        }

        return CommandResult.FromExitCode(await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            _interactionService,
            _logger,
            resourceName,
            commandName,
            commandArguments,
            cancellationToken));
    }

    private static async Task<ResourceSnapshotCommand?> GetCommandMetadataAsync(IAppHostAuxiliaryBackchannel connection, string resourceName, string commandName, bool includeHidden, CancellationToken cancellationToken)
    {
        var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden, cancellationToken).ConfigureAwait(false);
        var resources = ResourceSnapshotMapper.ResolveResources(resourceName, snapshots);
        var lookupCommandName = s_legacyCommandNameMap.GetValueOrDefault(commandName, commandName);

        return resources
            .SelectMany(static resource => resource.Commands)
            .FirstOrDefault(command => string.Equals(command.Name, lookupCommandName, StringComparisons.CommandName));
    }

    private static async Task<(string Name, string Description)[]> GetAvailableCommandMetadataAsync(IAppHostAuxiliaryBackchannel connection, string resourceName, bool includeHidden, CancellationToken cancellationToken)
    {
        var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden, cancellationToken).ConfigureAwait(false);
        var resources = ResourceSnapshotMapper.ResolveResources(resourceName, snapshots);

        return resources
            .SelectMany(static resource => resource.Commands)
            .Where(ResourceSnapshotMapper.IsCommandAvailableToApi)
            .GroupBy(static command => command.Name, StringComparers.CommandName)
            .OrderBy(static group => group.Key, StringComparers.CommandName)
            .Select(static group =>
            {
                var description = group
                    .Select(static command => command.Description ?? command.DisplayName)
                    .FirstOrDefault(static description => !string.IsNullOrEmpty(description));

                return (group.Key, description ?? string.Empty);
            })
            .ToArray();
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

        if (argument.Disabled)
        {
            option.Validators.Add(result =>
            {
                if (result is { Implicit: false })
                {
                    result.AddError($"Option '--{optionName}' is disabled.");
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
                var exitCode = _defaultHelpAction.Invoke(parseResult);
                if (TryGetResourceOnlyHelp(parseResult, out var resourceName))
                {
                    try
                    {
                        await WriteAvailableCommandsAsync(parseResult, resourceName, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        command._logger.LogDebug(ex, "Failed to augment resource help with available resource commands.");
                    }
                }

                return exitCode;
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

            var includeHidden = parseResult.GetValue(s_includeHiddenOption);
            var resourceCommand = await GetCommandMetadataAsync(result.Connection!, request.ResourceName, request.CommandName, includeHidden, cancellationToken).ConfigureAwait(false);
            if (resourceCommand is null)
            {
                return _defaultHelpAction.Invoke(parseResult);
            }

            WriteResourceCommandHelp(parseResult.InvocationConfiguration.Output, parseResult.CommandResult, request.ResourceName, resourceCommand);
            return ExitCodeConstants.Success;
        }

        private async Task WriteAvailableCommandsAsync(ParseResult parseResult, string resourceName, CancellationToken cancellationToken)
        {
            var connection = await ResolveConnectionForAvailableCommandsAsync(parseResult, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                return;
            }

            var includeHidden = parseResult.GetValue(s_includeHiddenOption);
            var commands = await GetAvailableCommandMetadataAsync(connection, resourceName, includeHidden, cancellationToken).ConfigureAwait(false);
            if (commands.Length == 0)
            {
                return;
            }

            GroupedHelpWriter.WriteTwoColumnSection(
                parseResult.InvocationConfiguration.Output,
                ResourceCommandStrings.AvailableResourceCommands,
                commands,
                maxWidth: 120,
                trailingBlankLine: false);
        }

        private async Task<IAppHostAuxiliaryBackchannel?> ResolveConnectionForAvailableCommandsAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var appHostProjectFile = parseResult.GetValue(s_appHostOption);
            if (appHostProjectFile is not null)
            {
                return await ResolveExplicitConnectionForAvailableCommandsAsync(appHostProjectFile, cancellationToken).ConfigureAwait(false);
            }

            var inScopeConnections = await command._interactionService.ShowStatusAsync(
                SharedCommandStrings.ScanningForRunningAppHosts,
                async () =>
                {
                    await command._backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
                    return command._backchannelMonitor.Connections.Where(static connection => connection.IsInScope).ToList();
                });

            return inScopeConnections.Count == 1 ? inScopeConnections[0] : null;
        }

        private async Task<IAppHostAuxiliaryBackchannel?> ResolveExplicitConnectionForAvailableCommandsAsync(FileInfo appHostProjectFile, CancellationToken cancellationToken)
        {
            FileInfo? selectedAppHostProjectFile = appHostProjectFile;

            if (Directory.Exists(appHostProjectFile.FullName))
            {
                var searchResult = await command._projectLocator.UseOrFindAppHostProjectFileAsync(
                    appHostProjectFile,
                    MultipleAppHostProjectsFoundBehavior.Throw,
                    createSettingsFile: false,
                    cancellationToken).ConfigureAwait(false);

                selectedAppHostProjectFile = searchResult.SelectedProjectFile;
            }
            else if (!appHostProjectFile.Exists)
            {
                return null;
            }

            if (selectedAppHostProjectFile is null)
            {
                return null;
            }

            var targetPath = Path.GetFullPath(selectedAppHostProjectFile.FullName);
            var matchingConnections = await command._interactionService.ShowStatusAsync(
                SharedCommandStrings.ScanningForRunningAppHosts,
                async () =>
                {
                    await command._backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
                    return command._backchannelMonitor.Connections
                        .Where(connection => IsMatchingAppHostPath(connection.AppHostInfo?.AppHostPath, targetPath))
                        .ToList();
                });

            return matchingConnections.Count == 1 ? matchingConnections[0] : null;
        }

        private static bool IsMatchingAppHostPath(string? appHostPath, string targetPath)
        {
            return !string.IsNullOrEmpty(appHostPath) &&
                string.Equals(Path.GetFullPath(appHostPath), targetPath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetResourceOnlyHelp(ParseResult parseResult, [NotNullWhen(true)] out string? resourceName)
        {
            // Resource-only help is `aspire resource <resource> --help`. Because the command argument has a default,
            // System.CommandLine can bind the next option token (or an option value like --apphost's path) as the
            // command. Treat those as "no command" so resource-scoped help can still show the resource's commands.
            var resourceArgumentResult = parseResult.GetResult(s_resourceArgument);
            resourceName = resourceArgumentResult?.Tokens.Count > 0 ? resourceArgumentResult.Tokens[0].Value : null;
            var commandArgumentResult = parseResult.GetResult(s_commandArgument);
            var commandName = commandArgumentResult?.Tokens.Count > 0 ? commandArgumentResult.Tokens[0].Value : null;
            var appHostOptionValue = GetOptionTokenValue(parseResult, s_appHostOption.InnerOption) ?? GetOptionTokenValue(parseResult, s_appHostOption.LegacyOption);

            var hasResourceName = !string.IsNullOrEmpty(resourceName) && !IsOptionLikeToken(resourceName);

            // The command slot is considered empty when it has no token, when it captured an option like --help,
            // or when it captured the value for --apphost/--project instead of an actual resource command name.
            var hasNoCommandName = string.IsNullOrEmpty(commandName) ||
                IsOptionLikeToken(commandName) ||
                string.Equals(commandName, appHostOptionValue, StringComparison.Ordinal);

            return hasResourceName && hasNoCommandName;
        }

        private static string? GetOptionTokenValue(ParseResult parseResult, Option<FileInfo?> option)
        {
            var result = parseResult.GetResult(option);
            return result?.Tokens.Count > 0 ? result.Tokens[0].Value : null;
        }

        private static void WriteResourceCommandHelp(TextWriter writer, System.CommandLine.Parsing.CommandResult commandResult, string resourceName, ResourceSnapshotCommand command)
        {
            var cliOptionNames = GetCliOptionNames(commandResult);

            writer.WriteLine(command.Description is { Length: > 0 } ? command.Description : command.DisplayName ?? command.Name);
            writer.WriteLine();
            GroupedHelpWriter.WriteUsage(
                writer,
                string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandSpecificHelpUsageSyntax, resourceName, command.Name));

            if (command.ArgumentInputs.Length > 0)
            {
                GroupedHelpWriter.WriteTwoColumnSection(
                    writer,
                    ResourceCommandStrings.CommandSpecificHelpCommandOptions,
                    command.ArgumentInputs.Select(argument => (GetCommandOptionLabel(argument), GetArgumentDescription(argument, cliOptionNames))),
                    maxWidth: 120);
            }

            GroupedHelpWriter.WriteTwoColumnSection(
                writer,
                HelpGroupStrings.Options,
                GetVisibleCliOptions(commandResult).Select(static option => (GroupedHelpWriter.FormatOptionLabel(option, includeValueName: true), option.Description ?? string.Empty)),
                maxWidth: 120,
                trailingBlankLine: false);
        }

        private static IEnumerable<Option> GetVisibleCliOptions(System.CommandLine.Parsing.CommandResult commandResult)
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
            while (current is System.CommandLine.Parsing.CommandResult parentCommandResult)
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

        private static HashSet<string> GetCliOptionNames(System.CommandLine.Parsing.CommandResult commandResult)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddOptionNames(commandResult.Command.Options, includeOnlyRecursive: false, names);

            var current = commandResult.Parent;
            while (current is System.CommandLine.Parsing.CommandResult parentCommandResult)
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

        private static string GetCommandOptionLabel(ResourceSnapshotCommandArgument argument)
        {
            var optionName = ToKebabCase(argument.Name);
            return IsBooleanInput(argument)
                ? $"--{optionName}"
                : $"--{optionName} <{ResourceCommandStrings.CommandSpecificHelpValuePlaceholder}>";
        }

        private static string GetArgumentDescription(ResourceSnapshotCommandArgument argument, HashSet<string> cliOptionNames)
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
                parts.Add(ResourceCommandStrings.CommandSpecificHelpRequired);
            }

            if (argument.Options is { Count: > 0 } options)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandSpecificHelpAllowedValues, string.Join(", ", options.Keys)));
            }

            if (argument.Value is { Length: > 0 } value)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandSpecificHelpDefaultValue, value));
            }

            var optionName = ToKebabCase(argument.Name);
            if (cliOptionNames.Contains(optionName))
            {
                var valuePlaceholder = IsBooleanInput(argument)
                    ? string.Empty
                    : $" <{ResourceCommandStrings.CommandSpecificHelpValuePlaceholder}>";
                parts.Add(string.Format(CultureInfo.CurrentCulture, ResourceCommandStrings.CommandSpecificHelpDelimiterHint, optionName, valuePlaceholder));
            }

            return string.Join(" ", parts);
        }
    }
}
