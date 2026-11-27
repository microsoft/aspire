// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Traefik middlewares and applying them to routes.
/// </summary>
public static partial class TraefikMiddlewareExtensions
{
    /// <summary>
    /// Adds a Traefik middleware of any type, configured with <c>WithOption</c>.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the resource, which is also the Traefik middleware name.</param>
    /// <param name="type">The Traefik middleware type as Traefik names it, for example <c>stripPrefix</c> or <c>inFlightReq</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Use it for middlewares that have no dedicated method. Apply the middleware with <c>WithTraefikMiddleware</c> on a
    /// routed resource, or per route with <see cref="TraefikRouteOptions.Middlewares"/>. In run mode the middleware is
    /// written to Traefik's dynamic configuration; at publish time it becomes <c>traefik.http.middlewares.*</c> labels
    /// on the services that use it, so it also works with an external Traefik.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/overview/"/> for
    /// the types and their options.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik");
    ///
    /// var limit = traefik.AddMiddleware("limit", "inFlightReq")
    ///     .WithOption("amount", 10);
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithTraefikRoute(traefik, "api.dev.localhost")
    ///     .WithTraefikMiddleware(limit);
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddMiddleware(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string type)
    {
        ArgumentNullException.ThrowIfNull(builder);
        TraefikNames.ThrowIfInvalidName(name, "middleware name", nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (!MiddlewareTypeRegex().IsMatch(type))
        {
            throw new ArgumentException($"The middleware type '{type}' is invalid. Use the type name from the Traefik documentation, for example 'stripPrefix'.", nameof(type));
        }

        // Added to the model first: AddResource throws for a duplicate name, which must not leave the middleware on Traefik.
        var middleware = builder.ApplicationBuilder.AddResource(new TraefikMiddlewareResource(name, type, builder.Resource))
            .WithIconName("Filter")
            .ExcludeFromManifest()
            // Shown under Traefik in the dashboard so it is visible which middlewares exist. Nothing runs, so the
            // state is static.
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TraefikMiddleware",
                State = KnownResourceStates.Active,
                Properties = [new("traefik.middleware.type", type)],
            });

        builder.Resource.Middlewares.Add(middleware.Resource);

        return middleware;
    }

    /// <summary>
    /// Sets an option of a Traefik middleware.
    /// </summary>
    /// <param name="builder">The middleware resource builder.</param>
    /// <param name="path">The option name, with dots for nested options, for example <c>customRequestHeaders.X-Script-Name</c>.</param>
    /// <param name="value">The value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Setting the same option again replaces the value. Option names are case-insensitive, as in Traefik.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOption export, which accepts every supported value type.")]
    public static IResourceBuilder<TraefikMiddlewareResource> WithOption(this IResourceBuilder<TraefikMiddlewareResource> builder, string path, string value) =>
        SetOption(builder, path, value, nameof(value));

    /// <inheritdoc cref="WithOption(IResourceBuilder{TraefikMiddlewareResource}, string, string)"/>
    /// <remarks>
    /// The expression is resolved from Traefik's point of view in run mode. At publish time parameters in it become
    /// <c>${VAR}</c> placeholders in the generated <c>.env</c> file.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOption export, which accepts every supported value type.")]
    public static IResourceBuilder<TraefikMiddlewareResource> WithOption(this IResourceBuilder<TraefikMiddlewareResource> builder, string path, ReferenceExpression value) =>
        SetOption(builder, path, value, nameof(value));

    /// <inheritdoc cref="WithOption(IResourceBuilder{TraefikMiddlewareResource}, string, string)"/>
    /// <remarks>
    /// At publish time the parameter becomes a <c>${VAR}</c> placeholder in the generated <c>.env</c> file.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOption export, which accepts every supported value type.")]
    public static IResourceBuilder<TraefikMiddlewareResource> WithOption(this IResourceBuilder<TraefikMiddlewareResource> builder, string path, IResourceBuilder<ParameterResource> value) =>
        SetOption(builder, path, value is null ? null : ReferenceExpression.Create($"{value}"), nameof(value));

    /// <inheritdoc cref="WithOption(IResourceBuilder{TraefikMiddlewareResource}, string, string)"/>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOption export, which accepts every supported value type.")]
    public static IResourceBuilder<TraefikMiddlewareResource> WithOption(this IResourceBuilder<TraefikMiddlewareResource> builder, string path, bool value) =>
        SetOption(builder, path, value, nameof(value));

    /// <inheritdoc cref="WithOption(IResourceBuilder{TraefikMiddlewareResource}, string, string)"/>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOption export, which accepts every supported value type.")]
    public static IResourceBuilder<TraefikMiddlewareResource> WithOption(this IResourceBuilder<TraefikMiddlewareResource> builder, string path, long value) =>
        SetOption(builder, path, value, nameof(value));

    /// <summary>
    /// Sets a list option of a Traefik middleware, for example the <c>prefixes</c> of <c>stripPrefix</c>.
    /// </summary>
    /// <param name="builder">The middleware resource builder.</param>
    /// <param name="path">The option name, with dots for nested options.</param>
    /// <param name="values">The items of the list.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// At publish time the items are joined with commas in a Docker label, so a literal item can't contain a comma.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOption export, which accepts every supported value type.")]
    public static IResourceBuilder<TraefikMiddlewareResource> WithOption(this IResourceBuilder<TraefikMiddlewareResource> builder, string path, IEnumerable<string> values) =>
        SetListOption(builder, path, values, nameof(values));

    /// <inheritdoc cref="WithOption(IResourceBuilder{TraefikMiddlewareResource}, string, IEnumerable{string})"/>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withOption export, which accepts every supported value type.")]
    public static IResourceBuilder<TraefikMiddlewareResource> WithOption(this IResourceBuilder<TraefikMiddlewareResource> builder, string path, IEnumerable<ReferenceExpression> values) =>
        SetListOption(builder, path, values, nameof(values));

    /// <summary>
    /// Applies Traefik middlewares to every route of this resource that goes through the middlewares' Traefik.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="middlewares">The middlewares, in the order Traefik runs them.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The middlewares also apply to routes added after this call. They run before the middlewares of the individual
    /// routes (<see cref="TraefikRouteOptions.Middlewares"/>), so authentication and security headers see the request as
    /// the client sent it, before any path rewrite.
    /// </para>
    /// <para>
    /// The resource must have at least one route through each middleware's Traefik.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithTraefikMiddleware<T>(this IResourceBuilder<T> builder, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(middlewares);

        foreach (var middleware in middlewares)
        {
            ArgumentNullException.ThrowIfNull(middleware, nameof(middlewares));
        }

        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        foreach (var middleware in middlewares)
        {
            builder.WithAnnotation(new TraefikMiddlewareAnnotation(middleware.Resource), ResourceAnnotationMutationBehavior.Append);
        }

        return builder;
    }

    /// <summary>
    /// Sets an option of a Traefik middleware.
    /// </summary>
    /// <param name="builder">The middleware resource builder.</param>
    /// <param name="path">The option name, with dots for nested options, for example <c>customRequestHeaders.X-Script-Name</c>.</param>
    /// <param name="value">The value: a string, a reference expression, a parameter, a boolean, a number, or a list of strings.</param>
    /// <returns>A reference to the middleware resource builder.</returns>
    /// <remarks>
    /// Setting the same option again replaces the value. Option names are case-insensitive, as in Traefik.
    /// </remarks>
    [AspireExport("withOption")]
    internal static IResourceBuilder<TraefikMiddlewareResource> WithOptionPolyglot(
        this IResourceBuilder<TraefikMiddlewareResource> builder,
        string path,
        [AspireUnion(typeof(string), typeof(ReferenceExpression), typeof(IResourceBuilder<ParameterResource>), typeof(bool), typeof(long), typeof(string[]))] object value)
    {
        return value switch
        {
            string text => builder.WithOption(path, text),
            ReferenceExpression expression => builder.WithOption(path, expression),
            IResourceBuilder<ParameterResource> parameter => builder.WithOption(path, parameter),
            bool flag => builder.WithOption(path, flag),
            long number => builder.WithOption(path, number),
            string[] list => builder.WithOption(path, list),
            _ => throw new ArgumentException($"Unsupported type '{value?.GetType().Name}'. Expected a string, a reference expression, a parameter, a boolean, a number, or a list of strings.", nameof(value)),
        };
    }

    private static IResourceBuilder<TraefikMiddlewareResource> SetOption(IResourceBuilder<TraefikMiddlewareResource> builder, string path, object? value, string valueParameterName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value, valueParameterName);

        builder.Resource.Options.Set(path, value);

        return builder;
    }

    private static IResourceBuilder<TraefikMiddlewareResource> SetListOption<T>(IResourceBuilder<TraefikMiddlewareResource> builder, string path, IEnumerable<T>? values, string valuesParameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(values, valuesParameterName);

        object[] items = [.. values];
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("A list item is null.", valuesParameterName);
        }

        return SetOption(builder, path, items, valuesParameterName);
    }

    // Traefik's middleware types are camelCase identifiers, for example `stripPrefixRegex` or `inFlightReq`.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex MiddlewareTypeRegex();
}
