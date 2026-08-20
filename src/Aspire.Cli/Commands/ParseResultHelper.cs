// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using CommandLineCommandResult = System.CommandLine.Parsing.CommandResult;

namespace Aspire.Cli.Commands;

/// <summary>
/// Contains projected CLI and AppHost arguments.
/// </summary>
internal sealed class ForwardedArguments
{
    private readonly List<string> _tokens;

    /// <summary>
    /// Initializes projected arguments with the insertion boundary for CLI options.
    /// </summary>
    internal ForwardedArguments(List<string> tokens, int optionCount)
    {
        _tokens = tokens;
        OptionCount = optionCount;
    }

    /// <summary>
    /// Gets the projected argument tokens.
    /// </summary>
    internal IReadOnlyList<string> Tokens => _tokens;

    /// <summary>
    /// Gets the insertion boundary before AppHost arguments.
    /// </summary>
    internal int OptionCount { get; private set; }

    /// <summary>
    /// Inserts CLI-owned tokens before AppHost arguments.
    /// </summary>
    internal void InsertCliOption(params ReadOnlySpan<string> tokens)
    {
        _tokens.InsertRange(OptionCount, tokens);
        OptionCount += tokens.Length;
    }
}

/// <summary>
/// Helpers for inspecting a <see cref="ParseResult"/> after parsing.
/// </summary>
internal static class ParseResultHelper
{
    private const string DoubleDashSeparator = "--";

    /// <summary>
    /// Projects explicitly supplied arguments while excluding options handled by the caller.
    /// </summary>
    internal static ForwardedArguments GetForwardedArguments(
        ParseResult parseResult,
        params Option[] excludedOptions)
    {
        var (excludedTokens, excludedOptionNames) = GetForwardingExclusions(parseResult, excludedOptions);

        // Command arguments (for example the package name in `aspire add <package>`) belong to the
        // CLI command being projected rather than to the child command line, so they stay excluded
        // from the owner map here.
        var optionValueOwners = GetValueOwners(parseResult.RootCommandResult, includeArgumentResults: false);
        var forwardedTokens = new List<string>(parseResult.Tokens.Count);
        int? optionCount = null;
        Token? lastForwardedToken = null;

        foreach (var token in parseResult.Tokens)
        {
            if (token.Type == TokenType.DoubleDash)
            {
                optionCount = forwardedTokens.Count;
                break;
            }

            var hasOptionValueOwner = optionValueOwners.TryGetValue(token, out var ownerResult);

            // The owner map is built without ArgumentResult entries above, so every owner here is
            // an OptionResult; the cast keeps that guarantee explicit rather than assumed.
            var optionResult = ownerResult as OptionResult;
            if (excludedTokens.Contains(token) ||
                (token.Type == TokenType.Option && excludedOptionNames.Contains(token.Value)))
            {
                continue;
            }

            if (token.Type != TokenType.Option &&
                !hasOptionValueOwner)
            {
                continue;
            }

            AddForwardedToken(token, optionResult, forwardedTokens, ref lastForwardedToken);
        }

        var finalOptionCount = optionCount ?? forwardedTokens.Count;

        if (parseResult.UnmatchedTokens.Count > 0)
        {
            forwardedTokens.Add(DoubleDashSeparator);
            forwardedTokens.AddRange(parseResult.UnmatchedTokens);
        }

        return new ForwardedArguments(forwardedTokens, finalOptionCount);
    }

    /// <summary>
    /// Renders the arguments of <paramref name="parseResult"/> in a form that is safe to write to
    /// logs: tokens the CLI itself owns are preserved and every token that would be forwarded to
    /// the AppHost is replaced with <see cref="AppHostArgumentRedactor.RedactedToken"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="AppHostArgumentRedactor"/> keys off the literal <c>--</c> separator, which is
    /// correct for a projected child command line (<see cref="GetForwardedArguments"/> always
    /// emits one) but wrong for raw user input: <c>run</c> and <c>start</c> forward unmatched
    /// tokens even when no separator was typed, so
    /// <code>
    /// aspire run --ApiKey sk-live-...
    /// </code>
    /// reaches the AppHost as <c>--ApiKey sk-live-...</c> with nothing to key off. The boundary is
    /// therefore derived from the parse tree instead: what the CLI did not claim is AppHost input.
    /// </remarks>
    internal static string GetLoggableArguments(ParseResult parseResult)
    {
        var cliOwnedTokens = GetCliOwnedTokens(parseResult);
        var loggableTokens = new List<string>(parseResult.Tokens.Count);
        var isAfterSeparator = false;

        foreach (var token in parseResult.Tokens)
        {
            // System.CommandLine synthesizes a leading token holding the root command name when the
            // first argument is not that name, so `aspire run` tokenizes as ["aspire", "run"] even
            // though the caller only passed ["run"]. Callers render the executable name themselves.
            if (ReferenceEquals(token, parseResult.RootCommandResult.IdentifierToken))
            {
                continue;
            }

            if (token.Type == TokenType.DoubleDash)
            {
                // The separator carries no user data and marks where AppHost input begins. Keeping
                // it readable preserves the boundary that makes the redacted tail interpretable.
                isAfterSeparator = true;
                loggableTokens.Add(DoubleDashSeparator);
                continue;
            }

            // Ownership must be checked by reference, not by value: the same raw string can appear
            // on both sides of the boundary (`run --apphost same-value -- same-value`) and only the
            // occurrence the parser bound to a CLI symbol is safe to log.
            loggableTokens.Add(!isAfterSeparator && cliOwnedTokens.Contains(token)
                ? token.Value
                : AppHostArgumentRedactor.RedactedToken);
        }

        return string.Join(' ', loggableTokens);
    }

    /// <summary>
    /// Collects the tokens the CLI itself consumed. Everything else in
    /// <see cref="ParseResult.Tokens"/> is AppHost input.
    /// </summary>
    private static HashSet<Token> GetCliOwnedTokens(ParseResult parseResult)
    {
        var ownedTokens = new HashSet<Token>(ReferenceEqualityComparer.Instance);

        CommandLineCommandResult? commandResult = parseResult.CommandResult;
        while (commandResult is not null)
        {
            ownedTokens.Add(commandResult.IdentifierToken);
            commandResult = commandResult.Parent as CommandLineCommandResult;
        }

        foreach (var token in parseResult.Tokens)
        {
            // The tokenizer assigns TokenType.Option only to arguments that matched a known option
            // (including the `--name` half of `--name=value`); an unrecognized `--secret` stays a
            // TokenType.Argument token and so is never treated as CLI-owned here.
            if (token.Type == TokenType.Option)
            {
                ownedTokens.Add(token);
            }
        }

        // Include command argument values (`aspire add <package>`) so ordinary invocations stay
        // readable in logs; they are consumed by the CLI command and never forwarded.
        foreach (var (token, _) in GetValueOwners(parseResult.RootCommandResult, includeArgumentResults: true))
        {
            ownedTokens.Add(token);
        }

        return ownedTokens;
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

    /// <summary>
    /// Maps every value token in the parse tree to the symbol result that consumed it.
    /// </summary>
    /// <param name="commandResult">The command result whose subtree is walked.</param>
    /// <param name="includeArgumentResults">
    /// Whether tokens consumed by command arguments are included. Forwarding needs option values
    /// only, while log redaction also needs command arguments so they stay readable.
    /// </param>
    private static Dictionary<Token, SymbolResult> GetValueOwners(
        CommandLineCommandResult commandResult,
        bool includeArgumentResults)
    {
        var owners = new Dictionary<Token, SymbolResult>(ReferenceEqualityComparer.Instance);
        AddValueOwners(commandResult, includeArgumentResults, owners);

        return owners;

        static void AddValueOwners(
            CommandLineCommandResult currentCommandResult,
            bool includeArguments,
            Dictionary<Token, SymbolResult> currentOwners)
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
                            // TryAdd rather than Add: a token reachable from two results must not
                            // throw, because this map is also built on the logging path.
                            currentOwners.TryAdd(token, optionResult);
                        }
                        break;
                    case ArgumentResult argumentResult when includeArguments:
                        foreach (var token in argumentResult.Tokens)
                        {
                            currentOwners.TryAdd(token, argumentResult);
                        }
                        break;
                    case CommandLineCommandResult childCommandResult:
                        AddValueOwners(childCommandResult, includeArguments, currentOwners);
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
        var emittedValue = optionResult is { Tokens.Count: 1 } &&
                           !optionResult.Errors.Any() &&
                           optionResult.Option.ValueType.IsAssignableTo(typeof(FileSystemInfo)) &&
                           optionResult.GetValueOrDefault<FileSystemInfo?>() is { } fileSystemInfo
            ? fileSystemInfo.FullName
            : token.Value;

        // System.CommandLine splits an equals-form option into owned identifier and value tokens:
        //   --values=--flag  ->  "--values", "--flag"
        // Re-emitting that as `--values --flag` can make a child parse `--flag` as an option
        // instead of a value. Keep equals syntax for the first option-shaped value, after typed
        // normalization so a FileSystemInfo value such as `--` remains an absolute path.
        if (token.Value.StartsWith('-')
            && optionResult is { IdentifierToken: { } identifierToken }
            && ReferenceEquals(lastForwardedToken, identifierToken))
        {
            forwardedTokens[^1] = $"{identifierToken.Value}={emittedValue}";
        }
        else
        {
            forwardedTokens.Add(emittedValue);
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
