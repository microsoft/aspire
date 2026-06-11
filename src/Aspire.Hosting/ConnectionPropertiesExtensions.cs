// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding connection properties to resources.
/// </summary>
public static class ConnectionPropertiesExtensions
{
    /// <summary>
    /// Combines two sets of connection properties, with properties from the additional set overriding those in the source set in case of key conflicts.
    /// </summary>
    /// <param name="source">The resource that exposes the base connection properties.</param>
    /// <param name="additional">The additional connection properties to merge into the values supplied by <paramref name="source"/>.</param>
    /// <returns>A sequence that contains the combined set of connection properties with duplicate keys resolved in favor of <paramref name="additional"/>.</returns>
    [AspireExportIgnore(Reason = "Connection property merging is an internal helper and is not part of the ATS surface.")]
    public static IEnumerable<KeyValuePair<string, ReferenceExpression>> CombineProperties(this IResourceWithConnectionString source, IEnumerable<KeyValuePair<string, ReferenceExpression>> additional)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(additional);

        var dict = new Dictionary<string, ReferenceExpression>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in source.GetConnectionProperties())
        {
            dict[kv.Key] = kv.Value;
        }

        foreach (var kv in additional)
        {
            dict[kv.Key] = kv.Value;
        }

        return dict.AsEnumerable();
    }
}
