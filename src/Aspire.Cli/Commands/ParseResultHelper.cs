// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Aspire.Cli.Resources;
using CommandLineCommandResult = System.CommandLine.Parsing.CommandResult;

namespace Aspire.Cli.Commands;

/// <summary>
/// Controls where unmatched tokens are placed in projected arguments.
/// </summary>
internal enum UnmatchedTokenPlacement
{
    Preserve,
    AfterSeparator
}

/// <summary>
/// Contains projected CLI and AppHost arguments.
/// </summary>
internal sealed class ForwardedArguments
{
    /// <summary>
    /// Initializes projected arguments with the insertion boundary for CLI options.
    /// </summary>
    internal ForwardedArguments(List<string> tokens, int optionCount)
    {
        Tokens = tokens;
        OptionCount = optionCount;
    }

    /// <summary>
    /// Gets the projected argument tokens.
    /// </summary>
    internal List<string> Tokens { get; }

    /// <summary>
    /// Gets the insertion boundary before AppHost arguments.
    /// </summary>
    internal int OptionCount { get; private set; }

    /// <summary>
    /// Inserts CLI-owned tokens before AppHost arguments.
    /// </summary>
    internal void InsertCliOption(params ReadOnlySpan<string> tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            Tokens.Insert(OptionCount + i, tokens[i]);
        }

        OptionCount += tokens.Length;
    }
}

/// <summary>
/// Helpers for inspecting a <see cref="ParseResult"/> after parsing.
/// </summary>
internal static class ParseResultHelper
{
    /// <summary>
    /// Projects explicitly supplied arguments while excluding options handled by the caller.
    /// </summary>
    internal static ForwardedArguments GetForwardedArguments(
        ParseResult parseResult,
        UnmatchedTokenPlacement unmatchedTokenPlacement,
        params Option[] excludedOptions)
    {
        var (excludedTokens, excludedOptionNames) = GetForwardingExclusions(parseResult, excludedOptions);
        var optionValueOwners = GetOptionValueOwners(parseResult.RootCommandResult);
        var forwardedTokens = new List<string>(parseResult.Tokens.Count);
        var optionCount = -1;
        var afterSeparator = false;
        Token? lastForwardedToken = null;

        foreach (var token in parseResult.Tokens)
        {
            if (afterSeparator)
            {
                forwardedTokens.Add(token.Value);
                continue;
            }

            var hasOptionValueOwner = optionValueOwners.TryGetValue(token, out var optionResult);
            if (token.Type == TokenType.DoubleDash && !hasOptionValueOwner)
            {
                optionCount = forwardedTokens.Count;
                if (unmatchedTokenPlacement == UnmatchedTokenPlacement.AfterSeparator)
                {
                    break;
                }

                forwardedTokens.Add(token.Value);
                afterSeparator = true;
                continue;
            }

            if (excludedTokens.Contains(token) ||
                (token.Type == TokenType.Option && excludedOptionNames.Contains(token.Value)))
            {
                continue;
            }

            if (unmatchedTokenPlacement == UnmatchedTokenPlacement.AfterSeparator &&
                token.Type != TokenType.Option &&
                !hasOptionValueOwner)
            {
                continue;
            }

            AddForwardedToken(token, optionResult, forwardedTokens, ref lastForwardedToken);
        }

        if (optionCount < 0)
        {
            optionCount = forwardedTokens.Count;
        }

        if (unmatchedTokenPlacement == UnmatchedTokenPlacement.AfterSeparator &&
            parseResult.UnmatchedTokens.Count > 0)
        {
            forwardedTokens.Add("--");
            forwardedTokens.AddRange(parseResult.UnmatchedTokens);
        }

        return new ForwardedArguments(forwardedTokens, optionCount);
    }

    private static (HashSet<Token> Tokens, HashSet<string> OptionNames) GetForwardingExclusions(
        ParseResult parseResult,
        Option[] excludedOptions)
    {
        // A command can contain the same raw value on both sides of a separator:
        //   run --apphost value -- value
        // Reference identity excludes only the command or option occurrence that owns a token.
        var excludedTokens = new HashSet<Token>(ReferenceEqualityComparer.Instance);
        var excludedOptionNames = new HashSet<string>(StringComparer.Ordinal);

        CommandLineCommandResult? commandResult = parseResult.CommandResult;
        while (commandResult is not null)
        {
            excludedTokens.Add(commandResult.IdentifierToken);
            commandResult = commandResult.Parent as CommandLineCommandResult;
        }

        foreach (var option in excludedOptions)
        {
            excludedOptionNames.Add(option.Name);
            excludedOptionNames.UnionWith(option.Aliases);

            if (parseResult.GetResult(option) is { Implicit: false } optionResult)
            {
                excludedTokens.UnionWith(optionResult.Tokens);
            }
        }

        return (excludedTokens, excludedOptionNames);
    }

    private static Dictionary<Token, OptionResult> GetOptionValueOwners(CommandLineCommandResult commandResult)
    {
        var owners = new Dictionary<Token, OptionResult>(ReferenceEqualityComparer.Instance);
        AddOptionValueOwners(commandResult, owners);

        return owners;

        static void AddOptionValueOwners(
            CommandLineCommandResult currentCommandResult,
            Dictionary<Token, OptionResult> currentOwners)
        {
            foreach (var child in currentCommandResult.Children)
            {
                switch (child)
                {
                    case OptionResult optionResult:
                        // System.CommandLine represents both `--option value` and `--option=value`
                        // as an identifier token plus value tokens owned by the OptionResult.
                        // Unknown values have no owner, so token identity distinguishes equal raw values.
                        foreach (var token in optionResult.Tokens)
                        {
                            currentOwners.Add(token, optionResult);
                        }
                        break;
                    case CommandLineCommandResult childCommandResult:
                        AddOptionValueOwners(childCommandResult, currentOwners);
                        break;
                }
            }
        }
    }

    private static void AddForwardedToken(
        Token token,
        OptionResult? optionResult,
        List<string> forwardedTokens,
        ref Token? lastForwardedToken)
    {
        // A matched single-value option can own the literal `--`; only an unowned
        // TokenType.DoubleDash is a separator. Encode the value as:
        //   --capture-profile-output=--
        if (token.Value == "--"
            && optionResult is { Tokens.Count: 1, IdentifierToken: { } identifierToken }
            && ReferenceEquals(lastForwardedToken, identifierToken))
        {
            forwardedTokens[^1] = $"{identifierToken.Value}=--";
        }
        else if (optionResult is { Tokens.Count: 1 } &&
                 optionResult.Option.ValueType.IsAssignableTo(typeof(FileSystemInfo)) &&
                 optionResult.GetValueOrDefault<FileSystemInfo?>() is { } fileSystemInfo)
        {
            forwardedTokens.Add(fileSystemInfo.FullName);
        }
        else
        {
            forwardedTokens.Add(token.Value);
        }

        lastForwardedToken = token;
    }

    /// <summary>
    /// Checks unmatched tokens for options that differ only by case from a known option,
    /// and returns an error message if found. Returns null when no near-miss is detected.
    /// Only inspects tokens that appear before the "--" double-dash separator.
    /// </summary>
    internal static string? CheckForMiscasedOptions(Command command, ParseResult parseResult)
    {
        // Only relevant when TreatUnmatchedTokensAsErrors is false; when true,
        // System.CommandLine already rejects unrecognized options during parsing.
        if (command.TreatUnmatchedTokensAsErrors)
        {
            return null;
        }

        var unmatchedTokens = parseResult.UnmatchedTokens;
        if (unmatchedTokens.Count == 0)
        {
            return null;
        }

        // Only check tokens that appear before the "--" separator. Tokens after "--"
        // are explicit pass-through arguments (e.g. "aspire run -- --AppHost somepath").
        // We use a set of pre-"--" values so that a token appearing both before and
        // after "--" is still checked.
        var tokensBeforeDoubleDash = GetTokensBeforeDoubleDash(parseResult);

        // Collect all known option names (including aliases) from this command and
        // recursive parent options. The dictionary maps case-insensitive option name
        // to its canonical (correctly-cased) form.
        var knownOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectOptionNames(command.Options, includeOnlyRecursive: false, knownOptions);

        var current = parseResult.CommandResult.Parent;
        while (current is System.CommandLine.Parsing.CommandResult parentCommandResult)
        {
            CollectOptionNames(parentCommandResult.Command.Options, includeOnlyRecursive: true, knownOptions);
            current = parentCommandResult.Parent;
        }

        foreach (var token in unmatchedTokens)
        {
            if (!token.StartsWith('-'))
            {
                continue;
            }

            // When a "--" separator is present, only check tokens that appeared before it.
            // When there is no "--", tokensBeforeDoubleDash is null and all tokens are checked.
            if (tokensBeforeDoubleDash is not null && !tokensBeforeDoubleDash.Contains(token))
            {
                continue;
            }

            // Split off the "=value" suffix so that "--AppHost=somepath" is looked up
            // as "--AppHost" against the known "--apphost" key.
            var equalsIndex = token.IndexOf('=');
            var optionName = equalsIndex >= 0 ? token[..equalsIndex] : token;

            if (knownOptions.TryGetValue(optionName, out var correctName) &&
                !string.Equals(optionName, correctName, StringComparison.Ordinal))
            {
                return string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.UnrecognizedOptionDidYouMeanFormat, optionName, correctName);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the set of token values that appear before the "--" double-dash separator,
    /// or null if no "--" separator is present (meaning all tokens are candidates).
    /// </summary>
    private static HashSet<string>? GetTokensBeforeDoubleDash(ParseResult parseResult)
    {
        HashSet<string>? result = null;

        foreach (var token in parseResult.Tokens)
        {
            if (token.Type == System.CommandLine.Parsing.TokenType.DoubleDash)
            {
                // Found "--"; return what we collected (which may be empty).
                return result ?? [];
            }

            result ??= new HashSet<string>(StringComparer.Ordinal);
            result.Add(token.Value);
        }

        // No "--" found — return null to signal that all tokens are candidates.
        return null;
    }

    private static void CollectOptionNames(IList<Option> options, bool includeOnlyRecursive, Dictionary<string, string> knownOptions)
    {
        foreach (var option in options)
        {
            if (includeOnlyRecursive && !option.Recursive)
            {
                continue;
            }

            // TryAdd so the first (closest in hierarchy) definition wins.
            knownOptions.TryAdd(option.Name, option.Name);
            foreach (var alias in option.Aliases)
            {
                knownOptions.TryAdd(alias, alias);
            }
        }
    }
}
