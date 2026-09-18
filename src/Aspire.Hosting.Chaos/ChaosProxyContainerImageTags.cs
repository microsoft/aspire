// <copyright file="ChaosProxyContainerImageTags.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Hosting.Chaos;

/// <summary>
/// Container image tags for the chaos proxy.
/// </summary>
/// <remarks>
/// Unused during in-house incubation - the container is built locally via WithDockerfile
/// pointing at the package's container/ source directory. Will be populated once the
/// image is published (M4) to MCR or ghcr.io.
/// </remarks>
internal static class ChaosProxyContainerImageTags
{
    /// <summary>Registry once published.</summary>
    public const string Registry = "TODO-mcr.microsoft.com-or-ghcr";

    /// <summary>Image name once published.</summary>
    public const string Image = "azurechaos/chaos-proxy";

    /// <summary>Image tag once published; major.minor format per Aspire conventions.</summary>
    public const string Tag = "0.1";
}
