// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Provides the context required to map a single azd <see cref="AzdResource"/> to an Aspire resource.
/// </summary>
[Experimental("ASPIREAZUREAZD001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzdResourceMapContext
{
    internal AzdResourceMapContext(
        IDistributedApplicationBuilder builder,
        string resourceName,
        AzdResource resource,
        AzdEnvironment? environment,
        AzdImportDiagnostics diagnostics)
    {
        Builder = builder;
        ResourceName = resourceName;
        Resource = resource;
        Environment = environment;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the distributed application builder the mapped resource should be added to.
    /// </summary>
    public IDistributedApplicationBuilder Builder { get; }

    /// <summary>
    /// Gets the Aspire resource name to use, already sanitized to a valid resource name.
    /// </summary>
    public string ResourceName { get; }

    /// <summary>
    /// Gets the azd resource being mapped.
    /// </summary>
    public AzdResource Resource { get; }

    /// <summary>
    /// Gets the selected azd environment, when one was loaded from the <c>.azure</c> directory.
    /// </summary>
    public AzdEnvironment? Environment { get; }

    /// <summary>
    /// Gets the diagnostics sink for reporting issues encountered while mapping this resource.
    /// </summary>
    public AzdImportDiagnostics Diagnostics { get; }
}

/// <summary>
/// Maps an azd <c>resources</c> entry to a concrete Aspire resource.
/// </summary>
/// <remarks>
/// Mappers are consulted in order; the first one whose <see cref="CanMap"/> returns <see langword="true"/>
/// is used. Provide custom mappers through <see cref="AzdImportOptions.ResourceMappers"/> to support
/// resource types the built-in mappers do not cover, or to override the default mapping for a type.
/// </remarks>
[Experimental("ASPIREAZUREAZD001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IAzdResourceMapper
{
    /// <summary>
    /// Determines whether this mapper can handle the resource described by <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The resource mapping context.</param>
    /// <returns><see langword="true"/> if this mapper should map the resource; otherwise <see langword="false"/>.</returns>
    bool CanMap(AzdResourceMapContext context);

    /// <summary>
    /// Maps the azd resource to an Aspire resource and adds it to the application model.
    /// </summary>
    /// <param name="context">The resource mapping context.</param>
    /// <returns>
    /// The created resource builder, or <see langword="null"/> if the resource could not be mapped
    /// (in which case the mapper should report a diagnostic via <see cref="AzdResourceMapContext.Diagnostics"/>).
    /// </returns>
    IResourceBuilder<IResource>? Map(AzdResourceMapContext context);
}
