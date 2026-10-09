// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Builds and publishes complete local image artifacts without selecting a platform.
/// </summary>
internal interface IContainerImageArtifactRuntime
{
    Task<string> BuildImageArtifactAsync(
        string contextPath, string dockerfilePath, ContainerImageBuildOptions options,
        Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets,
        string? stage, CancellationToken cancellationToken);

    Task<string> PublishImageArtifactAsync(string digest, string destinationImageName, CancellationToken cancellationToken);
}
