// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a URL on the host machine. When referenced in a container resource, localhost will be
/// replaced with the configured container host name.
/// </summary>
public record HostUrl(string Url) : IValueProvider, IManifestExpressionProvider
{
    // Goes into the manifest as a value, not an expression
    string IManifestExpressionProvider.ValueExpression => Url;

    // Returns the url
    ValueTask<string?> IValueProvider.GetValueAsync(System.Threading.CancellationToken cancellationToken) => ((IValueProvider)this).GetValueAsync(new(), cancellationToken);

    // Returns the url
    async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        var networkContext = context.GetNetworkIdentifier();

        // HostUrl is a bit of a hack that is not modeled as an expression
        // So in this one case, we need to fix up the container host name 'manually'
        // Internally, this is only used for OTEL_EXPORTER_OTLP_ENDPOINT, but HostUrl
        // is public, so we don't control how it is used

        if (networkContext == KnownNetworkIdentifiers.LocalhostNetwork)
        {
            return new(Url);
        }

        var retval = Url;

        try
        {
            var uri = new UriBuilder(Url);
            if (uri.Host is "localhost" or "127.0.0.1" or "[::1]")
            {
                if (context.ExecutionContext?.IsRunMode == true)
                {
                    // HostUrl isn't modeled as an expression, so we have to find the appropriate allocated endpoint to use manually in the case we're running in a container.
                    // We're given a URL from the point of view of the host, so need to figure how to modify the URL to be correct from the point of view of the container.
                    // This could simply be replacing the hostname, but if the container tunnel is running, we may need to translate the port as well.
                    // Without doing this, we wouldn't be able to resolve the OTEL address correctly from a container as it currently depends on HostUrl rather than the dashboard endpoints.
                    var options = context.ExecutionContext.ServiceProvider.GetRequiredService<IOptions<DcpOptions>>();

                    var infoService = context.ExecutionContext.ServiceProvider.GetRequiredService<IDcpDependencyCheckService>();
                    var dcpInfo = await infoService.GetDcpInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                    // Determine what hostname means that we want to contact the host machine from the container. If using the new tunnel feature, this needs to be the address of the tunnel instance.
                    // Otherwise we want to try and determine the container runtime appropriate hostname (host.docker.internal or host.containers.internal).
                    uri.Host = options.Value.EnableAspireContainerTunnel? KnownHostNames.DefaultContainerTunnelHostName : dcpInfo?.Containers?.ContainerHostName ?? KnownHostNames.DockerDesktopHostBridge;
                    var model = context.ExecutionContext.ServiceProvider.GetService<DistributedApplicationModel>();

                    if (options.Value.EnableAspireContainerTunnel && model is { })
                    {
                        // If we're running with the container tunnel enabled, we need to lookup the port on the tunnel that corresponds to the
                        // target port on the host machine.
                        
                        var targetEndpoint = model.Resources.Where(r => !r.IsContainer())
                            .OfType<IResourceWithEndpoints>()
                            .Select(r =>
                            {
                                // Find if the resource has a host endpoint with a port matching the one from the request
                                if (r.GetEndpoints(KnownNetworkIdentifiers.LocalhostNetwork).FirstOrDefault(ep => ep.Port == uri.Port) is EndpointReference ep)
                                {
                                    // Return the corresponding endpoint for the container network context. This will be used to determine the port to use when connecting from the container to the host machine.
                                    return r.GetEndpoint(ep.EndpointName, networkContext);
                                }

                                return null;
                            })
                            .Where(ep => ep is not null)
                            .FirstOrDefault();

                        if (targetEndpoint is { })
                        {
                            // If we found a container endpoint, remap the requested port
                            uri.Port = targetEndpoint.Port;
                        }
                    }

                    retval = uri.ToString();
                }
            }

            var hasEndingSlash = Url.EndsWith('/');

            // Remove trailing slash if we didn't have one before (UriBuilder always adds one)
            if (!hasEndingSlash && retval.EndsWith('/'))
            {
                retval = retval[..^1];
            }
        }
        catch (UriFormatException)
        {
            // This was a connection string style value instead of a URL. In that case we'll do a simple hostname replacement, but can't do anything about ports.
            var replacementHost = KnownHostNames.DockerDesktopHostBridge;
            if (context.ExecutionContext?.IsRunMode == true)
            {
                var options = context.ExecutionContext.ServiceProvider.GetRequiredService<IOptions<DcpOptions>>();

                var infoService = context.ExecutionContext.ServiceProvider.GetRequiredService<IDcpDependencyCheckService>();
                var dcpInfo = await infoService.GetDcpInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                replacementHost = options.Value.EnableAspireContainerTunnel ? KnownHostNames.DefaultContainerTunnelHostName : dcpInfo?.Containers?.ContainerHostName ?? KnownHostNames.DockerDesktopHostBridge;
            }

            // HostUrl was meant to only be used with valid URLs. However, this was not
            // previously enforced. So we need to handle the case where it's not a valid URL,
            // by falling back to a simple string replacement.
            retval = retval.Replace(KnownHostNames.Localhost, replacementHost, StringComparison.OrdinalIgnoreCase)
                         .Replace("127.0.0.1", replacementHost)
                         .Replace("[::1]", replacementHost);
        }

        return new(retval);
    }
}
