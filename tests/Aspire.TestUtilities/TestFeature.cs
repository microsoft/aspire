// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TestUtilities;

[Flags]
public enum TestFeature
{
    SSLCertificate = 1 << 0,
    Playwright = 1 << 1,
    DevCert = 1 << 2,

    /// <summary>
    /// A container runtime is available. Satisfied by either Docker or Podman, so use this for tests
    /// that just need to run containers. Prefer it over <see cref="Docker"/>.
    /// </summary>
    ContainerRuntime = 1 << 3,

    /// <summary>
    /// The available container runtime can build images from a Dockerfile. Docker needs the buildx
    /// plugin for this; Podman builds natively.
    /// </summary>
    ContainerImageBuild = 1 << 4,

    /// <summary>
    /// Docker specifically is available. Use this only for tests that depend on Docker itself rather
    /// than on containers in general — for example, ones that bind-mount the Docker socket or invoke
    /// the <c>docker</c> CLI. Otherwise use <see cref="ContainerRuntime"/>.
    /// </summary>
    Docker = 1 << 5,

    /// <summary>
    /// The Testcontainers library can reach a container runtime. This implies--but is stricter than--<see cref="ContainerRuntime"/>: 
    /// Testcontainers talks to a Docker-compatible HTTP API rather than
    /// driving a CLI, so it also needs a socket or named pipe to connect to. Podman does not always
    /// expose one — Testcontainers 4.x cannot use its Windows named pipe, and on Linux Podman runs
    /// daemonlessly unless <c>podman system service</c> is running — while DCP-driven container tests
    /// are perfectly happy on those hosts. Use this for tests backed by a Testcontainers fixture.
    /// </summary>
    Testcontainers = 1 << 6
}
