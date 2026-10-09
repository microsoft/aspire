// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

internal sealed class ContainerImageSourceAnnotation : IResourceAnnotation
{
    private readonly ContainerReference? _source;

    internal ContainerImageSourceAnnotation(ContainerReference source)
    {
        _source = source;
    }

    internal ContainerImageSourceAnnotation(
        DockerfileBuildAnnotation dockerfile,
        ContainerBuildOptionsCallbackAnnotation buildOptions)
    {
        Dockerfile = dockerfile;
        BuildOptions = buildOptions;
    }

    internal DockerfileBuildAnnotation? Dockerfile { get; }

    internal ContainerBuildOptionsCallbackAnnotation? BuildOptions { get; }

    internal ContainerReference Source => _source
        ?? throw new InvalidOperationException("Dockerfile-backed image artifacts do not have a remote source image reference.");

    internal string Image => $"{Source.Registry}/{Source.Image}" +
        (Source.Tag is not null ? $":{Source.Tag}" : "") +
        (Source.Digest is not null ? $"@{Source.Digest}" : "");
}
