// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for <see cref="OrleansServiceClient"/>.
/// </summary>
public static class OrleansServiceClientExtensions
{
    /// <summary>
    /// Adds an Orleans client to the resource.
    /// </summary>
    /// <param name="builder">The builder on which add the Orleans service builder.</param>
    /// <param name="orleansServiceClient">The Orleans service client, containing clustering, etc.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="InvalidOperationException">Clustering has not been configured.</exception>
    [AspireExport("withOrleansClientReference", Description = "Adds an Orleans client reference to a resource")]
    public static IResourceBuilder<T> WithReference<T>(
        this IResourceBuilder<T> builder,
        OrleansServiceClient orleansServiceClient)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        return builder.WithOrleansReference(orleansServiceClient.Service, isSilo: false);
    }
}
