// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

/// <summary>
/// Annotation that stores the ACI subnet reference for deployment script configuration.
/// </summary>
[Experimental("ASPIREAZURE003")]
internal sealed class AdminDeploymentScriptSubnetAnnotation(AzureSubnetResource subnet) : IResourceAnnotation
{
    /// <summary>
    /// Gets the ACI subnet resource used for deployment scripts.
    /// </summary>
    public AzureSubnetResource Subnet { get; } = subnet ?? throw new ArgumentNullException(nameof(subnet));
}
