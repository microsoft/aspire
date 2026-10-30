// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Identifies a Bicep module that creates tenant-scoped resources outside Azure Resource Manager.
/// </summary>
/// <remarks>
/// This annotation describes the created resources, not the ARM deployment scope. Annotated resources participate
/// in tenant resource suffix configuration. Provisioning, deployment cancellation, and local state management
/// remain available, but ARM resource inspection, relocation, deletion, and drift probes do not apply.
/// Resource-group deletion leaves these resources and their cached identification outputs intact, and reports
/// them for provider-specific cleanup.
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class TenantScopedResourceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes an annotation describing the tenant-scoped objects created by a module.
    /// </summary>
    /// <param name="resourceIdOutputs">
    /// A mapping from provider resource types to Bicep output names containing their object IDs.
    /// Only non-secret identification outputs should be included because they are reported in deletion results.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resourceIdOutputs"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a resource type or output name is empty.</exception>
    /// <example>
    /// <code lang="csharp">
    /// resource.Annotations.Add(new TenantScopedResourceAnnotation(new Dictionary&lt;string, string&gt;
    /// {
    ///     ["Microsoft.Graph/applications"] = "objectId",
    ///     ["Microsoft.Graph/servicePrincipals"] = "servicePrincipalId"
    /// }));
    /// </code>
    /// </example>
    public TenantScopedResourceAnnotation(IReadOnlyDictionary<string, string> resourceIdOutputs)
    {
        ArgumentNullException.ThrowIfNull(resourceIdOutputs);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (resourceType, outputName) in resourceIdOutputs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
            outputs.Add(resourceType, outputName);
        }

        ResourceIdOutputs = new ReadOnlyDictionary<string, string>(outputs);
    }

    /// <summary>
    /// Gets the provider resource types and the output names used to identify objects left after resource-group deletion.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResourceIdOutputs { get; }
}
