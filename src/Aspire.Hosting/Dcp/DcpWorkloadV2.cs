// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Tracks an app-model workload independently of its replaceable V2 API object.
/// </summary>
internal sealed class DcpWorkloadV2(IResource modelResource, CustomResource dcpResource) : IRenderedModelResource, IResourceReference
{
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IResource ModelResource { get; } = modelResource;
    public CustomResource DcpResource { get; set; } = dcpResource;
    public string DcpResourceName => DcpResource.Metadata.Name;
    public string DcpResourceKind => DcpResource.Kind;
    public SemaphoreSlim SerializedOpSemaphore { get; } = new(1, 1);
    public Task Initialized => _initialized.Task;
    public bool Created { get; set; }
    public bool ExplicitStart { get; set; } = modelResource.HasAnnotationOfType<ExplicitStartupAnnotation>();
    public List<DcpServiceV2> Services { get; } = [];

    public string ResourceType => DcpResource switch
    {
        V2.LogicalContainer => KnownResourceTypes.Container,
        V2.LogicalContainerExec => KnownResourceTypes.ContainerExec,
        V2.LogicalProcess => ModelResource.GetResourceType(),
        _ => throw new InvalidOperationException($"Unexpected V2 workload '{DcpResource.Kind}'.")
    };

    public void MarkInitialized() => _initialized.TrySetResult();
    public void Dispose() => SerializedOpSemaphore.Dispose();
}

/// <summary>
/// Associates a V2 logical service with an app-model endpoint and its workload port name.
/// </summary>
internal sealed class DcpServiceV2(
    IResource resource,
    EndpointAnnotation endpoint,
    V2.LogicalService service,
    string portName) : AppResource<V2.LogicalService>(service)
{
    public IResource ModelResource { get; } = resource;
    public EndpointAnnotation Endpoint { get; } = endpoint;
    public string PortName { get; } = portName;
    public V2.LogicalService? HostService { get; set; }
}
