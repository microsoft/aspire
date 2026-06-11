// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Polyglot-friendly subset of Azure Container App scale configuration.
/// </summary>
[AspireDto]
internal sealed class AzureContainerAppScaleConfig
{
    /// <summary>
    /// Gets or sets the minimum number of replicas.
    /// </summary>
    public int? MinReplicas { get; set; }
}
