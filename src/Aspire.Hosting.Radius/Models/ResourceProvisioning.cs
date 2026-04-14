// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Models;

/// <summary>
/// Specifies how a Radius resource is provisioned.
/// </summary>
public enum ResourceProvisioning
{
    /// <summary>
    /// The resource is provisioned automatically via a Radius recipe from the recipe pack.
    /// </summary>
    Automatic,

    /// <summary>
    /// The resource is provisioned manually; the developer provides host, port, and configuration.
    /// </summary>
    /// <remarks>
    /// <b>Upstream deprecation note</b>: Radius's user-defined resource type (UDT) model is
    /// retiring manual provisioning in favor of recipes that connect to pre-existing infrastructure.
    /// This mode will continue to function during the transition period, but a recipe-based
    /// replacement should be expected in a future release.
    /// See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-02-user-defined-resource-type-feature-spec.md
    /// </remarks>
    Manual
}
