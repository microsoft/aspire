// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// A Traefik router rule, built from matchers combined with <c>&amp;</c> (and), <c>|</c> (or), and <c>!</c> (not).
/// </summary>
/// <remarks>
/// <para>
/// Rules render to Traefik's v3 rule syntax with every value in backticks and parentheses around every nested
/// combination, so operator precedence never matters:
/// <code lang="csharp">
/// TraefikRule.PathPrefix("/api") &amp; (TraefikRule.Method("GET") | TraefikRule.Header("X-Api", "1")) &amp; !TraefikRule.ClientIP("10.0.0.0/8")
/// // PathPrefix(`/api`) &amp;&amp; (Method(`GET`) || Header(`X-Api`, `1`)) &amp;&amp; !ClientIP(`10.0.0.0/8`)
/// </code>
/// </para>
/// <para>
/// Regular expressions use Go syntax and are not checked here; Traefik reports invalid ones in its log and dashboard.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/rules-and-priority/"/>.
/// </para>
/// </remarks>
public sealed class TraefikRule
{
    private readonly string? _matcher;
    private readonly object[] _arguments = [];
    private readonly string? _operator;
    private readonly TraefikRule[] _operands = [];

    private TraefikRule(string matcher, object[] arguments)
    {
        _matcher = matcher;
        _arguments = arguments;
    }

    private TraefikRule(string @operator, TraefikRule[] operands)
    {
        _operator = @operator;
        _operands = operands;
    }

    /// <summary>Matches requests for a host name, for example <c>api.example.com</c> or <c>*.example.com</c>.</summary>
    /// <param name="host">The host name.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule Host(string host) => Matcher(nameof(Host), host);

    /// <inheritdoc cref="Host(string)"/>
    /// <param name="host">The host name. It can reference parameters, for example <c>ReferenceExpression.Create($"api.{domain}")</c>.</param>
    public static TraefikRule Host(ReferenceExpression host) => Matcher(nameof(Host), host);

    /// <summary>Matches requests whose host name matches a regular expression.</summary>
    /// <param name="regex">The regular expression, in Go syntax.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule HostRegexp(string regex) => Matcher(nameof(HostRegexp), regex);

    /// <summary>Matches requests whose path is exactly <paramref name="path"/>.</summary>
    /// <param name="path">The path, starting with <c>/</c>.</param>
    /// <returns>The matcher.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> doesn't start with <c>/</c>.</exception>
    public static TraefikRule Path(string path) => Matcher(nameof(Path), RequirePath(path, nameof(path)));

    /// <summary>Matches requests whose path starts with <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The prefix, starting with <c>/</c>.</param>
    /// <returns>The matcher.</returns>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> doesn't start with <c>/</c>.</exception>
    public static TraefikRule PathPrefix(string prefix) => Matcher(nameof(PathPrefix), RequirePath(prefix, nameof(prefix)));

    /// <summary>Matches requests whose path matches a regular expression.</summary>
    /// <param name="regex">The regular expression, in Go syntax.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule PathRegexp(string regex) => Matcher(nameof(PathRegexp), regex);

    /// <summary>Matches requests with an HTTP method, for example <c>GET</c>.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule Method(string method) => Matcher(nameof(Method), method);

    /// <summary>Matches requests that have a header with a value.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule Header(string name, string value) => Matcher(nameof(Header), name, value);

    /// <summary>Matches requests that have a header whose value matches a regular expression.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="regex">The regular expression, in Go syntax.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule HeaderRegexp(string name, string regex) => Matcher(nameof(HeaderRegexp), name, regex);

    /// <summary>Matches requests that have a query parameter with a value.</summary>
    /// <param name="name">The query parameter name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule Query(string name, string value) => Matcher(nameof(Query), name, value);

    /// <summary>Matches requests that have a query parameter whose value matches a regular expression.</summary>
    /// <param name="name">The query parameter name.</param>
    /// <param name="regex">The regular expression, in Go syntax.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule QueryRegexp(string name, string regex) => Matcher(nameof(QueryRegexp), name, regex);

    /// <summary>Matches requests from a client address or CIDR range. <c>X-Forwarded-For</c> is ignored.</summary>
    /// <param name="ip">The address or range, for example <c>10.0.0.0/8</c>.</param>
    /// <returns>The matcher.</returns>
    public static TraefikRule ClientIP(string ip) => Matcher(nameof(ClientIP), ip);

    /// <summary>Matches requests that match both rules.</summary>
    /// <param name="left">The first rule.</param>
    /// <param name="right">The second rule.</param>
    /// <returns>The combined rule.</returns>
    public static TraefikRule operator &(TraefikRule left, TraefikRule right) => Combine("&&", left, right);

    /// <summary>Matches requests that match either rule.</summary>
    /// <param name="left">The first rule.</param>
    /// <param name="right">The second rule.</param>
    /// <returns>The combined rule.</returns>
    public static TraefikRule operator |(TraefikRule left, TraefikRule right) => Combine("||", left, right);

    /// <summary>Matches requests that don't match the rule.</summary>
    /// <param name="rule">The rule to invert.</param>
    /// <returns>The inverted rule.</returns>
    public static TraefikRule operator !(TraefikRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new("!", [rule]);
    }

    /// <summary>Matches requests that match both rules.</summary>
    /// <param name="other">The other rule.</param>
    /// <returns>The combined rule.</returns>
    public TraefikRule And(TraefikRule other) => this & other;

    /// <summary>Matches requests that match either rule.</summary>
    /// <param name="other">The other rule.</param>
    /// <returns>The combined rule.</returns>
    public TraefikRule Or(TraefikRule other) => this | other;

    /// <summary>Matches requests that don't match this rule.</summary>
    /// <returns>The inverted rule.</returns>
    public TraefikRule Not() => !this;

    /// <summary>
    /// Matches TLS connections by server name (SNI), for TCP routes.
    /// </summary>
    /// <param name="host">The server name.</param>
    /// <returns>The matcher.</returns>
    /// <remarks>
    /// TCP routers have their own matchers. Only HostSNI is needed, for <see cref="TraefikTcpRouteOptions"/>, so it isn't
    /// public: TCP and HTTP matchers can't be mixed in one rule. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/tcp/routing/rules-and-priority/"/>.
    /// </remarks>
    internal static TraefikRule HostSni(ReferenceExpression host) => Matcher("HostSNI", host);

    /// <summary>
    /// Matches every TCP connection, including plain TCP without a server name.
    /// </summary>
    /// <returns>The matcher.</returns>
    internal static TraefikRule AnyHostSni() => Matcher("HostSNI", "*");

    /// <summary>
    /// Appends the rule in Traefik's syntax, keeping expression values as value providers so parameters resolve per mode.
    /// </summary>
    /// <param name="builder">The builder the rule is appended to.</param>
    internal void WriteTo(ReferenceExpressionBuilder builder)
    {
        if (_matcher is not null)
        {
            builder.AppendLiteral($"{_matcher}(");
            for (var i = 0; i < _arguments.Length; i++)
            {
                builder.AppendLiteral(i == 0 ? "`" : ", `");
                if (_arguments[i] is ReferenceExpression expression)
                {
                    builder.AppendValueProvider(expression);
                }
                else
                {
                    builder.AppendLiteral((string)_arguments[i]);
                }
                builder.AppendLiteral("`");
            }
            builder.AppendLiteral(")");
            return;
        }

        if (_operator == "!")
        {
            builder.AppendLiteral("!");
            WriteOperand(builder, _operands[0], parenthesize: _operands[0]._matcher is null);
            return;
        }

        for (var i = 0; i < _operands.Length; i++)
        {
            if (i > 0)
            {
                builder.AppendLiteral($" {_operator} ");
            }

            // An operand of the other binary operator always gets parentheses, so readers don't need to know that
            // `&&` binds tighter than `||`.
            var operand = _operands[i];
            WriteOperand(builder, operand, parenthesize: operand._operator is "&&" or "||");
        }
    }

    private static TraefikRule Matcher(string matcher, params object[] arguments)
    {
        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case null:
                    throw new ArgumentNullException(nameof(arguments), $"A value of the {matcher} matcher is null.");
                case string text when string.IsNullOrEmpty(text):
                    throw new ArgumentException($"A value of the {matcher} matcher is empty.");
                case string text when text.Contains('`'):
                    // Values are quoted with backticks, so a backtick would end the value early.
                    throw new ArgumentException($"The value '{text}' of the {matcher} matcher can't contain '`'.");
            }
        }

        return new(matcher, arguments);
    }

    private static string RequirePath(string path, string parameterName)
    {
        TraefikNames.ThrowIfInvalidPath(path, "path", parameterName);

        return path;
    }

    private static TraefikRule Combine(string @operator, TraefikRule left, TraefikRule right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // Flatten chains of the same operator, so `a & b & c` renders without nested parentheses.
        var operands = new List<TraefikRule>();
        foreach (var operand in (TraefikRule[])[left, right])
        {
            if (operand._operator == @operator)
            {
                operands.AddRange(operand._operands);
            }
            else
            {
                operands.Add(operand);
            }
        }

        return new(@operator, [.. operands]);
    }

    private static void WriteOperand(ReferenceExpressionBuilder builder, TraefikRule operand, bool parenthesize)
    {
        if (parenthesize)
        {
            builder.AppendLiteral("(");
        }

        operand.WriteTo(builder);

        if (parenthesize)
        {
            builder.AppendLiteral(")");
        }
    }
}
