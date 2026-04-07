// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

internal sealed class DcpResourceState(IDictionary<string, IResource> applicationModel, IEnumerable<IAppResource> appResources)
{
    public readonly ConcurrentDictionary<string, Container> ContainersMap = [];
    public readonly ConcurrentDictionary<string, Executable> ExecutablesMap = [];
    public readonly ConcurrentDictionary<string, ContainerExec> ContainerExecsMap = [];
    public readonly ConcurrentDictionary<string, Service> ServicesMap = [];
    public readonly ConcurrentDictionary<string, Endpoint> EndpointsMap = [];
    public readonly ConcurrentDictionary<(string, string), List<string>> ResourceAssociatedServicesMap = [];

    public IDictionary<string, IResource> ApplicationModel { get; } = applicationModel;
    public IEnumerable<IAppResource> AppResources { get; } = appResources;
}
