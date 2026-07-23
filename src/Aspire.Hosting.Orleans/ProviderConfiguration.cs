// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Configuration for an Orleans provider.
/// </summary>
internal sealed class ProviderConfiguration(string providerType, string? serviceKey = null, string? invariant = null, IResourceBuilder<IResourceWithConnectionString>? resource = null) : IProviderConfiguration
{
    private const string AdoNetProviderType = "AdoNet";
    private readonly string _providerType = ValidateProviderType(providerType);
    private readonly string? _invariant = providerType.Equals(AdoNetProviderType, StringComparison.Ordinal) ? ValidateInvariant(invariant) : null;

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
            providerType = resourceType.EndsWith(resource, StringComparison.Ordinal) ? resourceType[..^resource.Length] : resourceType;
        }

        return providerType;
    }

    private static string ValidateInvariant(string? invariant)
    {
        if (invariant is null)
        {
            throw new ArgumentNullException(nameof(invariant), "Orleans ADO.NET providers require an invariant. Configure it by calling WithOrleansAdoNetInvariant on the resource builder.");
        }

        if (string.IsNullOrWhiteSpace(invariant))
        {
            throw new ArgumentException("Orleans ADO.NET providers require an invariant. Configure it by calling WithOrleansAdoNetInvariant on the resource builder.", nameof(invariant));
        }

        return invariant;
    }

    private static string ValidateProviderType(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);

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
        string? invariant = null;

        if (resourceBuilder.Resource.TryGetAnnotationsOfType<OrleansAdoNetInvariantAnnotation>(out var annotations))
        {
            invariant = annotations.FirstOrDefault()?.Invariant;
        }

        return new(providerType, serviceKey, invariant, resourceBuilder);
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
        resourceBuilder.WithEnvironment($"Orleans__{envVarPrefix}__ProviderType", _providerType);
        if (!string.IsNullOrEmpty(serviceKey))
        {
            // The ADO.NET providers use ConnectionName instead of ServiceKey.
            var key = _providerType.Equals(AdoNetProviderType, StringComparison.Ordinal)
                ? "ConnectionName"
                : "ServiceKey";

            resourceBuilder.WithEnvironment($"Orleans__{envVarPrefix}__{key}", serviceKey);
        }

        if (!string.IsNullOrEmpty(_invariant))
        {
            resourceBuilder.WithEnvironment($"Orleans__{envVarPrefix}__Invariant", _invariant);
        }

        if (resource is not null)
        {
            resourceBuilder.WithReference(resource);
        }
    }
}
