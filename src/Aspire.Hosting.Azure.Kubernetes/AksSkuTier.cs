// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Specifies the SKU tier for an AKS cluster.
/// </summary>
public enum AksSkuTier
{
    /// <summary>
    /// Free tier with no SLA.
    /// </summary>
    Free,

    /// <summary>
    /// Standard tier with financially backed SLA.
    /// </summary>
    Standard,

    /// <summary>
    /// Premium tier with mission-critical features.
    /// </summary>
    Premium
}
