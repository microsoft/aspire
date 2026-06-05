// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents a resource for the Aspire Dashboard.
/// This resource is used to visualize telemetry data in the Aspire Hosting environment.
/// </summary>
/// <param name="name">The name of the Aspire Dashboard resource.</param>
[AspireExport(ExposeProperties = true)]
public class DockerComposeAspireDashboardResource(string name) : ContainerResource(name)
{
    /// <summary>
    /// Gets the primary endpoint of the Aspire Dashboard.
    /// </summary>
    public EndpointReference PrimaryEndpoint => new(this, "http");

    /// <summary>
    /// Gets the OTLP gRPC endpoint for telemetry data.
    /// </summary>
    public EndpointReference OtlpGrpcEndpoint => new(this, "otlp-grpc");
}
