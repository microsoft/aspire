// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Helpers for inspecting a <see cref="ParseResult"/> after parsing.
/// </summary>
internal static class ParseResultHelper
{
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
