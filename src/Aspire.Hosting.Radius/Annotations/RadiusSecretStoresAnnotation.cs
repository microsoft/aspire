// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Secret-store types are experimental; consumed internally by the integration.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Secrets;

namespace Aspire.Hosting.Radius.Annotations;

/// <summary>
/// Per-environment annotation collecting the consumer wirings that reference the Radius
/// secret stores declared for an environment. The annotation is per-resource and is never
/// shared between environments; it is a no-op signal for the publish/deploy steps — absent
/// it, the byte-for-byte default path is preserved. (Declared stores are discovered from the
/// application model directly, so they are not duplicated here.)
/// </summary>
internal sealed class RadiusSecretStoresAnnotation : IResourceAnnotation
{
    /// <summary>The consumer wirings (recipe-config auth / envSecrets) referencing declared stores.</summary>
    public List<RadiusSecretStoreConsumer> Consumers { get; } = [];

    /// <summary>
    /// Returns the singleton <see cref="RadiusSecretStoresAnnotation"/> on
    /// <paramref name="resource"/>, creating and attaching one if absent.
    /// </summary>
    internal static RadiusSecretStoresAnnotation GetOrAdd(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var existing = resource.Annotations.OfType<RadiusSecretStoresAnnotation>().FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        var created = new RadiusSecretStoresAnnotation();
        resource.Annotations.Add(created);
        return created;
    }
}
