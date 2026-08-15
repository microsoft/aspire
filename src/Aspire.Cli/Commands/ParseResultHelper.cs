// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Aspire.Cli.Resources;

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
        // Use reference identity defensively so distinct token occurrences with the same value
        // and type can never cause each other to be excluded.
        var excludedTokens = new HashSet<Token>(ReferenceEqualityComparer.Instance);
        var excludedOptionNames = new HashSet<string>(StringComparer.Ordinal);

        System.CommandLine.Parsing.CommandResult? commandResult = parseResult.CommandResult;
        while (commandResult is not null)
        {
            excludedTokens.Add(commandResult.IdentifierToken);
            commandResult = commandResult.Parent as System.CommandLine.Parsing.CommandResult;
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

        var forwardedTokens = new List<string>(parseResult.Tokens.Count);
        var afterDoubleDash = false;
        foreach (var token in parseResult.Tokens)
        {
            if (token.Type == TokenType.DoubleDash)
            {
                forwardedTokens.Add(token.Value);
                afterDoubleDash = true;
                continue;
            }

            if (afterDoubleDash)
            {
                forwardedTokens.Add(token.Value);
                continue;
            }

            if (excludedTokens.Contains(token) ||
                token.Type == TokenType.Option && excludedOptionNames.Contains(token.Value))
            {
                continue;
            }

            forwardedTokens.Add(token.Value);
        }

        return forwardedTokens;
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
