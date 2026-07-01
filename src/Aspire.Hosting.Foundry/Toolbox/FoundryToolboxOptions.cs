// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Options used when adding a Microsoft Foundry Toolbox resource to a project.
/// </summary>
[AspireDto]
internal sealed class FoundryToolboxOptions
{
    /// <summary>
    /// Gets or sets the optional Toolbox version to reference. When unset, the default
    /// Toolbox version is used.
    /// </summary>
    public string? Version { get; set; }
}
