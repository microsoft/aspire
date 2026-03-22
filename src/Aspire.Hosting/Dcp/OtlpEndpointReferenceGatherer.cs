// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// For containers, it replaces OTLP endpoint environemnt variable value with a reference to dashboard OTLP ingestion endpoint.
/// </summary>
/// <remarks>
/// In run mode, the dashboard plays the role of an OTLP collector, but the dashboard resouce is added dynamically,
/// just before the application started. That is why the OTLP configuration extension methods use configuration only.
/// OTOH, DCP has full model to work with, and can replace the OTLP endpoint environment variables with references
/// to the dashboard OTLP ingestion endpoint. For containers this allows DCP to tunnel these properly into container networks.
/// </remarks>
internal class OtlpEndpointReferenceGatherer : IExecutionConfigurationGatherer
{
    public async ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
    {
        if (!resource.IsContainer() || !resource.TryGetLastAnnotation<OtlpExporterAnnotation>(out var oea))
        {
            // This gatherer is only relevant for container resources that emit OTEL telemetry.
            return;
        }

        if (!context.EnvironmentVariables.TryGetValue(OtlpConfigurationExtensions.OtlpEndpointEnvironmentVariableName, out _))
        {
            // If the OTLP endpoint is not set, do not try to set it.
            return;
        }

        var model = executionContext.ServiceProvider.GetService<DistributedApplicationModel>();
        if (model is null)
        {
            // Tests may not have a full model
            return;
        }

        var dashboardResource = model.Resources.SingleOrDefault(r => StringComparers.ResourceName.Equals(r.Name, KnownResourceNames.AspireDashboard)) as IResourceWithEndpoints;
        if (dashboardResource == null)
        {
            // Most test runs do not include the dashboard, and that's ok. If the dashboard is not present, do not try to set the OTLP endpoint.
            return;
        }

        if (!dashboardResource.TryGetEndpoints(out var dashboardEndpoints))
        {
            Debug.Fail("Dashboard does not have any endpoints??");
            return;
        }

        var grpcEndpoint = dashboardEndpoints.FirstOrDefault(e => e.Name == KnownEndpointNames.OtlpGrpcEndpointName);
        var httpEndpoint = dashboardEndpoints.FirstOrDefault(e => e.Name == KnownEndpointNames.OtlpHttpEndpointName);
        var resourceNetwork = resource.GetDefaultResourceNetwork();

        var endpointReference = (oea.RequiredProtocol, grpcEndpoint, httpEndpoint) switch
        {
            (OtlpProtocol.Grpc, not null, _) => new EndpointReference(dashboardResource, grpcEndpoint, resourceNetwork),
            (OtlpProtocol.HttpProtobuf or OtlpProtocol.HttpJson, _, not null) => new EndpointReference(dashboardResource, httpEndpoint, resourceNetwork),
            (_, not null, _) => new EndpointReference(dashboardResource, grpcEndpoint, resourceNetwork),
            (_, _, not null) => new EndpointReference(dashboardResource, httpEndpoint, resourceNetwork),
            _ => null
        };
        Debug.Assert(endpointReference != null, "Dashboard should have at least one matching OTLP endpoint");

        if (endpointReference is not null)
        {
            ValueProviderContext vpc = new() { ExecutionContext = executionContext, Caller = resource, Network = resourceNetwork };
            var url = await endpointReference.GetValueAsync(vpc, cancellationToken).ConfigureAwait(false);
            Debug.Assert(url is not null, $"We should be able to get a URL value from the reference dashboard endpoint '{endpointReference.EndpointName}'");
            if (url is not null)
            {
                context.EnvironmentVariables[OtlpConfigurationExtensions.OtlpEndpointEnvironmentVariableName] = url;
            }
        }
    }
}
