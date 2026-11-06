// <copyright file="ServiceUrlResourceBuilderExtensions.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// <see cref="WithServiceUrl{T, TTarget}"/> — declares that a client addresses a target
/// service via a custom environment variable instead of Aspire service discovery, in a way
/// the chaos mesh can see.
/// </summary>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public static class ServiceUrlResourceBuilderExtensions
{
    private const string HttpEndpointName = "http";

    /// <summary>
    /// Binds <paramref name="environmentVariable"/> on the client to <paramref name="target"/>'s
    /// <c>http</c> endpoint AND records a <see cref="ServiceUrlBindingAnnotation"/> the chaos mesh
    /// reads, so the client→target edge is auto-routed through its mesh proxy.
    /// </summary>
    /// <typeparam name="T">The client resource type.</typeparam>
    /// <typeparam name="TTarget">The target resource type (must expose endpoints).</typeparam>
    /// <param name="builder">The client resource builder.</param>
    /// <param name="environmentVariable">The environment variable the client reads the target URL from (e.g. <c>WORKSPACES__SERVICEBASEURL</c>).</param>
    /// <param name="target">The target service the client calls.</param>
    /// <returns>The same client resource builder for chaining.</returns>
    /// <remarks>
    /// This replaces a raw <c>WithEnvironment(environmentVariable, target.GetEndpoint("http"))</c>.
    /// Functionally identical when the mesh is absent (the env var still points at the target),
    /// but when <see cref="ChaosProxyMeshExtensions.AddChaosProxyMesh"/> runs it discovers the
    /// recorded binding and overrides the env var with the proxy URL — no per-AppHost re-route
    /// block required.
    /// </remarks>
    public static IResourceBuilder<T> WithServiceUrl<T, TTarget>(
        this IResourceBuilder<T> builder,
        string environmentVariable,
        IResourceBuilder<TTarget> target)
        where T : IResourceWithEnvironment
        where TTarget : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(environmentVariable);
        ArgumentNullException.ThrowIfNull(target);

        builder.WithEnvironment(environmentVariable, target.GetEndpoint(HttpEndpointName));
        builder.Resource.Annotations.Add(new ServiceUrlBindingAnnotation(environmentVariable, target.Resource));
        return builder;
    }
}
