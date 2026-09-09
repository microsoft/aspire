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

    #region Compute Configuration

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
    /// <remarks>
    /// <para>
    /// This capability deliberately does not expose the C# <c>env</c> parameter. A container always
    /// receives <paramref name="target"/> as its effective volume path in every mode, so the C#
    /// convenience overload is exactly equivalent to <c>withVolume(target, name).withEnvironment(env, target)</c>
    /// in a polyglot AppHost. Keeping the exported parameter list frozen matters because the Rust
    /// generator emits optional capability parameters positionally and has no overloading, so appending
    /// a parameter here would be a source-breaking change for existing Rust AppHosts. Projects and
    /// executables genuinely need the parameter because their run-mode path is computed by the host,
    /// and they get it through the separate withProjectVolume/withExecutableVolume capabilities.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<ContainerResource> WithVolume(
        this IResourceBuilder<ContainerResource> resource,
        string target,
        string? name = null,
        bool isReadOnly = false)
    {
        return VolumeResourceBuilderExtensions.WithVolumeCore(resource, name, target, isReadOnly, env: null);
    }

    /// <summary>
    /// Adds a volume to a project resource.
    /// </summary>
    /// <param name="resource">The project resource builder handle.</param>
    /// <param name="target">The mount path inside the published container.</param>
    /// <param name="name">The volume name.</param>
    /// <param name="env">The environment variable that receives the effective volume path.</param>
    /// <param name="isReadOnly">Whether the published volume is read-only.</param>
    /// <returns>The same project resource builder handle for chaining.</returns>
    [AspireExport("withProjectVolume", MethodName = "withVolume")]
    public static IResourceBuilder<ProjectResource> WithProjectVolumeForPolyglot(
        this IResourceBuilder<ProjectResource> resource,
        string target,
        string name,
        string env,
        bool isReadOnly = false)
    {
        return WithProcessVolume(resource, target, name, isReadOnly, env);
    }

    /// <summary>
    /// Adds a volume to an executable resource.
    /// </summary>
    /// <param name="resource">The executable resource builder handle.</param>
    /// <param name="target">The mount path inside the published container.</param>
    /// <param name="name">The volume name.</param>
    /// <param name="env">The environment variable that receives the effective volume path.</param>
    /// <param name="isReadOnly">Whether the published volume is read-only.</param>
    /// <returns>The same executable resource builder handle for chaining.</returns>
    [AspireExport("withExecutableVolume", MethodName = "withVolume")]
    public static IResourceBuilder<ExecutableResource> WithExecutableVolumeForPolyglot(
        this IResourceBuilder<ExecutableResource> resource,
        string target,
        string name,
        string env,
        bool isReadOnly = false)
    {
        return WithProcessVolume(resource, target, name, isReadOnly, env);
    }

    private static IResourceBuilder<T> WithProcessVolume<T>(
        IResourceBuilder<T> resource,
        string target,
        string name,
        bool isReadOnly,
        string env)
        where T : IComputeResource, IResourceWithEnvironment
    {
        // These exports are the polyglot projection of the public WithVolume<T>(name, target, env, isReadOnly)
        // overload, so they have to reject the same inputs. WithVolumeCore only null-checks target because it is
        // shared with the container overloads, which have accepted an empty target since they shipped.
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(env);

        return VolumeResourceBuilderExtensions.WithVolumeCore(resource, name, target, isReadOnly, env);
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
    [AspireExport]
    public static string GetResourceName(this IResourceBuilder<IResource> resource)
    {
        return resource.Resource.Name;
    }

    #endregion

    #region Project Configuration

    /// <summary>
    /// Includes only the specified project endpoint names in environment-variable injection.
    /// </summary>
    /// <param name="resource">The project resource builder handle.</param>
    /// <param name="endpointNames">The endpoint names to include in environment variables.</param>
    /// <returns>The same project resource builder handle for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<ProjectResource> WithEndpointsInEnvironment(
        this IResourceBuilder<ProjectResource> resource,
        string[] endpointNames)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(endpointNames);

        var includedEndpointNames = endpointNames.ToHashSet(StringComparers.EndpointAnnotationName);

        return global::Aspire.Hosting.ProjectResourceBuilderExtensions.WithEndpointsInEnvironment(
            resource,
            endpoint => includedEndpointNames.Contains(endpoint.Name));
    }

    #endregion

    #region Parameters

    // Note: withDescription is now on ParameterResourceBuilderExtensions.WithDescription

    #endregion
}
