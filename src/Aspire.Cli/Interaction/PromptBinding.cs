// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli.Interaction;

/// <summary>
/// Binds a CLI option to an interactive prompt.
/// When a prompt method receives this, it first checks the parse result for an explicitly provided
/// option value before prompting interactively. In non-interactive mode, it uses the default value
/// or displays an actionable error naming the required option.
/// </summary>
internal sealed class PromptBinding<T>
{
    private readonly Func<ParseResult, (bool WasProvided, T? Value)> _resolver;
    private readonly ParseResult? _parseResult;

    internal PromptBinding(
        ParseResult? parseResult,
        string symbolDisplayName,
        Func<ParseResult, (bool WasProvided, T? Value)> resolver,
        T? defaultValue,
        bool hasExplicitDefault)
        : this(parseResult, symbolDisplayName, resolver, defaultValue, hasExplicitDefault, hasExplicitDefault ? defaultValue : default)
    {
    }

    internal PromptBinding(
        ParseResult? parseResult,
        string symbolDisplayName,
        Func<ParseResult, (bool WasProvided, T? Value)> resolver,
        T? defaultValue,
        bool hasExplicitDefault,
        T? nonInteractiveDefaultValue)
    {
        _parseResult = parseResult;
        SymbolDisplayName = symbolDisplayName;
        _resolver = resolver;
        DefaultValue = defaultValue;
        HasExplicitDefault = hasExplicitDefault;
        NonInteractiveDefaultValue = nonInteractiveDefaultValue;
    }

    /// <summary>
    /// Gets the display name of the CLI option, formatted for error messages
    /// (e.g. "'--publisher'").
    /// </summary>
    public string SymbolDisplayName { get; }

    /// <summary>
    /// Gets the default value to use for interactive prompts when the symbol was not provided.
    /// </summary>
    public T? DefaultValue { get; }

    /// <summary>
    /// Gets the default value to use when non-interactive and the symbol was not provided.
    /// </summary>
    public T? NonInteractiveDefaultValue { get; }

    /// <summary>
    /// Gets whether a default value was explicitly specified when this binding was created.
    /// When <c>false</c>, prompt methods should throw in non-interactive mode instead of
    /// silently using <c>default(T)</c>.
    /// </summary>
    public bool HasExplicitDefault { get; }

    /// <summary>
    /// Resolves the value from the parse result. Returns whether the symbol was explicitly
    /// provided by the user and the resolved value.
    /// </summary>
    public (bool WasProvided, T? Value) Resolve()
    {
        if (_parseResult is null)
        {
            return (false, default);
        }

        return _resolver(_parseResult);
    }

    /// <summary>
    /// Creates a new <see cref="PromptBinding{T}"/> with the same resolver but a different default value.
    /// </summary>
    public PromptBinding<T> WithDefault(T? newDefault) =>
        new(_parseResult, SymbolDisplayName, _resolver, newDefault, hasExplicitDefault: true, nonInteractiveDefaultValue: newDefault);
}

/// <summary>
/// Factory methods for creating <see cref="PromptBinding{T}"/> instances.
/// </summary>
internal static class PromptBinding
{
    public static (bool WasProvided, T? OptionValue, T? DefaultValue) Resolve<T>(PromptBinding<T>? binding)
    {
        if (binding == null)
        {
            return default;
        }

        var (wasProvided, optionValue) = binding.Resolve();
        return (wasProvided, optionValue, binding.DefaultValue);
    }

    public static PromptBinding<T> Create<T>(ParseResult parseResult, Option<T> option) =>
        new(parseResult, FormatOptionName(option), BuildOptionResolver(option), default, hasExplicitDefault: false);

    public static PromptBinding<T> Create<T>(ParseResult parseResult, Option<T> option, T defaultValue) =>
        new(parseResult, FormatOptionName(option), BuildOptionResolver(option), defaultValue, hasExplicitDefault: true);

    /// <summary>
    /// Creates a <see cref="PromptBinding{T}"/> that uses <paramref name="interactiveDefault"/> as the
    /// default for interactive prompts but still requires the CLI option in non-interactive mode.
    /// </summary>
    public static PromptBinding<T> CreateWithInteractiveDefault<T>(ParseResult parseResult, Option<T> option, T interactiveDefault) =>
        new(parseResult, FormatOptionName(option), BuildOptionResolver(option), interactiveDefault, hasExplicitDefault: false);

    /// <summary>
    /// Creates a <see cref="PromptBinding{T}"/> with only a default value and no CLI symbol binding.
    /// Use when there is no corresponding CLI option or argument for the prompt.
    /// </summary>
    public static PromptBinding<T> CreateDefault<T>(T defaultValue) =>
        new(null, string.Empty, static _ => (false, default), defaultValue, hasExplicitDefault: true);

    /// <summary>
    /// Creates a <see cref="PromptBinding{T}"/> for a <c>bool?</c> option that resolves to the inverse of its value.
    /// This is useful for suppress/disable options that should negate a confirmation result.
    /// When not provided in non-interactive mode, defaults to <paramref name="defaultValue"/>.
    /// </summary>
    public static PromptBinding<bool> CreateInvertedBoolConfirm(ParseResult parseResult, Option<bool?> option, bool defaultValue) =>
        new(parseResult, FormatOptionName(option), BuildResolver<bool?, bool>(option, value => value != true), defaultValue, hasExplicitDefault: true);

    /// <summary>
    /// Creates a <see cref="PromptBinding{T}"/> for a <c>bool?</c> option that maps to a confirmation prompt.
    /// When the option is explicitly provided, the binding resolves to <c>true</c> only when the option value is <c>true</c>.
    /// When the option is not explicitly provided, <paramref name="defaultValue"/> is used as the confirmation default,
    /// including as the interactive prompt default when the user accepts the prompt by pressing Enter.
    /// </summary>
    /// <param name="parseResult">The parse result used to determine whether <paramref name="option"/> was explicitly provided.</param>
    /// <param name="option">The nullable Boolean option to bind to the confirmation prompt.</param>
    /// <param name="defaultValue">The default confirmation value to use when <paramref name="option"/> was not explicitly provided.</param>
    /// <returns>
    /// A <see cref="PromptBinding{T}"/> that resolves the explicitly provided <c>bool?</c> option to a <see cref="bool"/>,
    /// where <c>true</c> maps to <c>true</c> and any other value maps to <c>false</c>, and otherwise exposes
    /// <paramref name="defaultValue"/> as the prompt default.
    /// </returns>
    public static PromptBinding<bool> CreateBoolConfirm(ParseResult parseResult, Option<bool?> option, bool defaultValue) =>
        CreateBoolConfirm(parseResult, option, interactiveDefault: defaultValue, nonInteractiveDefault: defaultValue);

    /// <summary>
    /// Creates a <see cref="PromptBinding{T}"/> for a <c>bool?</c> option that maps to a confirmation prompt.
    /// When the option is explicitly provided, the binding resolves to <c>true</c> only when the option value is <c>true</c>.
    /// When the option is not explicitly provided, <paramref name="interactiveDefault"/> is used as the confirmation prompt default,
    /// and <paramref name="nonInteractiveDefault"/> is used when interactive input is not available.
    /// </summary>
    /// <param name="parseResult">The parse result used to determine whether <paramref name="option"/> was explicitly provided.</param>
    /// <param name="option">The nullable Boolean option to bind to the confirmation prompt.</param>
    /// <param name="interactiveDefault">The default confirmation value to use for the interactive prompt.</param>
    /// <param name="nonInteractiveDefault">The default confirmation value to use when interactive input is not available.</param>
    /// <returns>
    /// A <see cref="PromptBinding{T}"/> that resolves the explicitly provided <c>bool?</c> option to a <see cref="bool"/>,
    /// where <c>true</c> maps to <c>true</c> and any other value maps to <c>false</c>, and otherwise exposes
    /// <paramref name="interactiveDefault"/> as the prompt default.
    /// </returns>
    public static PromptBinding<bool> CreateBoolConfirm(ParseResult parseResult, Option<bool?> option, bool interactiveDefault, bool nonInteractiveDefault) =>
        new(parseResult, FormatOptionName(option), BuildResolver<bool?, bool>(option, value => value == true), interactiveDefault, hasExplicitDefault: true, nonInteractiveDefaultValue: nonInteractiveDefault);

    private static string FormatOptionName<T>(Option<T> option) => $"'{option.Name}'";

    private static Func<ParseResult, (bool, TResult?)> BuildResolver<TOption, TResult>(
        Option<TOption> option, Func<TOption?, TResult?> transform) =>
        parseResult =>
        {
            var result = parseResult.GetResult(option);
            if (result is not null && !result.Implicit)
            {
                return (true, transform(parseResult.GetValue(option)));
            }

            return (false, default);
        };

    private static Func<ParseResult, (bool, T?)> BuildOptionResolver<T>(Option<T> option) =>
        BuildResolver<T, T>(option, static value => value);
}
