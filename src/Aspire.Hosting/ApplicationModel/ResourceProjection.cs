// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

internal interface IResourceProjection
{
    IResource Owner { get; }
}

internal sealed class ContainerResourceProjection<TOwner>(TOwner owner)
    : ContainerResource(owner.Name), IResourceProjection
    where TOwner : IResource
{
    public TOwner Owner { get; } = owner;

    public override ResourceAnnotationCollection Annotations => Owner.Annotations;

    IResource IResourceProjection.Owner => Owner;
}
