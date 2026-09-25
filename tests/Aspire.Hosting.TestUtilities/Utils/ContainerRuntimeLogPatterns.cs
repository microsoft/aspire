// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Recognizes container runtime output that Aspire surfaces verbatim in resource logs.
/// </summary>
/// <remarks>
/// DCP streams the runtime's raw stdout/stderr into the resource log stream without normalizing it,
/// so assertions over those logs have to account for Docker and Podman describing the same condition
/// differently. Docker is a CLI over a daemon REST API and uses BuildKit; Podman is daemonless and
/// builds with Buildah.
/// <para>
/// Each predicate accepts any supported runtime's phrasing rather than selecting one by the detected
/// runtime. The phrasings are mutually exclusive, so a test still fails when no runtime produced the
/// expected output, and this keeps the checks working regardless of which runtime DCP picked.
/// </para>
/// <para>
/// The resource logger prefixes each line with a timestamp, so patterns must match anywhere in the
/// line rather than being anchored to its start. For example:
/// <c>2026-07-31T17:40:30.9610000Z Error: unable to copy from source docker://...</c>
/// </para>
/// </remarks>
public static partial class ContainerRuntimeLogPatterns
{
    /// <summary>
    /// Matches a line reporting that pulling an image from a registry failed.
    /// </summary>
    /// <remarks>
    /// Docker attributes the failure to the daemon that serviced the request:
    /// <code>
    /// Error response from daemon: Get "https://does.not.exist.internal/v2/": dial tcp: lookup does.not.exist.internal: no such host
    /// </code>
    /// Podman has no daemon and instead surfaces the containers/image copy pipeline, which names the
    /// remote image using the <c>docker://</c> transport. Two observed forms, from a registry that does
    /// not resolve and from one that rejects anonymous access:
    /// <code>
    /// Error: unable to copy from source docker://does.not.exist.internal/does-not-exist:latest: initializing source docker://does.not.exist.internal/does-not-exist:latest: fetching manifest latest in does.not.exist.internal/does-not-exist: pinging container registry does.not.exist.internal: Get "https://does.not.exist.internal/v2/": dial tcp: lookup does.not.exist.internal: no such host
    /// Error: unable to copy from source docker://cgr.dev/mattermost.com/go-msft-fips:1.24.6: initializing source docker://cgr.dev/mattermost.com/go-msft-fips:1.24.6: fetching manifest 1.24.6 in cgr.dev/mattermost.com/go-msft-fips: unable to retrieve auth token: invalid username/password: unauthorized: Authentication required
    /// </code>
    /// The outer wrapper is version-dependent — Podman 5.x started at <c>Error: initializing source</c>
    /// while 6.0.2 wraps that in <c>Error: unable to copy from source</c> — so match the two parts that
    /// are stable across both: the <c>Error:</c> marker and a <c>docker://</c> source reference.
    /// Requiring both keeps this off successful pulls, which mention neither:
    /// <code>
    /// Trying to pull docker.io/library/hello-world:latest...
    /// Getting image source signatures
    /// Copying blob sha256:58dee6a49ef1c01bb8a00180d70f55b3527c8e7326a05b3c5135c4ff60cfb6d6
    /// Writing manifest to image destination
    /// </code>
    /// </remarks>
    public static bool IsImagePullFailure(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Contains("Error response from daemon", StringComparison.Ordinal)
            || PodmanImagePullFailureRegex().IsMatch(line);
    }

    /// <summary>
    /// Matches a line of image build progress, used to prove build output reaches the app host.
    /// </summary>
    /// <remarks>
    /// Docker builds with BuildKit, which emits numbered internal steps:
    /// <code>
    /// #1 [internal] load build definition from Dockerfile
    /// </code>
    /// Podman builds with Buildah, which emits one line per Dockerfile instruction as
    /// <c>STEP &lt;n&gt;/&lt;total&gt;:</c>, prefixed with <c>[&lt;stage&gt;/&lt;stages&gt;]</c> for a
    /// multi-stage build:
    /// <code>
    /// [1/2] STEP 1/5: FROM mcr.microsoft.com/cbl-mariner/base/nginx:1.22 AS builder
    /// [2/2] STEP 6/6: LABEL "com.microsoft.developer.usvc-dev.build"="0.25.9"
    /// STEP 1/3: FROM alpine
    /// </code>
    /// The step counter is matched explicitly rather than looking for a bare <c>STEP</c>, so that build
    /// output which merely contains that word — say a <c>RUN echo</c> in the Dockerfile under test —
    /// does not satisfy the assertion.
    /// </remarks>
    public static bool IsBuildProgress(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Contains("load build definition from Dockerfile", StringComparison.Ordinal)
            || PodmanBuildStepRegex().IsMatch(line);
    }

    [GeneratedRegex(@"Error:.*\bdocker://")]
    private static partial Regex PodmanImagePullFailureRegex();

    [GeneratedRegex(@"\bSTEP \d+/\d+:")]
    private static partial Regex PodmanBuildStepRegex();
}
