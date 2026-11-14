// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Maps a Helm values path to a source that can be resolved by a deployment publisher.
/// </summary>
/// <typeparam name="T">The type of the deferred source or template.</typeparam>
/// <remarks>
/// The path consists of three separate keys: <see cref="Section"/>, <see cref="ResourceKey"/>,
/// and <see cref="ValueKey"/>. Treat each key as a literal dictionary key, not as a dotted path.
/// </remarks>
public sealed class HelmValueMapping<T>
{
    internal HelmValueMapping(string section, string resourceKey, string valueKey, T value)
    {
        Section = section;
        ResourceKey = resourceKey;
        ValueKey = valueKey;
        Value = value;
    }

    /// <summary>
    /// Gets the top-level values section, such as <c>parameters</c>, <c>config</c>, or <c>secrets</c>.
    /// </summary>
    public string Section { get; }

    /// <summary>
    /// Gets the resource key within the values section.
    /// </summary>
    public string ResourceKey { get; }

    /// <summary>
    /// Gets the value key within the resource's values.
    /// </summary>
    public string ValueKey { get; }

    /// <summary>
    /// Gets the original deferred source or the unresolved Helm template.
    /// </summary>
    public T Value { get; }
}
