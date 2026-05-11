// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Commands;

internal sealed record CommandInput(
    string Name,
    string? Label,
    string? Description,
    string InputType,
    bool Required,
    string? Value,
    Dictionary<string, string?>? Options,
    bool AllowCustomChoice,
    bool Disabled,
    bool ParseAsString = false,
    bool ConfigurationAliases = false);

internal sealed record CommandInputParseResult(JsonObject Arguments, string? ErrorMessage);

internal static class CommandInputParser
{
    public static CommandInputParseResult Parse(ResourceSnapshotCommandArgument[] inputs, string[] capturedArguments, bool loadArguments = false)
    {
        // --load-arguments accepts partial prompt state (for example --category=fruit).
        // Dynamic metadata may enable inputs or change choices, so only the AppHost can validate it.
        return Parse(inputs.Select(input => new CommandInput(
            input.Name,
            input.Label,
            input.Description,
            input.InputType,
            !loadArguments && input.DynamicLoading is null && input.Required,
            input.Value,
            input.Options,
            loadArguments || input.DynamicLoading is not null || input.AllowCustomChoice,
            !loadArguments && input.DynamicLoading is null && input.Disabled,
            ParseAsString: loadArguments || input.DynamicLoading is not null)), capturedArguments);
    }

    public static CommandInputParseResult Parse(PipelineInput[] inputs, string[] capturedArguments, bool requireMissingInputs)
    {
        return Parse(inputs.Select(input => new CommandInput(
            input.Name,
            input.Label,
            input.Description,
            input.InputType,
            requireMissingInputs && input.Required,
            input.Value,
            input.Options,
            input.AllowCustomChoice,
            input.Disabled,
            ConfigurationAliases: true)), capturedArguments);
    }

    public static string ToKebabCase(string value)
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

    public static bool IsBooleanInput(string inputType)
    {
        return string.Equals(inputType, "Boolean", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNumberInput(string inputType)
    {
        return string.Equals(inputType, "Number", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOptionLikeToken(string value)
    {
        return value is not "--" && value.StartsWith("-", StringComparison.Ordinal);
    }

    public static string[] RemoveDelimiter(string[] capturedArguments)
    {
        if (capturedArguments.Length == 0 || capturedArguments[0] is not "--")
        {
            return capturedArguments;
        }

        return capturedArguments[1..];
    }

    public static JsonObject CreateUnknownArguments(string[] capturedArguments)
    {
        var arguments = new JsonObject();
        foreach (var token in GroupOptionLikeArguments(capturedArguments))
        {
            arguments[token] = null;
        }

        return arguments;
    }

    public static string[] GroupUnrecognizedCommandOptions(IReadOnlyList<string> arguments)
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

    public static string FormatUnrecognizedCommandOptions(string[] optionNames)
    {
        return optionNames.Length == 1
            ? $"Unrecognized command option '{optionNames[0]}'."
            : $"Unrecognized command options: {string.Join(", ", optionNames.Select(static optionName => $"'{optionName}'"))}.";
    }

    private static CommandInputParseResult Parse(IEnumerable<CommandInput> commandInputs, string[] capturedArguments)
    {
        var inputArray = commandInputs.ToArray();
        var arguments = new JsonObject();
        var options = new Dictionary<CommandInput, Option>();
        var parserCommand = new Command("command-inputs")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        foreach (var input in inputArray)
        {
            var option = CreateCommandInputOption(input);
            options.Add(input, option);
            parserCommand.Options.Add(option);
        }

        parserCommand.Validators.Add(result =>
        {
            var missingRequiredOptions = inputArray
                .Where(input => input.Required && string.IsNullOrEmpty(input.Value) && result.GetResult(options[input]) is not { Implicit: false })
                .Select(input => $"--{ToKebabCase(input.Name)}")
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

        var parseResult = parserCommand.Parse(capturedArguments);
        if (parseResult.Errors.Count > 0)
        {
            var unrecognizedCommandOptions = GroupUnrecognizedCommandOptions(parseResult.UnmatchedTokens);
            if (unrecognizedCommandOptions.Length > 0)
            {
                return new(arguments, FormatUnrecognizedCommandOptions(unrecognizedCommandOptions));
            }

            return new(arguments, string.Join(Environment.NewLine, parseResult.Errors.Select(static error => error.Message)));
        }

        foreach (var input in inputArray)
        {
            var option = options[input];
            if (parseResult.GetResult(option) is not { Implicit: false })
            {
                continue;
            }

            if (option is Option<bool> boolOption)
            {
                arguments[input.Name] = parseResult.GetValue(boolOption).ToString().ToLowerInvariant();
            }
            else if (option is Option<double?> numberOption)
            {
                var value = parseResult.GetValue(numberOption);
                arguments[input.Name] = value?.ToString(CultureInfo.InvariantCulture);
            }
            else if (option is Option<string?> stringOption)
            {
                arguments[input.Name] = parseResult.GetValue(stringOption);
            }
        }

        foreach (var unmatchedToken in parseResult.UnmatchedTokens)
        {
            arguments[unmatchedToken] = null;
        }

        return new(arguments, null);
    }

    private static Option CreateCommandInputOption(CommandInput input)
    {
        var optionName = ToKebabCase(input.Name);
        Option option = (IsBooleanInput(input.InputType), IsNumberInput(input.InputType), input.ParseAsString) switch
        {
            (true, _, false) => new Option<bool>($"--{optionName}")
            {
                DefaultValueFactory = _ => bool.TryParse(input.Value, out var value) && value
            },
            (_, true, false) => new Option<double?>($"--{optionName}")
            {
                Arity = ArgumentArity.ExactlyOne,
                AllowMultipleArgumentsPerToken = false,
                DefaultValueFactory = _ => double.TryParse(input.Value, CultureInfo.InvariantCulture, out var value) ? value : null
            },
            _ => new Option<string?>($"--{optionName}")
            {
                Arity = ArgumentArity.ExactlyOne,
                AllowMultipleArgumentsPerToken = false,
                DefaultValueFactory = _ => input.Value
            }
        };

        if (option is Option<bool> boolOption)
        {
            boolOption.Arity = ArgumentArity.ZeroOrOne;
            boolOption.AllowMultipleArgumentsPerToken = false;
        }

        option.Description = input.Description ?? input.Label;
        option.Required = input.Required && string.IsNullOrEmpty(input.Value);

        if (!input.AllowCustomChoice && input.Options is { Count: > 0 } options)
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

        if (input.Disabled)
        {
            option.Validators.Add(result =>
            {
                if (result is { Implicit: false })
                {
                    result.AddError($"Option '--{optionName}' is disabled.");
                }
            });
        }

        AddAliasIfDifferent(option, $"--{input.Name}", optionName);
        if (input.ConfigurationAliases)
        {
            AddAliasIfDifferent(option, $"--Parameters:{input.Name}", optionName);
            AddAliasIfDifferent(option, $"--ConnectionStrings:{input.Name}", optionName);
        }

        return option;
    }

    private static void AddAliasIfDifferent(Option option, string alias, string optionName)
    {
        if (!string.Equals(alias, $"--{optionName}", StringComparison.Ordinal))
        {
            option.Aliases.Add(alias);
        }
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
}
