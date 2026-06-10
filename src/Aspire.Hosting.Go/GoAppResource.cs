// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

/// <summary>
/// Represents a Go application resource in the distributed application model.
/// </summary>
/// <remarks>
/// <para>
/// This resource allows Go applications to run as part of a distributed application. The resource
/// manages the Go toolchain, working directory, and lifecycle of the Go application.
/// </para>
/// <para>
/// Go applications can expose HTTP endpoints, communicate with other services, and participate
/// in service discovery like other Aspire resources. They support automatic OpenTelemetry
/// instrumentation when configured with the appropriate Go packages.
/// </para>
/// </remarks>
/// <example>
/// Add a Go web application to the distributed application model:
/// <code lang="csharp">
/// var builder = DistributedApplication.CreateBuilder(args);
///
/// var api = builder.AddGoApp("api", "../go-api")
///     .WithHttpEndpoint(port: 8080);
///
/// builder.Build().Run();
/// </code>
/// </example>
/// <param name="name">The name of the resource in the application model.</param>
/// <param name="workingDirectory">The working directory for the Go application, typically the directory containing <c>go.mod</c>.</param>
[AspireExport(ExposeProperties = true)]
public class GoAppResource(string name, string workingDirectory)
    : ExecutableResource(name, "go", workingDirectory), IResourceWithServiceDiscovery, IContainerFilesDestinationResource;
