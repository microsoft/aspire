// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Configuration for an Orleans provider.
/// </summary>
internal sealed class ProviderConfiguration(string providerType, string? serviceKey = null, IResourceBuilder<IResourceWithConnectionString>? resource = null) : IProviderConfiguration
{
    private static string GetProviderType(IResourceBuilder<IResourceWithConnectionString> resourceBuilder)
    {
        string providerType;

        if (resourceBuilder.Resource.TryGetAnnotationsOfType<OrleansProviderTypeAnnotation>(out var annotations) && annotations.FirstOrDefault() is OrleansProviderTypeAnnotation annotation)
        {
            providerType = annotation.ProviderType;
        }
        else
        {
            const string resource = "Resource";
            var resourceType = resourceBuilder.Resource.GetType().Name;

            // Use a simple transformation to get the provider type: remove the "Resource" suffix if it exists.
            providerType = resourceType.EndsWith(resource) ? resourceType[..^resource.Length] : resourceType;
        }

        return providerType;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ProviderConfiguration"/>.
    /// </summary>
    /// <param name="resourceBuilder">The resource which this provider configuration represents.</param>
    /// <returns>The new provider configuration.</returns>
    internal static ProviderConfiguration Create(IResourceBuilder<IResourceWithConnectionString> resourceBuilder)
    {
        var serviceKey = resourceBuilder.Resource.Name;
        var providerType = GetProviderType(resourceBuilder);

        return new(providerType, serviceKey, resourceBuilder);
    }

    /// <summary>
    /// Configures the provided resource.
    /// </summary>
    /// <typeparam name="T">The underlying resource builder type.</typeparam>
    /// <param name="resourceBuilder">The resource builder.</param>
    /// <param name="configurationSectionName">The name of the configuration section which this value is being added to.</param>
    public void ConfigureResource<T>(IResourceBuilder<T> resourceBuilder, string configurationSectionName) where T : IResourceWithEnvironment
    {
        var envVarPrefix = configurationSectionName.Replace(":", "__");
        resourceBuilder.WithEnvironment($"Orleans__{envVarPrefix}__ProviderType", providerType);
        if (!string.IsNullOrEmpty(serviceKey))
        {
            resourceBuilder.WithEnvironment($"Orleans__{envVarPrefix}__ServiceKey", serviceKey);
        }

        if (resource is not null)
        {
            resourceBuilder.WithReference(resource);
        }
    }
}
