// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a collection of resources.
/// </summary>
public interface IResourceCollection : IList<IResource>
{
    /// <summary>
    /// Attempts to find a resource by its name.
    /// </summary>
    /// <param name="name">The name of the resource to find.</param>
    /// <param name="resource">When this method returns, contains the resource with the specified name, if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a resource with the specified name was found; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// The resource name comparison is case-insensitive.
    /// </remarks>
    bool TryGetByName(string name, [NotNullWhen(true)] out IResource? resource)
    {
        foreach (var item in this)
        {
            if (string.Equals(item.Name, name, StringComparisons.ResourceName))
            {
                resource = item;
                return true;
            }
        }

        resource = null;
        return false;
    }
}

