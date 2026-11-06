// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Builds the router rule of a route from its options.
/// </summary>
internal static class TraefikRouteRule
{
    /// <summary>
    /// Builds a rule such as <c>Host(`api.example.com`) &amp;&amp; PathPrefix(`/v1`)</c>, with every matcher option combined
    /// with <c>&amp;&amp;</c>.
    /// </summary>
    /// <param name="options">The route options.</param>
    /// <returns>The rule.</returns>
    /// <exception cref="ArgumentException">The options contain no matcher, or an invalid value.</exception>
    /// <remarks>
    /// The rule is an expression, so parameters in it resolve in run mode and become <c>${VAR}</c> placeholders at
    /// publish time. See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/rules-and-priority/"/>.
    /// </remarks>
    public static ReferenceExpression Build(TraefikRouteOptions options)
    {
        var builder = new ReferenceExpressionBuilder();

        if (options.RawRule is { } rawRule)
        {
            // Host may be set next to a raw rule: it isn't added to the rule then, but gives the route its public URL
            // (GetTraefikUrl) and its oauth2-proxy sign-in route, which a rule string can't provide.
            if (HasMatchersOtherThanHost(options))
            {
                throw new ArgumentException($"{nameof(TraefikRouteOptions.RawRule)} can't be combined with other matchers. Write the whole rule in it.");
            }

            builder.AppendLiteral(rawRule);
            return builder.Build();
        }

        var matchers = new List<TraefikRule>();

        if (options.Host is { } host)
        {
            matchers.Add(TraefikRule.Host(host));
        }

        if (options.Path is { } path)
        {
            matchers.Add(TraefikRule.Path(path));
        }

        if (options.PathPrefix is { } pathPrefix)
        {
            matchers.Add(TraefikRule.PathPrefix(pathPrefix));
        }

        // Several methods or client ranges mean any of them.
        AddAny(matchers, options.Methods.Select(TraefikRule.Method));
        matchers.AddRange(options.Headers.Select(header => TraefikRule.Header(header.Key, header.Value)));
        matchers.AddRange(options.Query.Select(query => TraefikRule.Query(query.Key, query.Value)));
        AddAny(matchers, options.ClientIps.Select(TraefikRule.ClientIP));

        if (options.Rule is { } rule)
        {
            matchers.Add(rule);
        }

        if (matchers.Count == 0)
        {
            throw new ArgumentException("A Traefik route needs a matcher, for example a host or a path prefix.");
        }

        matchers.Aggregate((left, right) => left & right).WriteTo(builder);
        return builder.Build();
    }

    /// <summary>
    /// Builds the rule of a TCP route: <c>HostSNI(`amqp.example.com`)</c>, or <c>HostSNI(`*`)</c> for every connection.
    /// </summary>
    /// <param name="options">The TCP route options.</param>
    /// <returns>The rule.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/tcp/routing/rules-and-priority/"/>.</remarks>
    public static ReferenceExpression Build(TraefikTcpRouteOptions options)
    {
        var builder = new ReferenceExpressionBuilder();
        (options.HostSni is { } host ? TraefikRule.HostSni(host) : TraefikRule.AnyHostSni()).WriteTo(builder);
        return builder.Build();
    }

    private static bool HasMatchersOtherThanHost(TraefikRouteOptions options) =>
        options.Path is not null || options.PathPrefix is not null || options.Methods.Count > 0 ||
        options.Headers.Count > 0 || options.Query.Count > 0 || options.ClientIps.Count > 0 || options.Rule is not null;

    private static void AddAny(List<TraefikRule> matchers, IEnumerable<TraefikRule> alternatives)
    {
        var list = alternatives.ToList();
        if (list.Count > 0)
        {
            matchers.Add(list.Aggregate((left, right) => left | right));
        }
    }
}
