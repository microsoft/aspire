// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Validation and conversion of the names used in Traefik configuration.
/// </summary>
internal static partial class TraefikNames
{
    /// <summary>
    /// Checks a router, entry point, or resolver name. Names become segments of label keys such as
    /// <c>traefik.http.routers.&lt;name&gt;.rule</c>, so dots would split the key, and <c>@</c> is reserved for
    /// provider namespaces (<c>api@internal</c>).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the name can be used; otherwise, <see langword="false"/>.</returns>
    public static bool IsValidName(string name) => ValidNameRegex().IsMatch(name);

    /// <summary>
    /// Throws when a name passed to a public method is empty or isn't valid (see <see cref="IsValidName"/>).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="description">What the name is, for the message, for example <c>entry point name</c>.</param>
    /// <param name="parameterName">The parameter the name was passed in.</param>
    public static void ThrowIfInvalidName([NotNull] string? name, string description, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);

        if (!IsValidName(name))
        {
            throw new ArgumentException($"The {description} '{name}' is invalid. Use letters, digits, '-' and '_' only.", parameterName);
        }
    }

    /// <summary>
    /// Throws when a path passed to a public method is empty or doesn't start with <c>/</c>, which Traefik's path
    /// matchers and middleware options require.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="description">What the path is, for the message, for example <c>path</c> or <c>query</c>.</param>
    /// <param name="parameterName">The parameter the path was passed in.</param>
    public static void ThrowIfInvalidPath([NotNull] string? path, string description, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException($"The {description} '{path}' must start with '/'.", parameterName);
        }
    }

    /// <summary>
    /// Formats a boolean as Traefik's flags, labels, and options spell it: <c>true</c> or <c>false</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><c>true</c> or <c>false</c>.</returns>
    public static string ToLiteral(bool value) => value ? "true" : "false";

    /// <summary>
    /// Converts a name to an environment variable segment, for example <c>app-api</c> to <c>APP_API</c>.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The upper-case segment.</returns>
    public static string ToEnvironmentName(string name) => NonAlphanumericRegex().Replace(name, "_").ToUpperInvariant();

    /// <summary>
    /// Returns the text of an expression without value providers, such as <c>ReferenceExpression.Create($"web")</c>,
    /// or <see langword="null"/> when it references parameters or other values.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    public static string? GetLiteral(ReferenceExpression expression) =>
        // Names can't contain braces, so the composite format string of a literal expression is the text itself.
        expression.ValueProviders.Count == 0 && !expression.IsConditional ? expression.Format : null;

    /// <summary>
    /// Converts a time span to a Go duration string, which Traefik uses for intervals, for example <c>1s</c> or
    /// <c>250ms</c>. See <see href="https://pkg.go.dev/time#ParseDuration"/>.
    /// </summary>
    /// <param name="value">A positive time span.</param>
    /// <param name="parameterName">The parameter the value was passed in, for the exception.</param>
    /// <returns>The duration string.</returns>
    public static string ToDuration(TimeSpan value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, parameterName);

        // Whole seconds read better; anything finer is written in milliseconds, which Go accepts as well.
        return value.Ticks % TimeSpan.TicksPerSecond == 0
            ? FormattableString.Invariant($"{(long)value.TotalSeconds}s")
            : FormattableString.Invariant($"{(long)Math.Ceiling(value.TotalMilliseconds)}ms");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex ValidNameRegex();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}
