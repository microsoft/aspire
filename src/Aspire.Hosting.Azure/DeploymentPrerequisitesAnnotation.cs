// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An annotation that tracks Azure resources (such as role assignments and private endpoints) that must be
/// provisioned before a compute resource can be deployed.
/// </summary>
/// <param name="resources">The Azure resources that must be provisioned before the annotated resource is deployed.</param>
public sealed class DeploymentPrerequisitesAnnotation(IReadOnlySet<AzureBicepResource> resources) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Azure resources that must be provisioned before the annotated resource is deployed.
    /// </summary>
    public IReadOnlySet<AzureBicepResource> Resources { get; } = resources;
}
