// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

internal interface IResourceProjection
{
    IResource Owner { get; }
}

internal interface IResourceProjectionSource
{
    bool TrySelect(
        DistributedApplicationExecutionContext executionContext,
        out IResource? projection);
}

internal sealed class ResourceProjectionAnnotation(IResourceProjectionSource source) : IResourceAnnotation
{
    public IResourceProjectionSource Source { get; } = source;
}

internal sealed class OperationResourceProjectionSource : IResourceProjectionSource
{
    private readonly DistributedApplicationOperation _operation;
    private readonly IResource _projection;

    public OperationResourceProjectionSource(
        DistributedApplicationOperation operation,
        IResource projection)
    {
        _operation = operation;
        _projection = projection;
    }

    public bool TrySelect(
        DistributedApplicationExecutionContext executionContext,
        out IResource? projection)
    {
        projection = executionContext.Operation == _operation ? _projection : null;
        return projection is not null;
    }
}

internal sealed class ContainerResourceProjection<TOwner>(TOwner owner)
    : ContainerResource(owner.Name), IResourceProjection
    where TOwner : IResource
{
    public TOwner Owner { get; } = owner;

    public override ResourceAnnotationCollection Annotations { get; } = new(owner.Annotations);

    IResource IResourceProjection.Owner => Owner;
}
