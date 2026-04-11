// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an expression that might be made up of multiple resource properties. For example,
/// a connection string might be made up of a host, port, and password from different endpoints.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ReferenceExpression"/> operates in one of two modes:
/// </para>
/// <list type="bullet">
///   <item><b>Value mode</b> — a format string with interpolated <see cref="IValueProvider"/> parameters
///   (e.g., <c>"redis://{0}:{1}"</c>).</item>
///   <item><b>Conditional mode</b> — a ternary-style expression that selects between two branch
///   expressions based on the string value of a <see cref="Condition"/>. Created via
///   <see cref="CreateConditional"/>.</item>
/// </list>
/// </remarks>
[AspireExport]
[DebuggerDisplay("ReferenceExpression = {ValueExpression}, Providers = {ValueProviders.Count}")]
public class ReferenceExpression : IExpressionValue, IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    /// <summary>
    /// Represents an empty reference expression with no name, value providers, or arguments.
    /// </summary>
    /// <remarks>Use this field to represent a default or uninitialized reference expression. The instance has
    /// an empty name and contains no value providers or arguments.</remarks>
    public static readonly ReferenceExpression Empty = Create(string.Empty, [], [], []);
    private readonly string[] _manifestExpressions;
    private readonly string?[] _stringFormats;

    // Conditional mode fields (null when in value mode)
    private readonly IValueProvider? _condition;
    private readonly ReferenceExpression? _whenTrue;
    private readonly ReferenceExpression? _whenFalse;
    private readonly string? _matchValue;
    private readonly string? _name;

    private ReferenceExpression(string format, IValueProvider[] valueProviders, string[] manifestExpressions, string?[] stringFormats)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(valueProviders);
        ArgumentNullException.ThrowIfNull(manifestExpressions);

        Format = format;
        ValueProviders = valueProviders;
        _manifestExpressions = manifestExpressions;
        _stringFormats = stringFormats;
    }

    private ReferenceExpression(IValueProvider condition, string matchValue, ReferenceExpression whenTrue, ReferenceExpression whenFalse)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(whenTrue);
        ArgumentNullException.ThrowIfNull(whenFalse);
        ArgumentException.ThrowIfNullOrEmpty(matchValue);

        _condition = condition;
        _whenTrue = whenTrue;
        _whenFalse = whenFalse;
        _matchValue = matchValue;
        _name = GenerateConditionalName(condition, matchValue, whenTrue, whenFalse);

        // Expose the union of both branches' value providers so that callers
        // iterating ValueProviders (e.g., publish contexts) can discover all
        // parameters and resources referenced by the conditional.
        Format = string.Empty;
        ValueProviders = whenTrue.ValueProviders.Concat(whenFalse.ValueProviders).ToArray();
        _manifestExpressions = [];
        _stringFormats = [];
    }

    /// <summary>
    /// The format string for this expression.
    /// </summary>
    public string Format { get; }

    /// <summary>
    /// The manifest expressions for the parameters for the format string.
    /// </summary>
    public IReadOnlyList<string> ManifestExpressions => _manifestExpressions;

    /// <summary>
    /// The string formats of the parameters, e.g. "uri".
    /// </summary>
    public IReadOnlyList<string?> StringFormats => _stringFormats;

    /// <summary>
    /// The list of <see cref="IValueProvider"/> that will be used to resolve parameters for the format string.
    /// </summary>
    public IReadOnlyList<IValueProvider> ValueProviders { get; }

    /// <summary>
    /// Gets a value indicating whether this expression is a conditional expression that selects
    /// between two branches based on a condition.
    /// </summary>
    public bool IsConditional => _condition is not null;

    /// <summary>
    /// Gets the condition value provider whose result is compared to <see cref="MatchValue"/>,
    /// or <see langword="null"/> when <see cref="IsConditional"/> is <see langword="false"/>.
    /// </summary>
    public IValueProvider? Condition => _condition;

    /// <summary>
    /// Gets the expression to evaluate when <see cref="Condition"/> evaluates to <see cref="MatchValue"/>,
    /// or <see langword="null"/> when <see cref="IsConditional"/> is <see langword="false"/>.
    /// </summary>
    public ReferenceExpression? WhenTrue => _whenTrue;

    /// <summary>
    /// Gets the expression to evaluate when <see cref="Condition"/> does not evaluate to <see cref="MatchValue"/>,
    /// or <see langword="null"/> when <see cref="IsConditional"/> is <see langword="false"/>.
    /// </summary>
    public ReferenceExpression? WhenFalse => _whenFalse;

    /// <summary>
    /// Gets the value that <see cref="Condition"/> is compared against to select the <see cref="WhenTrue"/> branch,
    /// or <see langword="null"/> when <see cref="IsConditional"/> is <see langword="false"/>.
    /// </summary>
    public string? MatchValue => _matchValue;

    /// <summary>
    /// Gets the name of this conditional expression, used as the manifest resource name for the <c>value.v0</c> entry,
    /// or <see langword="null"/> when <see cref="IsConditional"/> is <see langword="false"/>.
    /// </summary>
    internal string? Name => _name;

    IEnumerable<object> IValueWithReferences.References
    {
        get
        {
            if (IsConditional)
            {
                // Yield the condition itself so dependency tracking discovers it as an IResource,
                // then yield its sub-references if it implements IValueWithReferences.
                yield return _condition!;

                if (_condition is IValueWithReferences conditionRefs)
                {
                    foreach (var reference in conditionRefs.References)
                    {
                        yield return reference;
                    }
                }

                foreach (var reference in ((IValueWithReferences)_whenTrue!).References)
                {
                    yield return reference;
                }

                foreach (var reference in ((IValueWithReferences)_whenFalse!).References)
                {
                    yield return reference;
                }

                yield break;
            }

            foreach (var vp in ValueProviders)
            {
                yield return vp;
            }
        }
    }

    /// <summary>
    /// The value expression for the format string.
    /// </summary>
    public string ValueExpression =>
        IsConditional
            ? $"{{{_name}.connectionString}}"
            : string.Format(CultureInfo.InvariantCulture, Format, _manifestExpressions);

    /// <summary>
    /// Gets the value of the expression. The final string value after evaluating the format string and its parameters.
    /// </summary>
    /// <param name="context">A context for resolving the value.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    public async ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        if (IsConditional)
        {
            var conditionValue = await _condition!.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
            var branch = string.Equals(conditionValue, _matchValue, StringComparison.OrdinalIgnoreCase) ? _whenTrue! : _whenFalse!;
            return await branch.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // NOTE: any logical changes to this method should also be made to ExpressionResolver.EvalExpressionAsync
        if (Format.Length == 0)
        {
            return null;
        }

        var args = new object?[ValueProviders.Count];
        for (var i = 0; i < ValueProviders.Count; i++)
        {
            args[i] = await ValueProviders[i].GetValueAsync(context, cancellationToken).ConfigureAwait(false);

            // Apply string format if needed
            var stringFormat = _stringFormats[i];
            if (stringFormat is not null && args[i] is string s)
            {
                args[i] = FormattingHelpers.FormatValue(s, stringFormat);
            }
        }

        return string.Format(CultureInfo.InvariantCulture, Format, args);
    }

    /// <summary>
    /// Gets the value of the expression. The final string value after evaluating the format string and its parameters.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    [AspireExport("getValue", Description = "Gets the resolved string value of the reference expression asynchronously")]
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken)
    {
        return this.GetValueAsync(new(), cancellationToken);
    }

    internal static ReferenceExpression Create(string format, IValueProvider[] valueProviders, string[] manifestExpressions, string?[] stringFormats)
    {
        return new(format, valueProviders, manifestExpressions, stringFormats);
    }

    /// <summary>
    /// Creates a new instance of <see cref="ReferenceExpression"/> with the specified format and value providers.
    /// </summary>
    /// <param name="handler">The handler that contains the format and value providers.</param>
    /// <returns>A new instance of <see cref="ReferenceExpression"/> with the specified format and value providers.</returns>
    public static ReferenceExpression Create(in ExpressionInterpolatedStringHandler handler)
    {
        return handler.GetExpression();
    }

    /// <summary>
    /// Creates a conditional <see cref="ReferenceExpression"/> that selects between two branch expressions
    /// based on the string value of a condition.
    /// </summary>
    /// <param name="condition">A value provider whose result is compared to <paramref name="matchValue"/>
    /// to determine which branch to evaluate.</param>
    /// <param name="matchValue">The string value that <paramref name="condition"/> is compared against.
    /// When the condition's value equals this (case-insensitive), the <paramref name="whenTrue"/> branch is selected.</param>
    /// <param name="whenTrue">The expression to evaluate when the condition matches <paramref name="matchValue"/>.</param>
    /// <param name="whenFalse">The expression to evaluate when the condition does not match <paramref name="matchValue"/>.</param>
    /// <returns>A new conditional <see cref="ReferenceExpression"/>.</returns>
    public static ReferenceExpression CreateConditional(IValueProvider condition, string matchValue, ReferenceExpression whenTrue, ReferenceExpression whenFalse)
    {
        return new ReferenceExpression(condition, matchValue, whenTrue, whenFalse);
    }

    private static string GenerateConditionalName(IValueProvider condition, string matchValue, ReferenceExpression whenTrue, ReferenceExpression whenFalse)
    {
        string baseName;

        if (condition is IManifestExpressionProvider expressionProvider)
        {
            var expression = expressionProvider.ValueExpression;
            var sanitized = SanitizeExpression(expression);
            baseName = sanitized.Length > 0 ? $"cond-{sanitized}" : "cond-expr";
        }
        else
        {
            baseName = "cond-expr";
        }

        var hash = ComputeConditionalHash(condition, whenTrue, whenFalse, matchValue);
        return $"{baseName}-{hash}";
    }

    private static string ComputeConditionalHash(IValueProvider condition, ReferenceExpression whenTrue, ReferenceExpression whenFalse, string matchValue)
    {
        var xxHash = new XxHash32();

        var conditionExpr = condition is IManifestExpressionProvider mep ? mep.ValueExpression : condition.GetType().Name;
        xxHash.Append(Encoding.UTF8.GetBytes(conditionExpr));
        xxHash.Append(Encoding.UTF8.GetBytes(whenTrue.ValueExpression));
        xxHash.Append(Encoding.UTF8.GetBytes(whenFalse.ValueExpression));
        xxHash.Append(Encoding.UTF8.GetBytes(matchValue));

        var hashBytes = xxHash.GetCurrentHash();
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string SanitizeExpression(string expression)
    {
        var builder = new StringBuilder(expression.Length);
        var lastWasSeparator = false;

        foreach (var ch in expression)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    /// <summary>
    /// Represents a handler for interpolated strings that contain expressions. Those expressions will either be literal strings or
    /// instances of types that implement both <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <param name="literalLength">The length of the literal part of the interpolated string.</param>
    /// <param name="formattedCount">The number of formatted parts in the interpolated string.</param>
    [InterpolatedStringHandler]
    public ref struct ExpressionInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        private static readonly char[] s_braces = ['{', '}'];
        private readonly StringBuilder _builder = new(literalLength * 2);
        private readonly List<IValueProvider> _valueProviders = new(formattedCount);
        private readonly List<string> _manifestExpressions = new(formattedCount);
        private readonly List<string?> _stringFormats = new(formattedCount);

        /// <summary>
        /// Appends a literal value to the expression.
        /// </summary>
        /// <param name="value">The literal string value to be appended to the interpolated string.</param>
        public readonly void AppendLiteral(string value)
        {
            // Only escape single braces, leave already escaped braces untouched
            _builder.Append(EscapeUnescapedBraces(value));
        }

        /// <summary>
        /// Appends a formatted value to the expression.
        /// </summary>
        /// <param name="value">The formatted string to be appended to the interpolated string.</param>
        public readonly void AppendFormatted(string? value)
        {
            AppendFormatted(value, format: null);
        }

        /// <summary>
        /// Appends a formatted value to the expression.
        /// </summary>
        /// <param name="value">The formatted string to be appended to the interpolated string.</param>
        /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
        public readonly void AppendFormatted(string? value, string? format = null)
        {
            // The value that comes in is a literal string that is not meant to be interpreted.
            // But the _builder later gets treated as a format string, so we just need to escape the braces.
            if (value is not null)
            {
                if (format is not null)
                {
                    value = FormattingHelpers.FormatValue(value, format);
                }

                _builder.Append(EscapeUnescapedBraces(value));
            }
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(T valueProvider) where T : IValueProvider, IManifestExpressionProvider
        {
            AppendFormatted(valueProvider, format: null);
        }

        /// <summary>
        /// Appends the formatted value provided by the specified reference expression to the output.
        /// </summary>
        /// <param name="valueProvider">A reference expression that supplies the value to be formatted and appended.</param>
        /// <param name="format">A composite format string that specifies how the value should be formatted, or null to use the default format.</param>
        /// <remarks>This method is marked obsolete only to prevent usages of this type explicitly.</remarks>
        [Obsolete("ReferenceExpression instances can't be used in interpolated string with a custom format. Duplicate the inner expression in-place.", error: true)]
        public void AppendFormatted(ReferenceExpression valueProvider, string format)
        {
            throw new InvalidOperationException("ReferenceExpression instances can't be used in interpolated string with a custom format. Duplicate the inner expression in-place.");
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(T valueProvider, string? format = null) where T : IValueProvider, IManifestExpressionProvider
        {
            var index = _valueProviders.Count;
            _builder.Append(CultureInfo.InvariantCulture, $"{{{index}}}");

            _valueProviders.Add(valueProvider);
            _manifestExpressions.Add(valueProvider.ValueExpression);
            _stringFormats.Add(format);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(IResourceBuilder<T> valueProvider)
            where T : IResource, IValueProvider, IManifestExpressionProvider
        {
            AppendFormatted(valueProvider, format: null);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(IResourceBuilder<T> valueProvider, string? format = null)
            where T : IResource, IValueProvider, IManifestExpressionProvider
        {
            var index = _valueProviders.Count;
            _builder.Append(CultureInfo.InvariantCulture, $"{{{index}}}");

            _valueProviders.Add(valueProvider.Resource);
            _manifestExpressions.Add(valueProvider.Resource.ValueExpression);
            _stringFormats.Add(format);
        }

        internal readonly ReferenceExpression GetExpression() =>
            new(_builder.ToString(), [.. _valueProviders], [.. _manifestExpressions], [.. _stringFormats]);

        private static string EscapeUnescapedBraces(string input)
        {
            // Fast path: nothing to escape
            if (input.IndexOfAny(s_braces) == -1)
            {
                return input;
            }

            // Allocate a bit of extra space in case we need to escape a few braces.
            var sb = new StringBuilder(input.Length + 4);

            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (IsBrace(c))
                {
                    if (IsNextCharSame(input, i))
                    {
                        // Already escaped, copy both and skip next
                        sb.Append(c).Append(c);
                        i++;
                    }
                    else
                    {
                        // Escape single brace
                        sb.Append(c).Append(c);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();

            static bool IsBrace(char ch) => ch == '{' || ch == '}';
            static bool IsNextCharSame(string s, int idx) =>
                idx + 1 < s.Length && s[idx + 1] == s[idx];
        }
    }
}

/// <summary>
/// A builder for creating <see cref="ReferenceExpression"/> instances.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class ReferenceExpressionBuilder
{
    private readonly StringBuilder _builder = new();
    private readonly List<IValueProvider> _valueProviders = new();
    private readonly List<string> _manifestExpressions = new();
    private readonly List<string?> _stringFormats = new();

    /// <summary>
    /// Indicates whether the expression is empty.
    /// </summary>
    public bool IsEmpty => _builder.Length == 0;

    /// <summary>
    /// Appends an interpolated string to the expression.
    /// </summary>
    /// <param name="handler"></param>
    public void Append([InterpolatedStringHandlerArgument("")] in ReferenceExpressionBuilderInterpolatedStringHandler handler)
    {
    }

    /// <summary>
    /// Appends a literal value to the expression.
    /// </summary>
    /// <param name="value">The literal string value to be appended to the interpolated string.</param>
    [AspireExport(Description = "Appends a literal string to the reference expression")]
    public void AppendLiteral(string value)
    {
        _builder.Append(value);
    }

    /// <summary>
    /// Appends a formatted value to the expression.
    /// </summary>
    /// <param name="value">The formatted string to be appended to the interpolated string.</param>
    public void AppendFormatted(string? value)
    {
        _builder.Append(value);
    }

    /// <summary>
    /// Appends a formatted value to the expression.
    /// </summary>
    /// <param name="value">The formatted string to be appended to the interpolated string.</param>
    /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
    [AspireExport(Description = "Appends a formatted string value to the reference expression")]
    public void AppendFormatted(string? value, string? format = null)
    {
        if (value is not null)
        {
            if (format is not null)
            {
                value = FormattingHelpers.FormatValue(value, format);
            }

            _builder.Append(value);
        }
    }

    /// <summary>
    /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
    /// <exception cref="InvalidOperationException"></exception>
    public void AppendFormatted<T>(T valueProvider) where T : IValueProvider, IManifestExpressionProvider
    {
        AppendFormatted(valueProvider, format: null);
    }

    /// <summary>
    /// Appends the formatted value provided by the specified reference expression to the output.
    /// </summary>
    /// <param name="valueProvider">A reference expression that supplies the value to be formatted and appended.</param>
    /// <param name="format">A composite format string that specifies how the value should be formatted, or null to use the default format.</param>
    /// <remarks>This method is marked obsolete only to prevent usages of this type explicitly.</remarks>
    [Obsolete("ReferenceExpression instances can't be used in interpolated string with a custom format. Duplicate the inner expression in-place.", error: true)]
    public void AppendFormatted(ReferenceExpression valueProvider, string format)
    {
        throw new InvalidOperationException("ReferenceExpression instances can't be used in interpolated string with a custom format. Duplicate the inner expression in-place.");
    }

    /// <summary>
    /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
    /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
    /// <exception cref="InvalidOperationException"></exception>
    public void AppendFormatted<T>(T valueProvider, string? format) where T : IValueProvider, IManifestExpressionProvider
    {
        var index = _valueProviders.Count;
        _builder.Append(CultureInfo.InvariantCulture, $"{{{index}}}");

        _valueProviders.Add(valueProvider);
        _manifestExpressions.Add(valueProvider.ValueExpression);
        _stringFormats.Add(format);
    }

    /// <summary>
    /// Appends a value provider to the expression using late binding.
    /// The object must implement both <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>,
    /// or be an <see cref="IResourceBuilder{T}"/> where T implements both interfaces.
    /// </summary>
    /// <param name="valueProvider">An object that implements both interfaces, or an IResourceBuilder wrapping such an object.</param>
    /// <param name="format">Optional format specifier.</param>
    /// <exception cref="ArgumentException">Thrown if the object doesn't implement the required interfaces.</exception>
    [AspireExport(Description = "Appends a value provider to the reference expression")]
    public void AppendValueProvider(object valueProvider, string? format = null)
    {
        // Unwrap IResourceBuilder<T> to get the underlying resource (covariant interface)
        var unwrapped = valueProvider is IResourceBuilder<IResource> rb ? rb.Resource : valueProvider;

        if (unwrapped is not IValueProvider vp)
        {
            throw new ArgumentException($"Object must implement IValueProvider", nameof(valueProvider));
        }
        if (unwrapped is not IManifestExpressionProvider mep)
        {
            throw new ArgumentException($"Object must implement IManifestExpressionProvider", nameof(valueProvider));
        }

        var index = _valueProviders.Count;
        _builder.Append(CultureInfo.InvariantCulture, $"{{{index}}}");

        _valueProviders.Add(vp);
        _manifestExpressions.Add(mep.ValueExpression);
        _stringFormats.Add(format);
    }

    /// <summary>
    /// Builds the <see cref="ReferenceExpression"/>.
    /// </summary>
    [AspireExport(Description = "Builds the reference expression")]
    public ReferenceExpression Build() =>
        ReferenceExpression.Create(_builder.ToString(), [.. _valueProviders], [.. _manifestExpressions], [.. _stringFormats]);

    /// <summary>
    /// Represents a handler for interpolated strings that contain expressions. Those expressions will either be literal strings or
    /// instances of types that implement both <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <param name="literalLength">The length of the literal part of the interpolated string.</param>
    /// <param name="formattedCount">The number of formatted parts in the interpolated string.</param>
    /// <param name="builder">The builder that will be used to create the <see cref="ReferenceExpression"/>.</param>
    [InterpolatedStringHandler]
#pragma warning disable CS9113 // Parameter is unread.
    public ref struct ReferenceExpressionBuilderInterpolatedStringHandler(int literalLength, int formattedCount, ReferenceExpressionBuilder builder)
#pragma warning restore CS9113 // Parameter is unread.
    {
        /// <summary>
        /// Appends a literal value to the expression.
        /// </summary>
        /// <param name="value">The literal string value to be appended to the interpolated string.</param>
        public readonly void AppendLiteral(string value)
        {
            builder.AppendLiteral(value);
        }

        /// <summary>
        /// Appends a formatted value to the expression.
        /// </summary>
        /// <param name="value">The formatted string to be appended to the interpolated string.</param>
        public readonly void AppendFormatted(string? value)
        {
            builder.AppendFormatted(value);
        }

        /// <summary>
        /// Appends a formatted value to the expression.
        /// </summary>
        /// <param name="value">The formatted string to be appended to the interpolated string.</param>
        /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
        public void AppendFormatted(string? value, string format)
        {
            builder.AppendFormatted(value, format);
        }

        /// <summary>
        /// Appends the formatted value provided by the specified reference expression to the output.
        /// </summary>
        /// <param name="valueProvider">A reference expression that supplies the value to be formatted and appended.</param>
        /// <param name="format">A composite format string that specifies how the value should be formatted, or null to use the default format.</param>
        /// <remarks>This method is marked obsolete only to prevent usages of this type explicitly.</remarks>
        [Obsolete("ReferenceExpression instances can't be used in interpolated string with a custom format. Duplicate the inner expression in-place.", error: true)]
        public void AppendFormatted(ReferenceExpression valueProvider, string format)
        {
            throw new InvalidOperationException("ReferenceExpression instances can't be used in interpolated string with a custom format. Duplicate the inner expression in-place.");
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(T valueProvider) where T : IValueProvider, IManifestExpressionProvider
        {
            AppendFormatted(valueProvider, format: null);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(T valueProvider, string? format) where T : IValueProvider, IManifestExpressionProvider
        {
            builder.AppendFormatted(valueProvider, format);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(IResourceBuilder<T> valueProvider)
            where T : IResource, IValueProvider, IManifestExpressionProvider
        {
            AppendFormatted(valueProvider, format: null);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <param name="format">The format to be applied to the value. e.g., "uri"</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(IResourceBuilder<T> valueProvider, string? format)
            where T : IResource, IValueProvider, IManifestExpressionProvider
        {
            builder.AppendFormatted(valueProvider.Resource, format);
        }
    }
}
