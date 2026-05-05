// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.Dcp;

internal static class ResourceSnapshotTestHelpers
{
    internal static CustomResourceSnapshot CreatePreviousSnapshot()
    {
        return new()
        {
            ResourceType = "resource",
            Properties = []
        };
    }

    internal static ResourcePropertySnapshot GetProperty(CustomResourceSnapshot snapshot, string name)
    {
        return Assert.Single(snapshot.Properties, p => p.Name == name);
    }

    internal static IEnumerable<T> GetEnumerablePropertyValue<T>(CustomResourceSnapshot snapshot, string name)
    {
        var property = GetProperty(snapshot, name);
        return Assert.IsAssignableFrom<IEnumerable<T>>(property.Value);
    }
}
