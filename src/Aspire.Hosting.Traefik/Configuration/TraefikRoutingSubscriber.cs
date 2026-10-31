// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Turns the <see cref="TraefikRouteAnnotation"/>s on routed resources into Traefik configuration.
/// </summary>
/// <remarks>
/// Routes are declared on the routed resource, so they are only complete once the whole model is built. This
/// subscriber runs on <see cref="BeforeStartEvent"/>, which <c>aspire publish</c> also raises before the
/// publishing pipeline, and:
/// <list type="bullet">
///   <item>assigns router names and resolves the backend endpoint of every route;</item>
///   <item>in run mode, records the routes on the managed Traefik (written to its dynamic configuration file)
///     and shows the public URLs on the routed resources in the dashboard;</item>
///   <item>at publish time, adds the <c>traefik.*</c> labels to the routed Docker Compose services.</item>
/// </list>
/// </remarks>
internal sealed class TraefikRoutingSubscriber : IDistributedApplicationEventingSubscriber
{
    // The types of the relationships the dashboard shows: from Traefik to its backends, and from a routed resource to
    // the middlewares of its routes.
    private const string RouteRelationshipType = "Route";

    private const string MiddlewareRelationshipType = "Middleware";

    /// <inheritdoc/>
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            ApplyRoutes(@event.Model, executionContext.IsPublishMode);
            return Task.CompletedTask;
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the routes of every routed resource and applies them to Traefik's configuration or the Docker Compose
    /// services. It runs once per application, on <see cref="BeforeStartEvent"/>: it adds annotations (URLs,
    /// relationships, Docker Compose customizations) that a second run would duplicate. Only the generated sign-in
    /// routes and the route lists on Traefik are reset first.
    /// </summary>
    /// <param name="model">The application model.</param>
    /// <param name="isPublishMode">Whether the application is being published, which writes Docker labels instead of Traefik's file configuration.</param>
    public static void ApplyRoutes(DistributedApplicationModel model, bool isPublishMode)
    {
        foreach (var traefik in model.Resources.OfType<TraefikResource>())
        {
            traefik.Routes.Clear();
            traefik.TransportRoutes.Clear();
        }

        AddSignInRoutes(model, isPublishMode);

        // Router names that differ only by case, or by '-' versus '_', are duplicates: see RouterNameComparer.
        var routerNames = new Dictionary<string, (IResource Owner, string Name)>(RouterNameComparer.Instance);
        var publishesToDockerCompose = isPublishMode && model.Resources.OfType<DockerComposeEnvironmentResource>().Any();

        foreach (var traefik in model.Resources.OfType<TraefikResource>())
        {
            if (traefik.DashboardHost is not null)
            {
                routerNames[traefik.DashboardRouterName] = (traefik, traefik.DashboardRouterName);
            }

            if (publishesToDockerCompose)
            {
                ApplyPublishFileConfiguration(traefik);
            }
        }

        foreach (var resource in model.Resources)
        {
            var annotations = resource.Annotations.OfType<TraefikRouteAnnotation>().ToList();
            if (annotations.Count == 0)
            {
                continue;
            }

            var resourceWithEndpoints = GetRoutedResource(resource);
            var deploysToDockerCompose = publishesToDockerCompose && DeploysToDockerCompose(model, resource);
            var routes = new List<TraefikRoute>(annotations.Count);
            var resourceMiddlewares = resource.Annotations.OfType<TraefikMiddlewareAnnotation>().Select(a => a.Middleware).ToList();

            foreach (var middleware in resourceMiddlewares)
            {
                if (!annotations.Any(a => a.Traefik == middleware.Parent))
                {
                    throw new InvalidOperationException($"Resource '{resource.Name}' uses the Traefik middleware '{middleware.Name}' of '{middleware.Parent.Name}', but has no route through '{middleware.Parent.Name}'.");
                }
            }

            for (var i = 0; i < annotations.Count; i++)
            {
                var annotation = annotations[i];
                var routerName = annotation.Options.RouterName ?? (i == 0 ? resource.Name : $"{resource.Name}-{i + 1}");

                ThrowIfInvalidRouterName(resource, routerName);

                if (routerNames.TryGetValue(routerName, out var existing))
                {
                    throw new InvalidOperationException($"The Traefik router name '{routerName}' of resource '{resource.Name}' clashes with '{existing.Name}' of '{existing.Owner.Name}'. Set {nameof(TraefikRouteOptions)}.{nameof(TraefikRouteOptions.RouterName)} to make it unique.");
                }

                routerNames.Add(routerName, (resource, routerName));

                // An external Traefik's resolvers aren't known, but the managed one's are, in run mode too, so a typo
                // shows up before the first deployment.
                if (annotation.Options.CertResolver is { } certResolver && annotation.Traefik.PublishExternal is null)
                {
                    ValidateCertResolver(annotation.Traefik, resource, routerName, certResolver, "HTTP");
                }

                // Traefik only logs a router whose entry point is missing or listens for UDP, and the route answers 404,
                // so check it here like the TCP and UDP routes. An external Traefik's entry points aren't known.
                if (annotation.Options.EntryPoint is { } entryPoint && (!isPublishMode || annotation.Traefik.PublishExternal is null))
                {
                    ValidateEntryPoint(annotation.Traefik, resource, routerName, entryPoint, "HTTP", isUdp: false);
                }

                var endpoint = ResolveEndpoint(resourceWithEndpoints, annotation.Options.EndpointName);
                var middlewares = GetMiddlewares(resource, routerName, annotation, resourceMiddlewares);
                var route = new TraefikRoute(annotation.Traefik, resourceWithEndpoints, routerName, endpoint, annotation.Options, middlewares);

                annotation.Traefik.Routes.Add(route);
                routes.Add(route);

                if (deploysToDockerCompose)
                {
                    if (resource is IResourceWithEnvironment resourceWithEnvironment)
                    {
                        TraefikDockerLabels.AddTargetPortProbe(resourceWithEnvironment, TraefikDockerLabels.GetTargetPortVariableName(route), route.Endpoint);
                    }

                    var defaults = annotation.Traefik.GetRouteDefaults(isPublishMode: true);
                    resource.Annotations.Add(new DockerComposeServiceCustomizationAnnotation(
                        (composeService, service) => TraefikDockerLabels.Apply(composeService, service, route, defaults)));
                }
            }

            if (deploysToDockerCompose)
            {
                var middlewares = routes.SelectMany(r => r.Middlewares).SelectMany(m => m.WithReferences()).Distinct().ToList();

                if (resource is IResourceWithEnvironment resourceWithEnvironment)
                {
                    TraefikDockerLabels.AddMiddlewareValueProbes(resourceWithEnvironment, middlewares);
                }

                // After the route customizations above, which read the probes.
                resource.Annotations.Add(new DockerComposeServiceCustomizationAnnotation((_, service) => TraefikDockerLabels.RemoveProbes(service)));

                JoinExternalNetwork(model, middlewares);
            }

            if (!isPublishMode)
            {
                AddPublicUrls(resource, routes);

                // Show the routes as relationships in the dashboard, from the proxy to its backends.
                foreach (var traefik in routes.Select(r => r.Traefik).Distinct())
                {
                    traefik.Annotations.Add(new ResourceRelationshipAnnotation(resource, RouteRelationshipType));
                }

                // Only middleware resources: the StripPathPrefix middleware isn't in the model.
                foreach (var middleware in routes.SelectMany(r => r.Middlewares).Distinct().Where(m => m.Parent.Middlewares.Contains(m)))
                {
                    resource.Annotations.Add(new ResourceRelationshipAnnotation(middleware, MiddlewareRelationshipType));
                }
            }
        }

        ApplyTransportRoutes(model, isPublishMode, publishesToDockerCompose);
        SetErrorPageServices(model);
    }

    /// <summary>
    /// Routes <c>/oauth2/*</c> on every host protected by an oauth2-proxy middleware to the oauth2-proxy container.
    /// </summary>
    /// <remarks>
    /// The provider redirects the browser back to <c>/oauth2/callback</c> on the host the user first requested, so that
    /// path must reach oauth2-proxy rather than the protected backend, and without passing through the middleware.
    /// </remarks>
    private static void AddSignInRoutes(DistributedApplicationModel model, bool isPublishMode)
    {
        foreach (var resource in model.Resources)
        {
            foreach (var generated in resource.Annotations.OfType<TraefikRouteAnnotation>().Where(a => a.IsSignInRoute).ToList())
            {
                resource.Annotations.Remove(generated);
            }

            if (resource is TraefikMiddlewareResource middleware)
            {
                middleware.ProtectedHosts.Clear();
                middleware.ProtectsPlainHttp = false;
                middleware.ProtectsNonStandardPort = false;
            }
        }

        foreach (var resource in model.Resources.ToList())
        {
            var resourceMiddlewares = resource.Annotations.OfType<TraefikMiddlewareAnnotation>().Select(a => a.Middleware).ToList();

            foreach (var annotation in resource.Annotations.OfType<TraefikRouteAnnotation>().ToList())
            {
                var middlewares = resourceMiddlewares
                    .Where(m => m.Parent == annotation.Traefik)
                    .Concat(annotation.Options.Middlewares)
                    .SelectMany(m => m.WithReferences())
                    .Where(m => m.OAuth2Proxy is not null)
                    .Distinct();

                foreach (var middleware in middlewares)
                {
                    if (annotation.Options.Host is not { } host)
                    {
                        throw new InvalidOperationException($"A Traefik route of resource '{resource.Name}' uses the oauth2-proxy middleware '{middleware.Name}', but has no host. The sign-in callback needs one.");
                    }

                    AddSignInRoute(middleware, annotation.Traefik, host, annotation.Options, isPublishMode);
                }
            }
        }

        foreach (var traefik in model.Resources.OfType<TraefikResource>())
        {
            // An external Traefik's dashboard isn't published (see WithDashboard), so neither is its sign-in route.
            if (traefik.DashboardHost is not { } host || (isPublishMode && traefik.PublishExternal is not null))
            {
                continue;
            }

            foreach (var middleware in traefik.DashboardMiddlewares.SelectMany(m => m.WithReferences()).Where(m => m.OAuth2Proxy is not null).Distinct())
            {
                AddSignInRoute(middleware, traefik, host, new TraefikRouteOptions { Host = host }, isPublishMode);
            }
        }

        static void AddSignInRoute(TraefikMiddlewareResource middleware, TraefikResource traefik, ReferenceExpression host, TraefikRouteOptions protectedRoute, bool isPublishMode)
        {
            var entryPoint = protectedRoute.EntryPoint;
            middleware.ProtectsPlainHttp |= !TraefikUrls.IsSecure(traefik, protectedRoute, isPublishMode);
            middleware.ProtectsNonStandardPort |= isPublishMode && TraefikUrls.GetNonStandardPublishPort(traefik, protectedRoute) is not null;

            // Hosts are compared by their manifest expression, so `api.{domain}` declared twice counts once. The list
            // feeds oauth2-proxy's --whitelist-domain, which doesn't depend on the entry point.
            if (!middleware.ProtectedHosts.Any(h => h.ValueExpression == host.ValueExpression))
            {
                middleware.ProtectedHosts.Add(host);
            }

            // Only middlewares added by AddOAuth2ProxyForwardAuth are passed here, and they all have the container.
            var proxy = middleware.OAuth2Proxy!;

            // The callback comes back on the entry point the user signed in from, so a host routed over both HTTP and
            // HTTPS needs a sign-in route on each. A route without an entry point uses Traefik's default one, so it
            // counts as that entry point and doesn't add a second router with the same rule. The proxy belongs to one
            // middleware, so all its sign-in routes go through the same Traefik.
            // https://oauth2-proxy.github.io/oauth2-proxy/configuration/integrations/traefik
            var defaultEntryPoint = traefik.GetRouteDefaults(isPublishMode).EntryPoint.ValueExpression;
            var signInRoutes = proxy.Annotations.OfType<TraefikRouteAnnotation>().Where(a => a.IsSignInRoute).ToList();

            // Sign-in routes always have a host: AddSignInRoutes checks it before calling this method.
            if (signInRoutes.Any(a => a.Options.Host!.ValueExpression == host.ValueExpression &&
                (a.Options.EntryPoint ?? defaultEntryPoint) == (entryPoint ?? defaultEntryPoint)))
            {
                return;
            }

            proxy.Annotations.Add(new TraefikRouteAnnotation(traefik, new TraefikRouteOptions
            {
                Host = host,
                PathPrefix = middleware.OAuth2ProxyPrefix,
                Priority = TraefikForwardAuthExtensions.SignInRoutePriority,
                EntryPoint = entryPoint,
                RouterName = $"{proxy.Name}-{signInRoutes.Count + 1}",
            }, isSignInRoute: true));
        }
    }

    private static void ApplyPublishFileConfiguration(TraefikResource traefik)
    {
        if (traefik.PublishExternal is not null && traefik.EntryPointMiddlewares.Count > 0)
        {
            throw new InvalidOperationException($"Traefik resource '{traefik.Name}' is published as external, so its entry-point middlewares can't be deployed. Configure them on the external Traefik instead.");
        }

        if (!traefik.HasPublishFileConfiguration)
        {
            return;
        }

        // Endpoint values in the middlewares, such as a forward authentication address, are resolved through
        // environment variables of Traefik's own service, as they are for routed services (see TraefikDockerLabels).
        TraefikDockerLabels.AddMiddlewareValueProbes(traefik, TraefikDynamicConfiguration.GetPublishMiddlewares(traefik));

        traefik.Annotations.Add(new DockerComposeServiceCustomizationAnnotation((composeService, service) =>
        {
            var content = TraefikDynamicConfiguration.GeneratePublish(traefik, composeService, service);
            TraefikDockerLabels.RemoveProbes(service);

            // The config itself is added to the compose file by AddTraefik, which can reach the environment's
            // ConfigureComposeFile; that runs after every service is generated.
            var name = $"{service.Name}-dynamic";
            traefik.PublishFileConfiguration = new(composeService.Parent, name, content);
            service.AddConfig(new ConfigReference
            {
                Source = name,
                Target = $"{TraefikResource.DynamicConfigurationDirectory}/{TraefikDynamicConfiguration.FileName}",
            });
        }));
    }

    private static bool DeploysToDockerCompose(DistributedApplicationModel model, IResource resource)
    {
        // The labels and the temporary variables that carry ports into them only make sense for a Docker Compose
        // service. A resource bound to another compute environment, such as Kubernetes, would keep the variables in
        // its deployment, because only the Docker Compose customizations remove them again.
        if (resource.GetComputeEnvironment() is { } environment)
        {
            return environment is DockerComposeEnvironmentResource;
        }

        // Without an explicit environment the resource goes to the only one; with several, the publishers report the
        // ambiguity themselves, unless all of them are Docker Compose environments.
        return model.Resources.OfType<IComputeEnvironmentResource>().All(e => e is DockerComposeEnvironmentResource);
    }

    private static IResourceWithEndpoints GetRoutedResource(IResource resource)
    {
        if (resource is not IResourceWithEndpoints resourceWithEndpoints)
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' is routed through Traefik but has no endpoints.");
        }

        return resourceWithEndpoints;
    }

    private static void ThrowIfInvalidRouterName(IResource resource, string routerName)
    {
        if (!TraefikNames.IsValidName(routerName))
        {
            throw new InvalidOperationException($"The Traefik router name '{routerName}' of resource '{resource.Name}' is invalid. Use letters, digits, '-' and '_' only.");
        }
    }

    private static void ValidateCertResolver(TraefikResource traefik, IResource resource, string routerName, string certResolver, string kind)
    {
        if (traefik.FindAcmeResolver(certResolver) is null)
        {
            throw new InvalidOperationException($"The Traefik {kind} route '{routerName}' of resource '{resource.Name}' uses the certificate resolver '{certResolver}', which '{traefik.Name}' doesn't have. Add it with WithLetsEncrypt.");
        }
    }

    private static void ValidateEntryPoint(TraefikResource traefik, IResource resource, string routerName, string entryPoint, string kind, bool isUdp)
    {
        if (!traefik.HasEntryPoint(entryPoint))
        {
            throw new InvalidOperationException($"The Traefik {kind} route '{routerName}' of resource '{resource.Name}' uses the entry point '{entryPoint}', which '{traefik.Name}' doesn't have. Add it with WithEntryPoint, or WithLetsEncrypt for '{TraefikResource.WebSecureEntryPointName}'.");
        }

        if (isUdp != traefik.CustomEntryPoints.Any(e => e.Name == entryPoint && e.IsUdp))
        {
            throw new InvalidOperationException(isUdp
                ? $"The Traefik UDP route '{routerName}' of resource '{resource.Name}' needs a UDP entry point, but '{entryPoint}' listens for TCP. Add one with WithEntryPoint(name, targetPort, scheme: \"udp\")."
                : $"The Traefik {kind} route '{routerName}' of resource '{resource.Name}' needs a TCP entry point, but '{entryPoint}' listens for UDP.");
        }
    }

    private static EndpointReference ResolveEndpoint(IResourceWithEndpoints resource, string? endpointName)
    {
        if (endpointName is not null)
        {
            var endpoint = resource.GetEndpoint(endpointName);
            if (!endpoint.Exists)
            {
                throw new InvalidOperationException($"Resource '{resource.Name}' has no endpoint named '{endpointName}' to route Traefik traffic to.");
            }

            return endpoint;
        }

        var http = resource.GetEndpoint("http");
        if (http.Exists)
        {
            return http;
        }

        var httpEndpoints = resource.Annotations.OfType<EndpointAnnotation>()
            .Where(e => e.UriScheme is "http" or "https")
            .ToList();

        return httpEndpoints.Count switch
        {
            1 => resource.GetEndpoint(httpEndpoints[0].Name),
            0 => throw new InvalidOperationException($"Resource '{resource.Name}' has no HTTP endpoint to route Traefik traffic to."),
            _ => throw new InvalidOperationException($"Resource '{resource.Name}' has several HTTP endpoints and none is named 'http'. Set {nameof(TraefikRouteOptions)}.{nameof(TraefikRouteOptions.EndpointName)} to choose one."),
        };
    }

    private static List<TraefikMiddlewareResource> GetMiddlewares(IResource resource, string routerName, TraefikRouteAnnotation annotation, List<TraefikMiddlewareResource> resourceMiddlewares)
    {
        foreach (var middleware in annotation.Options.Middlewares)
        {
            if (middleware.Parent != annotation.Traefik)
            {
                throw new InvalidOperationException($"The Traefik route '{routerName}' of resource '{resource.Name}' goes through '{annotation.Traefik.Name}', but its middleware '{middleware.Name}' belongs to '{middleware.Parent.Name}'.");
            }
        }

        // Resource-wide middlewares (usually authentication or security headers) run before the route's own ones
        // (usually path rewrites), so they see the request as the client sent it. A middleware listed in both
        // places runs once, at its first position.
        var middlewares = resourceMiddlewares
            .Where(m => m.Parent == annotation.Traefik)
            .Concat(annotation.Options.Middlewares)
            .Distinct()
            .ToList();

        if (annotation.Options.StripPathPrefix)
        {
            // Last, so the route's own middlewares still see the path the route matched on. The middleware only
            // exists in Traefik's configuration, not as a resource.
            var name = $"{routerName}-strip";
            if (annotation.Traefik.Middlewares.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"The Traefik route '{routerName}' of resource '{resource.Name}' strips its path prefix with a middleware named '{name}', but '{annotation.Traefik.Name}' already has a middleware with that name.");
            }

            var strip = new TraefikMiddlewareResource(name, TraefikMiddlewareTypesExtensions.StripPrefixType, annotation.Traefik);
            // WithTraefikRoute only accepts StripPathPrefix with a path prefix.
            strip.Options.Set("prefixes", new object[] { annotation.Options.PathPrefix! });
            middlewares.Add(strip);
        }

        return middlewares;
    }

    private static void JoinExternalNetwork(DistributedApplicationModel model, List<TraefikMiddlewareResource> middlewares)
    {
        // An external Traefik reaches the services a middleware calls, such as a forward authentication server, over
        // its shared network, so those services join it too. Routed services join it in TraefikDockerLabels.
        foreach (var middleware in middlewares)
        {
            if (middleware.Parent.GetRouteDefaults(isPublishMode: true).Network is not { } network)
            {
                continue;
            }

            foreach (var referenced in middleware.Options.GetReferencedResources())
            {
                if (!model.Resources.Contains(referenced) || referenced.Annotations.OfType<TraefikNetworkAnnotation>().Any(a => a.Network == network))
                {
                    continue;
                }

                referenced.Annotations.Add(new TraefikNetworkAnnotation(network));
                referenced.Annotations.Add(new DockerComposeServiceCustomizationAnnotation((_, service) =>
                {
                    if (!service.Networks.Contains(network))
                    {
                        service.Networks.Add(network);
                    }
                }));
            }
        }
    }

    private static void AddPublicUrls(IResource resource, List<TraefikRoute> routes)
    {
        resource.Annotations.Add(new ResourceUrlsCallbackAnnotation(async context =>
        {
            var publicUrls = new List<ResourceUrlAnnotation>();
            foreach (var route in routes)
            {
                if (route.Options.Host is null)
                {
                    // A path-only route matches any host, so there is no single public URL to show.
                    continue;
                }

                var expression = TraefikUrls.Build(route.Traefik, route.Options, isPublishMode: false);
                var value = await expression.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                publicUrls.Add(new ResourceUrlAnnotation
                {
                    Url = value,
                    DisplayText = $"{value} (Traefik)",
                });
            }

            // Without a URL through Traefik, the direct URLs are the only addresses the resource table can show.
            if (publicUrls.Count == 0)
            {
                return;
            }

            // Show the resource as the address it answers on through Traefik. The direct localhost URLs bypass
            // the proxy (and any middleware on the route), so keep them in the details pane only.
            foreach (var url in context.Urls)
            {
                url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
            }

            context.Urls.AddRange(publicUrls);
        }));
    }

    /// <summary>
    /// Resolves the TCP and UDP routes, like <see cref="ApplyRoutes"/> does for HTTP routes.
    /// </summary>
    private static void ApplyTransportRoutes(DistributedApplicationModel model, bool isPublishMode, bool publishesToDockerCompose)
    {
        // TCP and UDP routers live in their own namespaces in Traefik (`tcp.routers`, `udp.routers`), so a name only has
        // to be unique within its protocol. The key `tcp.api` maps to the environment name TCP_API, like the variables
        // of TCP routes (see RouterNameComparer).
        var routerNames = new Dictionary<string, (IResource Owner, string Name)>(RouterNameComparer.Instance);

        foreach (var resource in model.Resources)
        {
            var annotations = resource.Annotations
                .Where(a => a is TraefikTcpRouteAnnotation or TraefikUdpRouteAnnotation)
                .ToList();
            if (annotations.Count == 0)
            {
                continue;
            }

            var resourceWithEndpoints = GetRoutedResource(resource);
            var deploysToDockerCompose = publishesToDockerCompose && DeploysToDockerCompose(model, resource);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var routes = new List<TraefikTransportRoute>(annotations.Count);

            foreach (var annotation in annotations)
            {
                var (traefik, tcp, entryPoint, endpointName, routerName) = annotation switch
                {
                    // WithTraefikTcpRoute requires an entry point, so it is set.
                    TraefikTcpRouteAnnotation t => (t.Traefik, t.Options, t.Options.EntryPoint!, t.Options.EndpointName, t.Options.RouterName),
                    TraefikUdpRouteAnnotation u => (u.Traefik, (TraefikTcpRouteOptions?)null, u.EntryPoint, u.EndpointName, (string?)null),
                    _ => throw new UnreachableException(),
                };

                var protocol = TraefikTransportRoute.GetProtocol(tcp);
                var index = counts[protocol] = counts.GetValueOrDefault(protocol) + 1;
                routerName ??= index == 1 ? resource.Name : $"{resource.Name}-{index}";

                ThrowIfInvalidRouterName(resource, routerName);

                if (!routerNames.TryAdd($"{protocol}.{routerName}", (resource, routerName)))
                {
                    var existing = routerNames[$"{protocol}.{routerName}"];
                    throw new InvalidOperationException($"The Traefik {protocol.ToUpperInvariant()} router name '{routerName}' of resource '{resource.Name}' clashes with '{existing.Name}' of '{existing.Owner.Name}'. Set {nameof(TraefikTcpRouteOptions)}.{nameof(TraefikTcpRouteOptions.RouterName)} to make it unique.");
                }

                // An external Traefik's entry points and resolvers aren't known when published.
                if (!isPublishMode || traefik.PublishExternal is null)
                {
                    ValidateEntryPoint(traefik, resource, routerName, entryPoint, protocol.ToUpperInvariant(), isUdp: tcp is null);

                    if (tcp?.CertResolver is { } certResolver)
                    {
                        ValidateCertResolver(traefik, resource, routerName, certResolver, "TCP");
                    }
                }

                var endpoint = ResolveTransportEndpoint(resourceWithEndpoints, endpointName, protocol);
                var route = new TraefikTransportRoute(traefik, resourceWithEndpoints, routerName, endpoint, entryPoint, tcp);

                traefik.TransportRoutes.Add(route);
                routes.Add(route);

                if (deploysToDockerCompose)
                {
                    if (resource is IResourceWithEnvironment resourceWithEnvironment)
                    {
                        TraefikDockerLabels.AddTargetPortProbe(resourceWithEnvironment, TraefikDockerLabels.GetTargetPortVariableName(route), endpoint);
                    }

                    var defaults = traefik.GetRouteDefaults(isPublishMode: true);
                    resource.Annotations.Add(new DockerComposeServiceCustomizationAnnotation(
                        (composeService, service) => TraefikDockerLabels.Apply(composeService, service, route, defaults)));
                }
            }

            if (!isPublishMode)
            {
                foreach (var traefik in routes.Select(r => r.Traefik).Distinct())
                {
                    if (!traefik.Annotations.OfType<ResourceRelationshipAnnotation>().Any(a => a.Resource == resource && a.Type == RouteRelationshipType))
                    {
                        traefik.Annotations.Add(new ResourceRelationshipAnnotation(resource, RouteRelationshipType));
                    }
                }
            }
        }
    }

    private static EndpointReference ResolveTransportEndpoint(IResourceWithEndpoints resource, string? endpointName, string protocol)
    {
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToList();
        EndpointAnnotation annotation;

        if (endpointName is not null)
        {
            annotation = endpoints.FirstOrDefault(e => StringComparers.EndpointAnnotationName.Equals(e.Name, endpointName))
                ?? throw new InvalidOperationException($"Resource '{resource.Name}' has no endpoint named '{endpointName}' to route Traefik traffic to.");
        }
        else if (endpoints.FirstOrDefault(e => StringComparers.EndpointAnnotationName.Equals(e.Name, protocol)) is { } named)
        {
            annotation = named;
        }
        else if (endpoints is [var single])
        {
            annotation = single;
        }
        else
        {
            var found = endpoints.Count == 0 ? "no endpoints" : $"several endpoints and none is named '{protocol}'";
            throw new InvalidOperationException($"Resource '{resource.Name}' has {found}. Set the endpoint name of its Traefik {protocol.ToUpperInvariant()} route.");
        }

        // A TCP route can target any TCP endpoint, including an HTTP one, but datagrams can only go to a UDP port.
        var expected = protocol == TraefikTransportRoute.UdpProtocol ? ProtocolType.Udp : ProtocolType.Tcp;
        if (annotation.Protocol != expected)
        {
            throw new InvalidOperationException($"The endpoint '{annotation.Name}' of resource '{resource.Name}' uses {annotation.Protocol}, so it can't receive a Traefik {protocol.ToUpperInvariant()} route.");
        }

        return resource.GetEndpoint(annotation.Name);
    }

    private static void SetErrorPageServices(DistributedApplicationModel model)
    {
        foreach (var middleware in model.Resources.OfType<TraefikMiddlewareResource>())
        {
            if (middleware.ErrorPageResource is not { } errorPages)
            {
                continue;
            }

            // The service of a route has the router's name. The first route of the resource is used.
            var route = middleware.Parent.Routes.FirstOrDefault(r => r.Resource == errorPages)
                ?? throw new InvalidOperationException($"The Traefik middleware '{middleware.Name}' serves error pages from '{errorPages.Name}', which has no route through '{middleware.Parent.Name}'. Call WithTraefikRoute on it.");

            middleware.Options.Set("service", route.RouterName);
        }
    }

    /// <summary>
    /// Compares router names by the environment variable name they map to, for example <c>api-v1</c> and <c>API_V1</c>
    /// both to <c>API_V1</c>.
    /// </summary>
    /// <remarks>
    /// Traefik matches router names case-sensitively in the file provider, but Docker label keys are compared
    /// case-insensitively by some tooling. The router name is also part of the <c>ASPIRE_TRAEFIK_*</c> variables that
    /// carry backend addresses and ports (see <see cref="TraefikNames.ToEnvironmentName"/>), so two names that map to
    /// the same variable would overwrite each other's value.
    /// </remarks>
    private sealed class RouterNameComparer : IEqualityComparer<string>
    {
        public static RouterNameComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && TraefikNames.ToEnvironmentName(x) == TraefikNames.ToEnvironmentName(y));

        public int GetHashCode(string obj) => TraefikNames.ToEnvironmentName(obj).GetHashCode(StringComparison.Ordinal);
    }
}
