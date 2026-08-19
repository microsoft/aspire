// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for controlling the Azure region that an individual Azure resource is deployed to.
/// </summary>
/// <remarks>
/// By default every Azure resource in an Aspire application is deployed to the location configured on the
/// <see cref="AzureEnvironmentResource"/>. Setting a location on an individual resource makes it possible to
/// spread a single application across multiple Azure regions, which is the basis of the regional stamp
/// (deployment stamp) topology. See <see href="https://learn.microsoft.com/azure/architecture/patterns/deployment-stamp"/>.
/// </remarks>
public static class AzureResourceLocationExtensions
{
    /// <summary>
    /// Sets the Azure region that this resource is deployed to.
    /// </summary>
    /// <typeparam name="T">The type of the Azure resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="location">The Azure region name, for example <c>eastus</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// The location overrides the region configured on the <see cref="AzureEnvironmentResource"/> for this
    /// resource only. Resources of different regions can coexist in a single resource group, so no additional
    /// resource group configuration is required.
    /// </para>
    /// <example>
    /// Deploy two Azure Container Apps environments to different regions:
    /// <code lang="C#">
    /// var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
    /// var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Use the polyglot withLocation export that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> WithLocation<T>(this IResourceBuilder<T> builder, string location)
        where T : AzureBicepResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(location);

        return WithLocationCore(builder, location);
    }

    /// <summary>
    /// Sets the Azure region that this resource is deployed to using a parameter.
    /// </summary>
    /// <typeparam name="T">The type of the Azure resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="location">A parameter that supplies the Azure region name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <example>
    /// Deploy a Container Apps environment to a region supplied at deployment time:
    /// <code lang="C#">
    /// var region = builder.AddParameter("primary-region");
    /// var aca = builder.AddAzureContainerAppEnvironment("aca").WithLocation(region);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Use the polyglot withLocation export that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> WithLocation<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> location)
        where T : AzureBicepResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(location);

        return WithLocationCore(builder, location.Resource);
    }

    /// <summary>
    /// Sets the Azure region that this resource is deployed to.
    /// </summary>
    /// <typeparam name="T">The type of the Azure resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="location">The Azure region name as a string or parameter resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withAzureResourceLocation", MethodName = "withLocation")]
    internal static IResourceBuilder<T> WithLocationForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object location)
        where T : AzureBicepResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(location);

        if (location is not (string or ParameterResource))
        {
            throw new ArgumentException(
                $"The location must be a string or a {nameof(ParameterResource)}, but was '{location.GetType()}'.",
                nameof(location));
        }

        if (location is string { Length: 0 })
        {
            throw new ArgumentException("The location must not be empty.", nameof(location));
        }

        return WithLocationCore(builder, location);
    }

    /// <summary>
    /// Gets the location explicitly configured for the resource, or <see langword="null"/> when the resource
    /// uses the location of the <see cref="AzureEnvironmentResource"/>.
    /// </summary>
    /// <param name="resource">The Azure resource.</param>
    /// <returns>The configured location value, which is either a <see cref="string"/> or a <see cref="ParameterResource"/>.</returns>
    internal static object? GetConfiguredLocation(this AzureBicepResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Only a location placed there by WithLocation counts as configured. The provisioner also writes
        // the environment location into this same slot during deployment (BicepProvisioner
        // PopulateWellKnownParameters), so the annotation is what distinguishes an author's intent from
        // an inferred value.
        return resource.Annotations.OfType<AzureResourceLocationAnnotation>().LastOrDefault()?.Location;
    }

    /// <summary>
    /// Copies the Azure region configured on <paramref name="source"/> onto <paramref name="target"/>, if any.
    /// </summary>
    /// <param name="target">The resource that should adopt the region.</param>
    /// <param name="source">The resource whose configured region is copied.</param>
    /// <remarks>
    /// <para>
    /// Compute environments use this to place the infrastructure they generate — container apps, web sites —
    /// in the same region as the environment itself. Azure requires it: a container app must live in the
    /// region of its managed environment, and a web site in the region of its App Service plan. Without it,
    /// every stamp of an application deployed to several regions would be emitted into the
    /// application-wide region.
    /// </para>
    /// <para>
    /// Only a region set with <c>WithLocation</c> is copied. A region that the provisioner inferred from the
    /// Azure environment is left alone, so that the target keeps resolving to the shared environment region.
    /// </para>
    /// <example>
    /// Propagate the environment's region onto a generated deployment target:
    /// <code lang="C#">
    /// var containerApp = new AzureContainerAppResource(name, configure, resource);
    /// containerApp.InheritLocationFrom(environment);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Infrastructure-generation helper for compute environment authors — not part of the ATS surface.")]
    public static void InheritLocationFrom(this AzureBicepResource target, AzureBicepResource source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        if (source.GetConfiguredLocation() is { } location)
        {
            target.Parameters[AzureBicepResource.KnownParameters.Location] = location;
            target.Annotations.Add(new AzureResourceLocationAnnotation(location));
        }
    }

    private static IResourceBuilder<T> WithLocationCore<T>(IResourceBuilder<T> builder, object location)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[AzureBicepResource.KnownParameters.Location] = location;

        // Track the override separately from the parameter so later code can tell an explicit choice apart
        // from the location the provisioner infers from the Azure environment. Repeated calls append, and
        // readers take the last annotation, so the most recent call wins.
        builder.Resource.Annotations.Add(new AzureResourceLocationAnnotation(location));

        return builder;
    }
}
