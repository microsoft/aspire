// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli.Commands;

/// <summary>
/// Pairs a visible option with a hidden legacy alias so that help text shows
/// only the new name while existing scripts that pass the old name continue
/// to work.
/// </summary>
internal sealed class OptionWithLegacy<T>
{
    /// <summary>
    /// Initializes a new option pair.
    /// </summary>
    /// <param name="optionName">The primary, visible option name (e.g. <c>--apphost</c>).</param>
    /// <param name="legacyName">The hidden backward-compatible name (e.g. <c>--project</c>).</param>
    /// <param name="description">The description shown in help text for the visible option.</param>
    internal OptionWithLegacy(string optionName, string legacyName, string description)
    {
        InnerOption = new(optionName) { Description = description };
        LegacyOption = new(legacyName) { Hidden = true };
    }

    /// <summary>
    /// Gets the visible option.
    /// </summary>
    internal Option<T> InnerOption { get; }

    /// <summary>
    /// Gets the hidden option kept for backward compatibility.
    /// </summary>
    internal Option<T> LegacyOption { get; }

    /// <summary>
    /// Gets the primary option name.
    /// </summary>
    internal string Name => InnerOption.Name;
}

/// <summary>
/// Extension methods for working with <see cref="OptionWithLegacy{T}"/>.
/// </summary>
internal static class OptionWithLegacyExtensions
{
    /// <summary>
    /// Registers both the visible and hidden options from an
    /// <see cref="OptionWithLegacy{T}"/> on the specified option list.
    /// </summary>
    internal static void Add<T>(this IList<Option> options, OptionWithLegacy<T> optionWithLegacy)
    {
        options.Add(optionWithLegacy.InnerOption);
        options.Add(optionWithLegacy.LegacyOption);
    }

    /// <summary>
    /// Resolves the value from a parse result, preferring the primary option
    /// and falling back to the legacy option.
    /// </summary>
    internal static T? GetValue<T>(this ParseResult parseResult, OptionWithLegacy<T> optionWithLegacy)
        => parseResult.GetValue(optionWithLegacy.InnerOption) ?? parseResult.GetValue(optionWithLegacy.LegacyOption);
}
