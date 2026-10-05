// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.CloudProviders;

namespace Aspire.Hosting.Radius.Annotations;

/// <summary>
/// Per-environment annotation that carries the configured Azure and/or AWS
/// cloud-provider configuration set via <c>WithAzureProvider</c> /
/// <c>WithAwsProvider</c>. The annotation is mutable and last-write-wins:
/// a second call to the same <c>With…Provider</c> on the same
/// environment overwrites the corresponding slot. The annotation is
/// per-resource and is never shared between environments.
/// </summary>
internal sealed class RadiusCloudProvidersAnnotation : IResourceAnnotation
{
    /// <summary>Configured Azure provider, or <see langword="null"/> if none.</summary>
    public AzureRadiusProviderConfig? Azure { get; set; }

    /// <summary>Configured AWS provider, or <see langword="null"/> if none.</summary>
    public AwsRadiusProviderConfig? Aws { get; set; }

    /// <summary>
    /// Returns the singleton <see cref="RadiusCloudProvidersAnnotation"/> on
    /// <paramref name="resource"/>, creating and attaching one if absent.
    /// </summary>
    internal static RadiusCloudProvidersAnnotation GetOrAdd(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var existing = resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        var created = new RadiusCloudProvidersAnnotation();
        resource.Annotations.Add(created);
        return created;
    }
}
