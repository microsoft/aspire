// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript.Tests;

internal static class ResourceBuilderTestExtensions
{
    internal static DockerfileBuildAnnotation GetDockerfileBuildAnnotation<TResource>(this IResourceBuilder<TResource> builder)
        where TResource : IResource
    {
        return builder.Resource.Annotations
            .OfType<DockerfileBuildAnnotation>()
            .Single();
    }
}
