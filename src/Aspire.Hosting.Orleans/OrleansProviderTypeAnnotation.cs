// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Specifies the Orleans provider type for a resource.
/// </summary>
/// <param name="providerType">The Orleans provider type to use for the resource.</param>
public sealed class OrleansProviderTypeAnnotation(string providerType) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Orleans provider type to use for the resource.
    /// </summary>
    public string ProviderType { get; } = ValidateProviderType(providerType);

    private static string ValidateProviderType(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);

        return providerType;
    }
}
