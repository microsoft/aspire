// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius;
using Aspire.Hosting.Radius.Models;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for configuring Radius publish behavior on resources.
/// </summary>
public static class RadiusPublishExtensions
{
    /// <summary>
    /// Configures how a resource is published as a Radius resource type instance.
    /// Stores a <see cref="RadiusResourceCustomization"/> annotation on the resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">A callback to configure the <see cref="RadiusResourceCustomization"/>.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when both <see cref="RadiusResourceCustomization.Recipe"/> and
    /// <see cref="ResourceProvisioning.Manual"/> are set, which is mutually exclusive.
    /// </exception>
    // RunSyncOnBackgroundThread = true: the configure callback mutates the customization
    // synchronously here. Polyglot AppHost runtimes (TypeScript, Python, ...) marshal sync
    // delegates over RPC and would deadlock if invoked on the dispatcher thread. The opt-in
    // tells the runtime to dispatch the callback on a worker thread instead. See ASPIREEXPORT010.
    [AspireExport(Description = "Customizes how a resource is published as a Radius resource type", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> PublishAsRadiusResource<T>(
        this IResourceBuilder<T> builder,
        Action<RadiusResourceCustomization> configure)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var customization = new RadiusResourceCustomization();
        configure(customization);

        // Authoritative validation: Recipe + Manual is mutually exclusive (FR-027)
        if (customization.Recipe is not null && customization.Provisioning == ResourceProvisioning.Manual)
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' sets both a custom recipe and manual provisioning, " +
                "which is mutually exclusive. Use either a recipe (automatic provisioning) or manual provisioning, not both.");
        }

        builder.Resource.Annotations.Add(new RadiusResourceCustomizationAnnotation(customization));
        return builder;
    }

    /// <summary>
    /// Registers a callback to customize the Azure.Provisioning AST before Bicep compilation.
    /// The callback runs after all <see cref="PublishAsRadiusResource{T}"/> customizations are applied (last-write-wins).
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="configure">A callback to mutate the <see cref="RadiusInfrastructureOptions"/>.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para><b>C#-only escape hatch.</b> This method and its
    /// <see cref="RadiusInfrastructureOptions"/> parameter expose the
    /// Azure.Provisioning AST (the typed <c>*Construct</c> classes) and are
    /// therefore not surfaced to polyglot AppHosts — the construct graph is not
    /// ATS-compatible. Polyglot AppHosts customize per-resource via
    /// <see cref="PublishAsRadiusResource{T}"/> instead. The construct shapes are
    /// still evolving with the Radius preview schemas; treat them as a preview
    /// surface that may change shape across releases.</para>
    /// <para><b>Identifier-rename propagation.</b> When a callback renames a construct's
    /// <c>BicepIdentifier</c>, the builder automatically re-resolves only the <c>.id</c>
    /// cross-references it originally created that targeted the renamed construct
    /// (app &#x2192; env, resource-type instance &#x2192; app/env, container &#x2192; app,
    /// container connections &#x2192; target). The rewire is scoped to identifier changes,
    /// so direct edits a callback makes to any reference value are preserved
    /// (last-write-wins).</para>
    /// <para><b>Callback-added constructs.</b> Constructs a callback adds itself are not
    /// tracked and will not be rewired when a builder-created parent is renamed — set
    /// such references explicitly in the callback that creates the new construct.</para>
    /// </remarks>
    // [AspireExportIgnore]: the callback parameter carries the Azure.Provisioning AST
    // (RadiusInfrastructureOptions + the typed *Construct classes) which is not ATS-
    // compatible — polyglot AppHost runtimes cannot marshal that surface over RPC.
    // The escape-hatch stays C#-only by design; polyglot AppHosts use PublishAsRadiusResource
    // for per-resource customization instead. See ASPIREEXPORT008.
    [AspireExportIgnore]
    public static IResourceBuilder<RadiusEnvironmentResource> ConfigureRadiusInfrastructure(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        Action<RadiusInfrastructureOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Resource.Annotations.Add(new RadiusInfrastructureConfigureAnnotation(configure));
        return builder;
    }
}
