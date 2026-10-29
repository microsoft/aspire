// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides metadata-only discovery of values consumed by a resource.
/// </summary>
internal sealed class ResourceReferenceDiscoveryAnnotation(IEnumerable<object> values) : IResourceAnnotation
{
    public IEnumerable<object> Values { get; } = values;

    public IResourceWithConnectionString? ConnectionPropertiesSource { get; init; }
}
