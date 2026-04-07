// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Annotation to specify a service delegation for an Azure Subnet.
/// </summary>
/// <param name="name">The name of the service delegation.</param>
/// <param name="serviceName">The service name for the delegation (e.g., "Microsoft.App/environments").</param>
public sealed class AzureSubnetServiceDelegationAnnotation(string name, string serviceName) : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the name associated with the service delegation.
    /// </summary>
    public string Name { get; set; } = name;

    /// <summary>
    /// Gets or sets the name of the service associated with the service delegation.
    /// </summary>
    public string ServiceName { get; set; } = serviceName;
}
