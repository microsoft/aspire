// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java.Tests;

internal static class TestEndpointAllocator
{
    /// <summary>
    /// Endpoints are allocated by the orchestrator at run time. Environment variable evaluation waits on
    /// that allocation, so a test that never starts the application has to supply it or the evaluation
    /// never returns.
    /// </summary>
    public static void AllocateEndpoints(IResource resource)
    {
        foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080, targetPortExpression: "8080");
        }
    }
}
