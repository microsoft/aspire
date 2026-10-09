// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using System.Collections.Concurrent;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Tests.Publishing;

public sealed class FakeRemoteContainerRuntime : FakeContainerRuntime, IContainerRuntime, IContainerImageArtifactRuntime
{
    public ConcurrentBag<string> RemoteResolveCalls { get; } = [];
    public ConcurrentBag<(string Source, string Destination)> RemoteCopyCalls { get; } = [];
    public required Func<string, CancellationToken, Task<string>> ResolveRemoteImageAsyncCallback { get; set; }
    public required Func<string, string, CancellationToken, Task<string>> CopyRemoteImageAsyncCallback { get; set; }
    public Func<CancellationToken, Task<string>>? BuildImageArtifactAsyncCallback { get; set; }
    public Func<string, string, CancellationToken, Task<string>>? PublishImageArtifactAsyncCallback { get; set; }
    public ConcurrentBag<(string Digest, string Destination)> ArtifactPublishCalls { get; } = [];

    Task<string> IContainerImageArtifactRuntime.BuildImageArtifactAsync(
        string contextPath, string dockerfilePath, ContainerImageBuildOptions options,
        Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets,
        string? stage, CancellationToken cancellationToken)
        => BuildArtifactAsync(contextPath, dockerfilePath, options, buildArguments, buildSecrets, stage, cancellationToken);

    private async Task<string> BuildArtifactAsync(
        string contextPath, string dockerfilePath, ContainerImageBuildOptions options,
        Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets,
        string? stage, CancellationToken cancellationToken)
    {
        await BuildImageAsync(contextPath, dockerfilePath, options, buildArguments, buildSecrets, stage, cancellationToken);

        return await (BuildImageArtifactAsyncCallback
            ?? throw new InvalidOperationException("Configure the artifact build callback."))(cancellationToken);
    }

    Task<string> IContainerImageArtifactRuntime.PublishImageArtifactAsync(string digest, string destinationImageName, CancellationToken cancellationToken)
    {
        ArtifactPublishCalls.Add((digest, destinationImageName));

        return (PublishImageArtifactAsyncCallback
            ?? throw new InvalidOperationException("Configure the artifact publication callback."))(digest, destinationImageName, cancellationToken);
    }

    public Task<string> ResolveRemoteImageAsync(string imageName, CancellationToken cancellationToken)
    {
        RemoteResolveCalls.Add(imageName);

        return ResolveRemoteImageAsyncCallback(imageName, cancellationToken);
    }

    public Task<string> CopyRemoteImageAsync(string sourceImageName, string destinationImageName, CancellationToken cancellationToken)
    {
        RemoteCopyCalls.Add((sourceImageName, destinationImageName));

        return CopyRemoteImageAsyncCallback(sourceImageName, destinationImageName, cancellationToken);
    }
}
