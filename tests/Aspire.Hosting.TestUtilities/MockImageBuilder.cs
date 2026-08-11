// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Collections.Concurrent;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Mock implementation of IResourceContainerImageManager for testing.
/// </summary>
public sealed class MockImageBuilder : IResourceContainerImageManager
{
    public bool BuildImageCalled { get; private set; }
    public bool BuildImagesCalled { get; private set; }
    public bool PushImageCalled { get; private set; }
    public ConcurrentBag<IResource> BuildImageResources { get; } = [];
    public ConcurrentBag<ContainerImageBuildOptions?> BuildImageOptions { get; } = [];
    public ConcurrentBag<IResource> PushImageCalls { get; } = [];

    public Task BuildImageAsync(IResource resource, CancellationToken cancellationToken = default)
    {
        BuildImageCalled = true;
        BuildImageResources.Add(resource);
        return Task.CompletedTask;
    }

    public Task BuildImagesAsync(IEnumerable<IResource> resources, CancellationToken cancellationToken = default)
    {
        BuildImagesCalled = true;
        foreach (var resource in resources)
        {
            BuildImageResources.Add(resource);
        }
        return Task.CompletedTask;
    }

    public Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
    {
        PushImageCalled = true;
        PushImageCalls.Add(resource);
        return Task.CompletedTask;
    }
}
