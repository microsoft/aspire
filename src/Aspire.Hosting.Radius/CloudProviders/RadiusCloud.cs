// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// The target cloud for a cloud-managed resource selection. The author specifies
/// this explicitly; it is cross-checked against the recipe and against the
/// provider configured on the environment (see
/// <c>RadiusManagedExtensions.WithManagedResource</c>).
/// </summary>
/// <remarks>
/// Closed set — only the two clouds the <c>Aspire.Hosting.Radius</c> integration
/// supports today (feature <c>003</c> <c>WithAzureProvider</c>/<c>WithAwsProvider</c>).
/// <see cref="None"/> is the default value and is not a valid cloud selection.
/// </remarks>
public enum RadiusCloud
{
    /// <summary>
    /// No cloud selected. This is the default value and is rejected by APIs that require a real cloud.
    /// </summary>
    None = 0,

    /// <summary>
    /// Materialize the resource as an Azure-managed service. Requires
    /// <c>WithAzureProvider</c> on the environment.
    /// </summary>
    Azure = 1,

    /// <summary>
    /// Materialize the resource as an AWS-managed service. Requires
    /// <c>WithAwsProvider</c> on the environment.
    /// </summary>
    Aws = 2,
}
