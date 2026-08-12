// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Ats;

/// <summary>
/// Options for configuring a container image in polyglot apphosts.
/// </summary>
[AspireDto]
internal sealed class AddContainerOptions
{
    /// <summary>
    /// The container image name.
    /// </summary>
    public required string Image { get; init; }

    /// <summary>
    /// The container image tag.
    /// </summary>
    public string? Tag { get; init; }
}
