// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// For containers, it replaces OTLP endpoint environment variable value with a reference to dashboard OTLP ingestion endpoint.
/// </summary>
/// <remarks>
/// In run mode, the dashboard plays the role of an OTLP collector, but the dashboard resource is added dynamically,
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

        if (!context.EnvironmentVariables.TryGetValue(KnownOtelConfigNames.ExporterOtlpEndpoint, out _))
        {
            // If the OTLP endpoint is not set, do not try to set it.
            return;
        }

        var model = executionContext.ServiceProvider.GetService<DistributedApplicationModel>();
        var resourceNetwork = resource.GetDefaultResourceNetwork();

        var resolved = OtlpConfigurationExtensions.ResolveOtlpEndpointFromDashboard(model, oea.RequiredProtocol, resourceNetwork);
        if (resolved is null)
        {
            return;
        }

        var endpointReference = resolved.Value.Endpoint;
        var vpc = new ValueProviderContext { ExecutionContext = executionContext, Caller = resource, Network = resourceNetwork };
        var url = await endpointReference.GetValueAsync(vpc, cancellationToken).ConfigureAwait(false);
        Debug.Assert(url is not null, $"We should be able to get a URL value from the reference dashboard endpoint '{endpointReference.EndpointName}'");
        if (url is not null)
        {
            context.EnvironmentVariables[KnownOtelConfigNames.ExporterOtlpEndpoint] = url;
        }
    }
}
