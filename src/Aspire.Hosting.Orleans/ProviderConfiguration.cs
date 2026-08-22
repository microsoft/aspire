// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Configuration for an Orleans provider.
/// </summary>
internal sealed class ProviderConfiguration(string providerType, string? serviceKey = null, IReadOnlyDictionary<string, string>? options = null, IResourceBuilder<IResourceWithConnectionString>? resource = null) : IProviderConfiguration
{
    private const string AdoNetProviderType = "AdoNet";
    private readonly string _providerType = ValidateProviderType(providerType);
    private readonly IReadOnlyDictionary<string, string>? _options = ValidateOptions(providerType, options);

    private static string GetProviderType(IResourceBuilder<IResourceWithConnectionString> resourceBuilder, out IReadOnlyDictionary<string, string>? options)
    {
        string providerType;

        if (resourceBuilder.Resource.TryGetAnnotationsOfType<OrleansProviderTypeAnnotation>(out var annotations) && annotations.FirstOrDefault() is OrleansProviderTypeAnnotation annotation)
        {
            providerType = annotation.ProviderType;
            options = annotation.Options;
        }
        else
        {
            const string resource = "Resource";
            var resourceType = resourceBuilder.Resource.GetType().Name;

            // Use a simple transformation to get the provider type: remove the "Resource" suffix if it exists.
            providerType = resourceType.EndsWith(resource, StringComparison.Ordinal) ? resourceType[..^resource.Length] : resourceType;
            options = null;
        }

        return providerType;
    }

    private static IReadOnlyDictionary<string, string>? ValidateOptions(string providerType, IReadOnlyDictionary<string, string>? options)
    {
        if (providerType.Equals(AdoNetProviderType, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(options, nameof(options));

            if (!options.TryGetValue("Invariant", out var invariant))
            {
                throw new InvalidOperationException("Orleans ADO.NET providers require an invariant. Configure it by calling WithOrleansAdoNetInvariant on the resource builder.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(invariant, nameof(invariant));
        }

        return options;
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
        var providerType = GetProviderType(resourceBuilder, out var options);

        return new(providerType, serviceKey, options, resourceBuilder);
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

        if (_options is not null)
        {
            foreach (var option in _options)
            {
                resourceBuilder.WithEnvironment($"Orleans__{envVarPrefix}__{option.Key}", option.Value);
            }
        }

        if (resource is not null)
        {
            resourceBuilder.WithReference(resource);
        }
    }
}
