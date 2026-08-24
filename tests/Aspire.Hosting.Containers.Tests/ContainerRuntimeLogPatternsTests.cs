// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Containers.Tests;

/// <summary>
/// Locks in the container runtime output formats that <see cref="ContainerRuntimeLogPatterns"/>
/// recognizes. The sample lines are verbatim captures, including the timestamp prefix that the
/// resource logger adds, so a runtime changing its wording fails here rather than in a functional
/// test where the cause is much harder to see.
/// </summary>
public class ContainerRuntimeLogPatternsTests
{
    [Theory]
    // Docker, registry does not resolve.
    [InlineData(@"2026-07-31T17:40:30.9610000Z Error response from daemon: Get ""https://does.not.exist.internal/v2/"": dial tcp: lookup does.not.exist.internal: no such host")]
    // Docker, registry rejects anonymous access.
    [InlineData("2026-07-31T17:40:30.9610000Z Error response from daemon: unauthorized: authentication required")]
    // Podman 6.0.2, registry does not resolve.
    [InlineData(@"2026-07-31T17:40:30.9610000Z Error: unable to copy from source docker://does.not.exist.internal/does-not-exist:latest: initializing source docker://does.not.exist.internal/does-not-exist:latest: fetching manifest latest in does.not.exist.internal/does-not-exist: pinging container registry does.not.exist.internal: Get ""https://does.not.exist.internal/v2/"": dial tcp: lookup does.not.exist.internal: no such host")]
    // Podman 6.0.2, registry rejects anonymous access.
    [InlineData("2026-07-31T17:40:30.9610000Z Error: unable to copy from source docker://cgr.dev/mattermost.com/go-msft-fips:1.24.6: initializing source docker://cgr.dev/mattermost.com/go-msft-fips:1.24.6: fetching manifest 1.24.6 in cgr.dev/mattermost.com/go-msft-fips: unable to retrieve auth token: invalid username/password: unauthorized: Authentication required")]
    // Podman 5.x, which lacked the outer "unable to copy from source" wrapper.
    [InlineData("2026-07-30T22:57:47.7100000Z Error: initializing source docker://does.not.exist.internal/does-not-exist:latest: pinging container registry does.not.exist.internal: no such host")]
    public void IsImagePullFailure_MatchesRuntimeFailureOutput(string line)
    {
        Assert.True(ContainerRuntimeLogPatterns.IsImagePullFailure(line));
    }

    [Theory]
    // Successful Podman pull: no error marker and no docker:// source reference.
    [InlineData("2026-07-31T17:40:30.9610000Z Trying to pull docker.io/library/hello-world:latest...")]
    [InlineData("2026-07-31T17:40:30.9610000Z Getting image source signatures")]
    [InlineData("2026-07-31T17:40:30.9610000Z Copying blob sha256:58dee6a49ef1c01bb8a00180d70f55b3527c8e7326a05b3c5135c4ff60cfb6d6")]
    [InlineData("2026-07-31T17:40:30.9610000Z Writing manifest to image destination")]
    // An unrelated error must not be mistaken for a pull failure.
    [InlineData("2026-07-31T17:40:30.9610000Z Error: container exited with code 1")]
    // A docker:// reference on its own is not a failure.
    [InlineData("2026-07-31T17:40:30.9610000Z Resolved docker://docker.io/library/redis:8.6")]
    public void IsImagePullFailure_DoesNotMatchUnrelatedOutput(string line)
    {
        Assert.False(ContainerRuntimeLogPatterns.IsImagePullFailure(line));
    }

    [Theory]
    // Docker BuildKit.
    [InlineData("2026-07-31T17:41:35.2070000Z #1 [internal] load build definition from Dockerfile")]
    // Podman/Buildah, multi-stage build.
    [InlineData("2026-07-31T17:41:35.2070000Z [1/2] STEP 1/5: FROM mcr.microsoft.com/cbl-mariner/base/nginx:1.22 AS builder")]
    [InlineData(@"2026-07-31T17:41:39.0250000Z [2/2] STEP 6/6: LABEL ""com.microsoft.developer.usvc-dev.build""=""0.25.9""")]
    // Podman/Buildah, single-stage build has no stage prefix.
    [InlineData("2026-07-31T17:41:35.2070000Z STEP 1/3: FROM alpine")]
    public void IsBuildProgress_MatchesRuntimeBuildOutput(string line)
    {
        Assert.True(ContainerRuntimeLogPatterns.IsBuildProgress(line));
    }

    [Theory]
    // Build output that merely contains the word STEP must not count as build progress.
    [InlineData("2026-07-31T17:41:35.2070000Z STEP: starting application")]
    [InlineData("2026-07-31T17:41:35.2070000Z Running STEP 2 of the deployment script")]
    // Buildah output that is not a step line.
    [InlineData("2026-07-31T17:41:39.0360000Z Successfully tagged localhost/testcontainer:50a50dfc")]
    [InlineData("2026-07-31T17:41:35.2070000Z Getting image source signatures")]
    public void IsBuildProgress_DoesNotMatchUnrelatedOutput(string line)
    {
        Assert.False(ContainerRuntimeLogPatterns.IsBuildProgress(line));
    }
}
