// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Specifies the Orleans provider type for a resource.
/// </summary>
/// <param name="providerType">The Orleans provider type to use for the resource.</param>
/// <param name="options">Configuration options to be set for this resource's type.</param>
public sealed class OrleansProviderTypeAnnotation(string providerType, IReadOnlyDictionary<string, string>? options = null) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Orleans provider type to use for the resource.
    /// </summary>
    public string ProviderType { get; } = ValidateProviderType(providerType);

    /// <summary>
    /// Gets the configuration options to be set for this resource's type.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; } = options;

    private static string ValidateProviderType(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);

        return providerType;
    }
}
