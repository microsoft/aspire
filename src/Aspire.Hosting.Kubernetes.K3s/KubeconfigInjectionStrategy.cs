// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Controls how kubeconfig credentials are injected into resources that reference a <see cref="K3sClusterResource"/>.
/// </summary>
public enum KubeconfigInjectionStrategy
{
    /// <summary>
    /// Automatically selects the strategy based on the consumer type.
    /// Uses <see cref="FilePath"/> for <c>ProjectResource</c> and <c>ExecutableResource</c>,
    /// and <see cref="EnvVar"/> for <c>ContainerResource</c>.
    /// </summary>
    Auto,

    /// <summary>
    /// Injects the kubeconfig file path via the <c>KUBECONFIG</c> environment variable.
    /// Suitable for local processes that can access the host filesystem.
    /// </summary>
    FilePath,

    /// <summary>
    /// Injects the full kubeconfig YAML as a base64-encoded string via the
    /// <c>KUBECONFIG_DATA</c> environment variable (or <c>{prefix}KUBECONFIG_DATA</c> when a prefix is specified).
    /// Suitable for containers where the host filesystem is not accessible.
    /// </summary>
    EnvVar,
}
