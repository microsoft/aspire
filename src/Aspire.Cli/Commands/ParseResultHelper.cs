// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Aspire.Cli.Resources;
using CommandLineCommandResult = System.CommandLine.Parsing.CommandResult;

namespace Aspire.Cli.Commands;

/// <summary>
/// Helpers for inspecting a <see cref="ParseResult"/> after parsing.
/// </summary>
internal static class ParseResultHelper
{
    /// <summary>
    /// Returns explicitly supplied tokens in their original order, excluding command tokens and tokens owned by the supplied options.
    /// </summary>
    /// <remarks>
    /// Forwarding is opt-out. Callers must exclude options transported separately or used to control delegation.
    /// </remarks>
    /// <param name="parseResult">The parse result containing the original tokens.</param>
    /// <param name="excludedOptions">The options whose identifiers and values should not be forwarded.</param>
    /// <returns>The forwarded tokens. All tokens following <c>--</c> are preserved.</returns>
    internal static List<string> GetForwardedTokens(ParseResult parseResult, params Option[] excludedOptions)
    {
        var (excludedTokens, excludedOptionNames) = GetForwardingExclusions(parseResult, excludedOptions);
        var optionValueOwners = GetOptionValueOwners(parseResult.RootCommandResult);

        var forwardedTokens = new List<string>(parseResult.Tokens.Count);
        var afterDoubleDash = false;
        Token? lastForwardedToken = null;
        foreach (var token in parseResult.Tokens)
        {
            if (token.Type == TokenType.DoubleDash)
            {
                forwardedTokens.Add(token.Value);
                lastForwardedToken = token;
                afterDoubleDash = true;
                continue;
            }

            if (afterDoubleDash)
            {
                forwardedTokens.Add(token.Value);
                lastForwardedToken = token;
                continue;
            }

            if (excludedTokens.Contains(token) ||
                (token.Type == TokenType.Option && excludedOptionNames.Contains(token.Value)))
            {
                continue;
            }

            AddForwardedToken(token, optionValueOwners, forwardedTokens, ref lastForwardedToken);
        }

        return forwardedTokens;
    }

    /// <summary>
    /// Returns explicitly supplied option tokens before <c>--</c>, followed by unmatched tokens after a separator.
    /// </summary>
    /// <remarks>
    /// This preserves the historical <c>start</c> delegation behavior while allowing newly registered options to flow automatically.
    /// </remarks>
    internal static List<string> GetForwardedOptionTokensWithUnmatchedTokensAfterDoubleDash(
        ParseResult parseResult,
        params Option[] excludedOptions)
    {
        var (excludedTokens, excludedOptionNames) = GetForwardingExclusions(parseResult, excludedOptions);
        var optionValueOwners = GetOptionValueOwners(parseResult.RootCommandResult);
        var forwardedTokens = new List<string>(parseResult.Tokens.Count);
        Token? lastForwardedToken = null;

        // System.CommandLine tokenizes both `--option value` and `--option=value` into
        // option/value Token instances. OptionResult.Tokens owns the value instances,
        // while unknown values do not belong to an OptionResult. Reference identity
        // therefore distinguishes duplicate matched and unmatched values reliably.
        foreach (var token in parseResult.Tokens)
        {
            if (token.Type == TokenType.DoubleDash)
            {
                break;
            }

            if (excludedTokens.Contains(token) ||
                (token.Type == TokenType.Option && excludedOptionNames.Contains(token.Value)))
            {
                continue;
            }

            if (token.Type == TokenType.Option || optionValueOwners.ContainsKey(token))
            {
                AddForwardedToken(token, optionValueOwners, forwardedTokens, ref lastForwardedToken);
            }
        }

        if (parseResult.UnmatchedTokens.Count > 0)
        {
            forwardedTokens.Add("--");
            forwardedTokens.AddRange(parseResult.UnmatchedTokens);
        }

        return forwardedTokens;
    }

    /// <summary>
    /// Replaces an explicitly forwarded option value before the AppHost argument separator.
    /// </summary>
    internal static void ReplaceForwardedOptionValue(List<string> forwardedTokens, Option option, string value)
    {
        var optionNames = new HashSet<string>(option.Aliases, StringComparer.Ordinal)
        {
            option.Name
        };
        var doubleDashIndex = forwardedTokens.IndexOf("--");
        var optionTokenCount = doubleDashIndex >= 0 ? doubleDashIndex : forwardedTokens.Count;

        for (var i = 0; i < optionTokenCount; i++)
        {
            if (optionNames.Contains(forwardedTokens[i]))
            {
                if (i + 1 < optionTokenCount)
                {
                    forwardedTokens[i + 1] = value;
                }

                return;
            }

            foreach (var optionName in optionNames)
            {
                if (forwardedTokens[i].StartsWith($"{optionName}=", StringComparison.Ordinal))
                {
                    forwardedTokens[i] = $"{optionName}={value}";
                    return;
                }
            }
        }
    }

    private static (HashSet<Token> Tokens, HashSet<string> OptionNames) GetForwardingExclusions(
        ParseResult parseResult,
        Option[] excludedOptions)
    {
        // Use reference identity defensively so distinct token occurrences with the same value
        // and type can never cause each other to be excluded.
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

            if (parseResult.GetResult(option) is not { Implicit: false } optionResult)
            {
                continue;
            }

            excludedTokens.UnionWith(optionResult.Tokens);
        }

        return (excludedTokens, excludedOptionNames);
    }

    private static Dictionary<Token, OptionResult> GetOptionValueOwners(CommandLineCommandResult commandResult)
    {
        var optionValueOwners = new Dictionary<Token, OptionResult>(ReferenceEqualityComparer.Instance);
        AddOptionValueOwners(commandResult, optionValueOwners);

        return optionValueOwners;

        static void AddOptionValueOwners(CommandLineCommandResult currentCommandResult, Dictionary<Token, OptionResult> owners)
        {
            foreach (var child in currentCommandResult.Children)
            {
                switch (child)
                {
                    case OptionResult optionResult:
                        foreach (var token in optionResult.Tokens)
                        {
                            owners.Add(token, optionResult);
                        }
                        break;
                    case CommandLineCommandResult childCommandResult:
                        AddOptionValueOwners(childCommandResult, owners);
                        break;
                }
            }
        }
    }

    private static void AddForwardedToken(
        Token token,
        IReadOnlyDictionary<Token, OptionResult> optionValueOwners,
        List<string> forwardedTokens,
        ref Token? lastForwardedToken)
    {
        // A matched single-value option can own the literal value "--", which is not a
        // separator in the original parse. DebugSessionOptions only transports strings,
        // so use equals syntax to keep that value distinct from a real separator:
        //   --capture-profile-output=--
        if (token.Value == "--"
            && optionValueOwners.TryGetValue(token, out var optionResult)
            && optionResult.Tokens.Count == 1
            && optionResult.IdentifierToken is { } identifierToken
            && ReferenceEquals(lastForwardedToken, identifierToken))
        {
            forwardedTokens[^1] = $"{identifierToken.Value}=--";
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
            if (token.Type == TokenType.DoubleDash)
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
