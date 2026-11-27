// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Writes the <c>traefik.*</c> labels that Traefik's Docker provider reads onto a routed Docker Compose service.
/// </summary>
/// <remarks>
/// For a route named <c>api</c> the labels look like:
/// <code>
/// traefik.enable: "true"
/// traefik.http.routers.api.rule: Host(`api.${DOMAIN}`)
/// traefik.http.routers.api.entrypoints: websecure
/// traefik.http.routers.api.service: api
/// traefik.http.routers.api.tls: "true"
/// traefik.http.routers.api.tls.certresolver: letsencrypt
/// traefik.http.services.api.loadbalancer.server.port: "8080"
/// </code>
/// Docker Compose interpolates <c>${VAR}</c> in label values, which keeps parameter values in the generated
/// <c>.env</c> file. A literal <c>$</c> must therefore be written as <c>$$</c>.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/other-providers/docker/"/> and
/// <see href="https://docs.docker.com/reference/compose-file/interpolation/"/>.
/// </remarks>
internal static class TraefikDockerLabels
{
    /// <summary>
    /// The prefix of the label that marks a service as routed by one managed Traefik, for example
    /// <c>aspire.traefik.traefik: "true"</c>. Each managed Traefik only reads services with its own label (see
    /// <see cref="GetInstanceConstraint"/>), so two of them in one deployment don't serve each other's routes.
    /// </summary>
    public const string InstanceLabelPrefix = "aspire.traefik.";

    // Routed services opt in with this label, because Traefik's Docker provider is started with exposedByDefault=false.
    private const string EnableLabel = "traefik.enable";

    private const string TargetPortVariablePrefix = "ASPIRE_TRAEFIK_TARGET_PORT_";

    private const string MiddlewareValueVariablePrefix = "ASPIRE_TRAEFIK_MIDDLEWARE_";

    /// <summary>
    /// Writes the labels of an HTTP route: its router, its service, and the middlewares it uses.
    /// </summary>
    /// <param name="composeService">The Docker Compose service resource of the routed resource, which names the placeholders of parameters.</param>
    /// <param name="service">The generated Docker Compose service the labels are added to.</param>
    /// <param name="route">The HTTP route.</param>
    /// <param name="defaults">The entry point, TLS, and network defaults of the route's Traefik.</param>
    public static void Apply(DockerComposeServiceResource composeService, Service service, TraefikRoute route, TraefikRouteDefaults defaults)
    {
        var labels = service.Labels;
        var router = $"traefik.http.routers.{route.RouterName}";
        var loadBalancer = $"traefik.http.services.{route.RouterName}.loadbalancer";

        labels[EnableLabel] = "true";
        AddInstanceLabel(labels, route.Traefik);
        labels[$"{router}.rule"] = ToComposeExpression(composeService, TraefikRouteRule.Build(route.Options));
        labels[$"{router}.entrypoints"] = route.Options.EntryPoint ?? ToComposeExpression(composeService, defaults.EntryPoint);
        labels[$"{router}.service"] = route.RouterName;

        if (route.Options.Priority is int priority)
        {
            labels[$"{router}.priority"] = priority.ToString(CultureInfo.InvariantCulture);
        }

        if (route.Options.CertResolver is { } routeCertResolver)
        {
            labels[$"{router}.tls"] = "true";
            labels[$"{router}.tls.certresolver"] = routeCertResolver;
        }
        else if (route.Options.EntryPoint is null)
        {
            if (defaults.Tls)
            {
                labels[$"{router}.tls"] = "true";
            }

            if (defaults.CertResolver is { } certResolver)
            {
                labels[$"{router}.tls.certresolver"] = ToComposeExpression(composeService, certResolver);
            }
        }

        if (route.Middlewares.Count > 0)
        {
            // Middlewares are defined on every routed service that uses them, not on Traefik's own service, so the
            // same labels work for an external Traefik. Traefik accepts identical definitions from several
            // containers. Within the Docker provider a middleware is referenced without the `@docker` suffix.
            labels[$"{router}.middlewares"] = string.Join(",", route.Middlewares.Select(m => m.Name));

            // A chain refers to its members by name, so their definitions go along with it.
            foreach (var middleware in route.Middlewares.SelectMany(m => m.WithReferences()).Distinct())
            {
                var definition = $"traefik.http.middlewares.{middleware.Name}.{middleware.Type}";
                if (middleware.Options.Count == 0)
                {
                    // A middleware with only default options (for example `compress`) is declared with `=true`.
                    labels[definition] = "true";
                    continue;
                }

                var probes = GetMiddlewareValueProbes(middleware);
                middleware.Options.AddLabels(labels, definition, expression =>
                    probes.TryGetValue(expression, out var variable) && service.Environment.TryGetValue(variable, out var resolved)
                        ? resolved
                        : ToComposeExpression(composeService, expression));
            }
        }

        // Pin the backend port. Traefik only infers it when the container exposes exactly one port, which isn't
        // true for services with several endpoints.
        labels[$"{loadBalancer}.server.port"] = GetTargetPort(service, GetTargetPortVariableName(route), route.Endpoint, route.RouterName);

        if (string.Equals(route.Endpoint.EndpointAnnotation.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            labels[$"{loadBalancer}.server.scheme"] = "https";
        }
        else if (TraefikLoadBalancer.UsesH2c(route.Endpoint))
        {
            labels[$"{loadBalancer}.server.scheme"] = "h2c";
        }

        TraefikLoadBalancer.AddLabels(labels, loadBalancer, TraefikLoadBalancer.Build(route));

        JoinNetwork(service, defaults);
    }

    /// <summary>
    /// Writes the labels of a TCP or UDP route, for example:
    /// <code>
    /// traefik.tcp.routers.messaging.rule: HostSNI(`amqp.${DOMAIN}`)
    /// traefik.tcp.routers.messaging.entrypoints: amqps
    /// traefik.tcp.routers.messaging.service: messaging
    /// traefik.tcp.routers.messaging.tls: "true"
    /// traefik.tcp.routers.messaging.tls.certresolver: letsencrypt
    /// traefik.tcp.services.messaging.loadbalancer.server.port: "5672"
    /// </code>
    /// A UDP router has no rule and no TLS. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/other-providers/docker/#tcp-routers"/>.
    /// </summary>
    /// <param name="composeService">The Docker Compose service resource of the routed resource, which names the placeholders of parameters.</param>
    /// <param name="service">The generated Docker Compose service the labels are added to.</param>
    /// <param name="route">The TCP or UDP route.</param>
    /// <param name="defaults">The TLS and network defaults of the route's Traefik.</param>
    public static void Apply(DockerComposeServiceResource composeService, Service service, TraefikTransportRoute route, TraefikRouteDefaults defaults)
    {
        var labels = service.Labels;
        var router = $"traefik.{route.Protocol}.routers.{route.RouterName}";

        labels[EnableLabel] = "true";
        AddInstanceLabel(labels, route.Traefik);

        if (route.Tcp is { } tcp)
        {
            labels[$"{router}.rule"] = ToComposeExpression(composeService, TraefikRouteRule.Build(tcp));

            if (tcp.Priority is int priority)
            {
                labels[$"{router}.priority"] = priority.ToString(CultureInfo.InvariantCulture);
            }

            if (tcp.TlsPassthrough)
            {
                labels[$"{router}.tls"] = "true";
                labels[$"{router}.tls.passthrough"] = "true";
            }
            else if (tcp.Tls)
            {
                labels[$"{router}.tls"] = "true";

                if (tcp.CertResolver is { } certResolver)
                {
                    labels[$"{router}.tls.certresolver"] = certResolver;
                }
                else if (defaults.CertResolver is { } defaultResolver)
                {
                    labels[$"{router}.tls.certresolver"] = ToComposeExpression(composeService, defaultResolver);
                }
            }
        }

        labels[$"{router}.entrypoints"] = route.EntryPoint;
        labels[$"{router}.service"] = route.RouterName;
        labels[$"traefik.{route.Protocol}.services.{route.RouterName}.loadbalancer.server.port"] =
            GetTargetPort(service, GetTargetPortVariableName(route), route.Endpoint, route.RouterName);

        JoinNetwork(service, defaults);
    }

    /// <summary>
    /// Gets the Docker provider constraint that limits a managed Traefik to the services routed through it, for example
    /// <c>Label(`aspire.traefik.traefik`, `true`)</c>.
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/providers/docker/#constraints"/>.
    /// </summary>
    /// <param name="traefik">The managed Traefik.</param>
    /// <returns>The constraint expression.</returns>
    public static string GetInstanceConstraint(TraefikResource traefik) =>
        $"Label(`{InstanceLabelPrefix}{traefik.Name}`, `true`)";

    /// <summary>
    /// The name of the temporary environment variable that carries a route's backend port from the Docker Compose
    /// publisher to the labels. See <see cref="AddTargetPortProbe"/>.
    /// </summary>
    /// <param name="route">The HTTP route.</param>
    /// <returns>The variable name, for example <c>ASPIRE_TRAEFIK_TARGET_PORT_API</c>.</returns>
    public static string GetTargetPortVariableName(TraefikRoute route) =>
        $"{TargetPortVariablePrefix}{TraefikNames.ToEnvironmentName(route.RouterName)}";

    /// <inheritdoc cref="GetTargetPortVariableName(TraefikRoute)"/>
    /// <remarks>
    /// The protocol is part of the name because TCP and UDP routers have their own namespaces, so their names can repeat
    /// an HTTP router's.
    /// </remarks>
    public static string GetTargetPortVariableName(TraefikTransportRoute route) =>
        $"{TargetPortVariablePrefix}{route.Protocol.ToUpperInvariant()}_{TraefikNames.ToEnvironmentName(route.RouterName)}";

    /// <summary>
    /// Adds an environment variable to the routed resource whose value is the target port of the route's endpoint.
    /// </summary>
    /// <param name="resource">The routed resource.</param>
    /// <param name="variableName">The variable name, from <c>GetTargetPortVariableName</c>.</param>
    /// <param name="endpoint">The endpoint the route sends traffic to.</param>
    /// <remarks>
    /// The container port of a service is decided by the Docker Compose publisher: a fixed target port, a port it
    /// allocates for containers without one (for example <c>8000</c>), or a <c>${API_PORT}</c> placeholder for .NET
    /// projects. That mapping isn't public, but the publisher resolves endpoint references in environment variables
    /// with it. So the port goes through an environment variable, which <see cref="Apply(DockerComposeServiceResource, Service, TraefikRoute, TraefikRouteDefaults)"/> reads back into the label
    /// and then removes from the service.
    /// </remarks>
    public static void AddTargetPortProbe(IResourceWithEnvironment resource, string variableName, EndpointReference endpoint)
    {
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables[variableName] = endpoint.Property(EndpointProperty.TargetPort);
        }));
    }

    /// <summary>
    /// Gets the name of the environment variable that carries a middleware option referencing an endpoint, such as a
    /// forward authentication address. In run mode Traefik itself gets these variables (see <c>AddTraefik</c>); at
    /// publish time the routed services do (see <see cref="AddMiddlewareValueProbes"/>).
    /// </summary>
    /// <param name="middleware">The middleware.</param>
    /// <param name="index">The 1-based position of the value among the middleware's endpoint-referencing values.</param>
    /// <returns>The variable name, for example <c>ASPIRE_TRAEFIK_MIDDLEWARE_SSO_1</c>.</returns>
    public static string GetMiddlewareValueVariableName(TraefikMiddlewareResource middleware, int index) =>
        FormattableString.Invariant($"{MiddlewareValueVariablePrefix}{TraefikNames.ToEnvironmentName(middleware.Name)}_{index}");

    /// <summary>
    /// Adds environment variables to the routed resource whose values are the middleware options that reference
    /// endpoints, for example <c>http://sso-proxy:4180/</c>.
    /// </summary>
    /// <param name="resource">The resource whose Docker Compose service gets the definitions of the middlewares.</param>
    /// <param name="middlewares">The middlewares.</param>
    /// <remarks>
    /// As with <see cref="AddTargetPortProbe"/>, the Docker Compose publisher's mapping from an endpoint to a service
    /// address isn't public, but it resolves endpoint references in environment variables. <see cref="Apply(DockerComposeServiceResource, Service, TraefikRoute, TraefikRouteDefaults)"/> reads the
    /// values back into the labels, and <see cref="RemoveProbes"/> removes the variables afterwards.
    /// </remarks>
    public static void AddMiddlewareValueProbes(IResourceWithEnvironment resource, IEnumerable<TraefikMiddlewareResource> middlewares)
    {
        var probes = middlewares.SelectMany(GetMiddlewareValueProbes).ToList();
        if (probes.Count == 0)
        {
            return;
        }

        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            foreach (var (expression, variable) in probes)
            {
                context.EnvironmentVariables[variable] = expression;
            }
        }));
    }

    /// <summary>
    /// Removes the middleware value variables once every route of the service has its labels.
    /// </summary>
    /// <param name="service">The generated Docker Compose service.</param>
    public static void RemoveProbes(Service service)
    {
        foreach (var name in service.Environment.Keys.Where(k => k.StartsWith(MiddlewareValueVariablePrefix, StringComparison.Ordinal)).ToList())
        {
            service.Environment.Remove(name);
        }
    }

    /// <summary>
    /// Gets the temporary environment variable that carries each endpoint-referencing value of a middleware's options.
    /// </summary>
    /// <param name="middleware">The middleware.</param>
    /// <returns>The variable name of each value, keyed by the expression object.</returns>
    public static Dictionary<ReferenceExpression, string> GetMiddlewareValueProbes(TraefikMiddlewareResource middleware) =>
        middleware.Options.GetEndpointExpressions()
            .Select((expression, i) => (expression, variable: GetMiddlewareValueVariableName(middleware, i + 1)))
            // Options hold the expression objects themselves, so they are matched by reference.
            .ToDictionary(p => p.expression, p => p.variable, (IEqualityComparer<ReferenceExpression>)ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Renders <paramref name="expression"/> for a Docker Compose file: parameter references become <c>${VAR}</c>
    /// placeholders whose values are written to <c>.env</c>, and literal text is escaped.
    /// </summary>
    /// <param name="composeService">The Docker Compose service resource the value is written for, which names the placeholders.</param>
    /// <param name="expression">The expression.</param>
    /// <returns>The text for the compose file.</returns>
    public static string ToComposeExpression(DockerComposeServiceResource composeService, ReferenceExpression expression)
    {
        if (expression.IsConditional)
        {
            throw new NotSupportedException("Conditional expressions are not supported in Traefik routes.");
        }

        var args = expression.ValueProviders
            .Select(object (valueProvider) => valueProvider switch
            {
                // The ParameterResource overload honors the parameter's configured environment name.
                ParameterResource parameter => parameter.AsEnvironmentPlaceholder(composeService),
                ReferenceExpression nested => ToComposeExpression(composeService, nested),
                IManifestExpressionProvider provider => provider.AsEnvironmentPlaceholder(composeService),
                _ => throw new NotSupportedException($"The value '{valueProvider}' can't be used in a Traefik route at publish time."),
            })
            .ToArray();

        return string.Format(CultureInfo.InvariantCulture, EscapeLiteral(expression.Format), args);
    }

    /// <summary>
    /// Escapes <c>$</c> as <c>$$</c>, so Docker Compose doesn't interpolate a literal value.
    /// </summary>
    /// <param name="value">Literal text.</param>
    /// <returns>The text with every <c>$</c> doubled.</returns>
    public static string EscapeLiteral(string value) => value.Replace("$", "$$", StringComparison.Ordinal);

    /// <summary>
    /// Escapes <c>$</c> in the literal text of the arguments added from <paramref name="start"/> on, so Docker Compose
    /// doesn't interpolate it.
    /// </summary>
    /// <param name="args">The arguments of a container at publish time.</param>
    /// <param name="start">The index of the first argument to escape, so arguments added by other callbacks keep theirs.</param>
    /// <remarks>
    /// Docker Compose interpolates <c>$VAR</c> and <c>${VAR}</c> in <c>command</c> too, and the Compose publisher only
    /// escapes the commands of shell-form containers, so a literal such as <c>ops$team@example.com</c> would lose
    /// <c>$team</c>, and <c>${HOME}</c> would take the deploying machine's value. Parameters, endpoints, and other
    /// values become placeholders that Compose must still fill in, so only literal text is escaped.
    /// https://docs.docker.com/reference/compose-file/interpolation/
    /// </remarks>
    public static void EscapeArguments(IList<object> args, int start)
    {
        for (var i = start; i < args.Count; i++)
        {
            args[i] = args[i] switch
            {
                string text => EscapeLiteral(text),
                ReferenceExpression expression => EscapeLiterals(expression),
                var other => other,
            };
        }
    }

    /// <summary>
    /// Returns an expression whose literal text, including the text of nested expressions and of both branches of a
    /// conditional, has every <c>$</c> doubled. Value providers are kept.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The escaped expression.</returns>
    public static ReferenceExpression EscapeLiterals(ReferenceExpression expression)
    {
        if (expression.IsConditional)
        {
            return ReferenceExpression.CreateConditional(
                expression.Condition!,
                expression.MatchValue!,
                EscapeLiterals(expression.WhenTrue!),
                EscapeLiterals(expression.WhenFalse!));
        }

        // Format is a composite format string, for example `--email={0}@{{x}}`: literal text with `{{` and `}}` for
        // braces, and `{n}` for value provider n. String formats such as `uri` are kept in StringFormats, not in the
        // placeholder. ReferenceExpressionBuilder.AppendLiteral escapes braces again.
        var format = expression.Format;
        var builder = new ReferenceExpressionBuilder();
        var literal = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c is '{' or '}' && i + 1 < format.Length && format[i + 1] == c)
            {
                literal.Append(c);
                i++;
            }
            else if (c == '{')
            {
                var end = format.IndexOf('}', i);
                var index = int.Parse(format.AsSpan(i + 1, end - i - 1), CultureInfo.InvariantCulture);
                var provider = expression.ValueProviders[index];

                AppendLiteral();
                builder.AppendValueProvider(
                    provider is ReferenceExpression nested ? EscapeLiterals(nested) : provider,
                    index < expression.StringFormats.Count ? expression.StringFormats[index] : null);
                i = end;
            }
            else
            {
                literal.Append(c);
            }
        }

        AppendLiteral();
        return builder.Build();

        void AppendLiteral()
        {
            if (literal.Length > 0)
            {
                builder.AppendLiteral(EscapeLiteral(literal.ToString()));
                literal.Clear();
            }
        }
    }

    private static void AddInstanceLabel(IDictionary<string, string> labels, TraefikResource traefik)
    {
        // An external Traefik isn't started by Aspire, so it has no constraint that reads the label.
        if (traefik.PublishExternal is null)
        {
            labels[$"{InstanceLabelPrefix}{traefik.Name}"] = "true";
        }
    }

    private static string GetTargetPort(Service service, string variableName, EndpointReference endpoint, string routerName)
    {
        if (service.Environment.Remove(variableName, out var port) && !string.IsNullOrEmpty(port))
        {
            return port;
        }

        if (endpoint.EndpointAnnotation.TargetPort is int targetPort)
        {
            return targetPort.ToString(CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"The container port of endpoint '{endpoint.EndpointName}' on resource '{endpoint.Resource.Name}' can't be determined for the Traefik route '{routerName}'.");
    }

    private static void JoinNetwork(Service service, TraefikRouteDefaults defaults)
    {
        if (defaults.Network is { } network)
        {
            // The external Traefik reaches the service over the shared network. A service on several networks
            // needs `traefik.docker.network`, or Traefik may pick an address on a network it isn't attached to.
            if (!service.Networks.Contains(network))
            {
                service.Networks.Add(network);
            }

            service.Labels["traefik.docker.network"] = network;
        }
    }
}
