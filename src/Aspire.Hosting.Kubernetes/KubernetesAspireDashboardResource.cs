// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a resource for the Aspire Dashboard deployed into a Kubernetes environment.
/// This resource is used to visualize telemetry data from resources running in the cluster.
/// </summary>
/// <param name="name">The name of the Aspire Dashboard resource.</param>
[AspireExport(ExposeProperties = true)]
public class KubernetesAspireDashboardResource(string name) : ContainerResource(name)
{
    /// <summary>
    /// Gets the primary HTTP endpoint of the Aspire Dashboard UI.
    /// </summary>
    public EndpointReference PrimaryEndpoint => new(this, "http");

    /// <summary>
    /// Gets the OTLP gRPC endpoint for receiving telemetry data.
    /// </summary>
    public EndpointReference OtlpGrpcEndpoint => new(this, "otlp-grpc");
}
