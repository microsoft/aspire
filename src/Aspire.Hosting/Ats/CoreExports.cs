// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Ats;

/// <summary>
/// Core ATS (Aspire Type System) exports for polyglot app host support.
/// </summary>
/// <remarks>
/// <para>
/// This class defines the foundational capabilities that enable non-.NET languages (TypeScript, Python, etc.)
/// to build Aspire distributed applications. These exports form the stable API surface for polyglot app hosts.
/// </para>
/// <para>
/// <strong>Design Principles:</strong>
/// <list type="bullet">
///   <item><description>Capabilities are the contract - not CLR method signatures</description></item>
///   <item><description>Handles replace direct object references - guest code never sees .NET types</description></item>
///   <item><description>Capability IDs use format {Package}/{Method}</description></item>
///   <item><description>.NET implementation details are hidden behind a stable polyglot surface</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Capability Naming Convention:</strong> <c>{Package}/{operation}</c>
/// </para>
/// <para>
/// <strong>Usage from TypeScript:</strong>
/// <code>
/// // Create builder and add resources
/// const builder = await client.invoke("Aspire.Hosting/createBuilder", {});
/// const redis = await client.invoke("Aspire.Hosting/addContainer", { builder, name: "cache", image: "redis:latest" });
/// await client.invoke("Aspire.Hosting/withEnvironment", { resource: redis, name: "REDIS_MODE", value: "standalone" });
///
/// // Build and run
/// const app = await client.invoke("Aspire.Hosting/build", { builder });
/// await client.invoke("Aspire.Hosting/run", { app });
/// </code>
/// </para>
/// </remarks>
internal static class CoreExports
{
    #region Application Lifecycle

    // Note: createBuilder is now on DistributedApplication.CreateBuilder
    // Note: build is now on IDistributedApplicationBuilder.Build via [AspireExport("build")]
    // Note: run is now on DistributedApplication.RunAsync via [AspireExport("run")]
    // Note: ExecutionContext, Configuration, Environment, and AppHostDirectory are accessed via property getters
    // on IDistributedApplicationBuilder which has [AspireExport(ExposeProperties = true)].

    // Note: getEndpoint is now on ResourceBuilderExtensions.GetEndpoint
    // Note: withReference is now on ResourceBuilderExtensions.WithReference

    #endregion

    #region Container Configuration

    /// <summary>
    /// Adds a volume to a container resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Volumes persist data across container restarts. Named volumes are managed
    /// by Docker/Podman and stored in a system-managed location.
    /// </para>
    /// <para>
    /// <strong>Why this wrapper exists:</strong> The original <c>ContainerResourceBuilderExtensions.WithVolume</c>
    /// has parameter order <c>(name?, target, isReadOnly)</c> where the optional <c>name</c> comes first.
    /// This wrapper reorders parameters to <c>(target, name?, isReadOnly)</c> so the required <c>target</c>
    /// parameter comes first, providing a better API for polyglot consumers.
    /// </para>
    /// </remarks>
    /// <param name="resource">The container resource builder handle.</param>
    /// <param name="target">The mount path inside the container.</param>
    /// <param name="name">The volume name. If null, an anonymous volume is created.</param>
    /// <param name="isReadOnly">Whether the volume is read-only.</param>
    /// <returns>The same resource builder handle for chaining.</returns>
    [AspireExport(Description = "Adds a volume")]
    public static IResourceBuilder<ContainerResource> WithVolume(
        this IResourceBuilder<ContainerResource> resource,
        string target,
        string? name = null,
        bool isReadOnly = false)
    {
        return ContainerResourceBuilderExtensions.WithVolume(resource, name, target, isReadOnly);
    }

    #endregion

    #region Resource Information

    /// <summary>
    /// Gets the name of the resource from a builder.
    /// </summary>
    /// <remarks>
    /// <strong>Why this wrapper exists:</strong> This capability accesses a nested property
    /// (<c>resource.Resource.Name</c>) which requires a wrapper method. There is no single
    /// .NET method that returns just the resource name that could be annotated directly.
    /// </remarks>
    /// <param name="resource">The resource builder handle.</param>
    /// <returns>The resource name.</returns>
    [AspireExport(Description = "Gets the resource name")]
    public static string GetResourceName(this IResourceBuilder<IResource> resource)
    {
        return resource.Resource.Name;
    }

    #endregion

    #region Parameters

    // Note: withDescription is now on ParameterResourceBuilderExtensions.WithDescription

    #endregion
}
