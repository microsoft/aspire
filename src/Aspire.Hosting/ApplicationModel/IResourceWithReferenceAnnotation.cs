// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

internal interface IResourceWithReferenceAnnotation : IResourceAnnotation
{
    bool CanApplyReference(IResource source);

    IResourceBuilder<TDestination> WithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResource source,
        string referenceName)
        where TDestination : IResourceWithEnvironment;
}
