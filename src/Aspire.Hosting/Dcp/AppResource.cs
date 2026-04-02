// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using System.Diagnostics;

namespace Aspire.Hosting.Dcp;

internal interface IAppResource : IDisposable
{
    CustomResource DcpResource { get; }
    string DcpResourceName { get; }
    string DcpResourceKind { get; }
    SemaphoreSlim SerializedOpSemaphore { get; }
    Task Initialized { get; }

    void MarkInitialized();
}

[DebuggerDisplay("DcpResourceName = {DcpResourceName}, DcpResourceKind = {DcpResourceKind}")]
internal class AppResource<TDcpResource> : IAppResource, IDisposable, IEquatable<AppResource<TDcpResource>> where TDcpResource : CustomResource, IKubernetesStaticMetadata
{
    public TDcpResource DcpResource { get; }
    public string DcpResourceName => DcpResource.Metadata.Name;
    public string DcpResourceKind => TDcpResource.ObjectKind;

    // Semaphore to serialize operations on this resource. For example, it can be used to ensure 
    // that resources are not restarted concurrently, nor can they be restarted before their initial setup is complete.
    public SemaphoreSlim SerializedOpSemaphore { get; } = new SemaphoreSlim(1, 1);

    public Task Initialized => _initializedTcs.Task;

    private readonly TaskCompletionSource _initializedTcs;

    CustomResource IAppResource.DcpResource => DcpResource;

    public AppResource(TDcpResource dcpResource)
    {
        DcpResource = dcpResource;
        _initializedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public bool Equals(AppResource<TDcpResource>? other)
    {
        if (other is null)
        {
            return false;
        }
        var dr = DcpResource;
        var odr = other.DcpResource;
        return dr.GetType().Equals(odr.GetType()) &&
            dr.Metadata.Name == odr.Metadata.Name &&
            dr.Metadata.NamespaceProperty == odr.Metadata.NamespaceProperty;
    }

    public void MarkInitialized()
    {
        _initializedTcs.TrySetResult();
    }

    public void Dispose()
    {
        SerializedOpSemaphore.Dispose();
    }
}

[DebuggerDisplay("ModelResource = {ModelResource}, DcpResourceName = {DcpResourceName}, DcpResourceKind = {DcpResourceKind}")]
internal class RenderedModelResource<TDcpResource> : AppResource<TDcpResource>, IResourceReference where TDcpResource : CustomResource, IKubernetesStaticMetadata
{
    public IResource ModelResource { get; }

    public RenderedModelResource(IResource modelResource, TDcpResource dcpResource) : base(dcpResource)
    {
        ModelResource = modelResource;
    }

    public virtual List<ServiceWithModelResource> ServicesProduced { get; } = [];
    public virtual List<ServiceWithModelResource> ServicesConsumed { get; } = [];
}

internal sealed class ServiceWithModelResource : RenderedModelResource<Service>
{
    public Service Service => DcpResource;
    public EndpointAnnotation EndpointAnnotation { get; }

    public override List<ServiceWithModelResource> ServicesProduced
    {
        get { throw new InvalidOperationException("Service resources do not produce any services"); }
    }
    public override List<ServiceWithModelResource> ServicesConsumed
    {
        get { throw new InvalidOperationException("Service resources do not consume any services"); }
    }

    public ServiceWithModelResource(IResource modelResource, Service service, EndpointAnnotation sba) : base(modelResource, service)
    {
        EndpointAnnotation = sba;
    }
}

internal interface IResourceReference
{
    IResource ModelResource { get; }
    string DcpResourceName { get; }
}
