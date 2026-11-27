// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik.Tests;

/// <summary>
/// Simulates the endpoint allocation that DCP performs in run mode, so value resolution works without starting
/// the app.
/// </summary>
internal static class EndpointAllocation
{
    /// <summary>
    /// Allocates <paramref name="endpointName"/> on the localhost network at <paramref name="hostPort"/>, and on the
    /// default container network at <paramref name="containerHost"/>:<paramref name="containerPort"/>.
    /// </summary>
    public static void Allocate(IResource resource, string endpointName, int hostPort, string containerHost, int containerPort)
    {
        var annotation = resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == endpointName);

        annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, "localhost", hostPort);
        annotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
            new AllocatedEndpoint(
                annotation,
                containerHost,
                containerPort,
                EndpointBindingMode.SingleAddress,
                containerPort.ToString(CultureInfo.InvariantCulture),
                KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
    }
}
