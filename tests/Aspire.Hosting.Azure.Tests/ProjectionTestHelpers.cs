// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Tests;

internal static class ProjectionTestHelpers
{
    internal static void AssertProjection<T>(IResourceBuilder<T> owner, ContainerResource projection)
        where T : class, IResource
    {
        Assert.Same(
            projection,
            Assert.Single(owner.ApplicationBuilder.Resources, resource => resource.Name == owner.Resource.Name));
        Assert.True(owner.ApplicationBuilder.Resources.Contains(owner.Resource));
        Assert.True(owner.ApplicationBuilder.Resources.Contains(projection));
        Assert.Same(projection, owner.Resource.AsContainer());
        Assert.Same(owner.Resource, projection.GetOwnerOrSelf());
        Assert.Same(owner.Resource.Annotations, projection.Annotations);
    }
}
