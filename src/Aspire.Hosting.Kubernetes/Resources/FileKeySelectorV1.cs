// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Selects a key from an environment variable file in a pod's emptyDir volume.
/// </summary>
/// <remarks>
/// Requires Kubernetes 1.34 or later with the EnvFiles feature enabled.
/// The feature is enabled by default from Kubernetes 1.35.
/// An init container can write the file before the consuming container starts.
/// Changes to the file do not update the environment of a running container.
/// </remarks>
/// <seealso href="https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#FileKeySelector" />
/// <seealso href="https://kubernetes.io/docs/tasks/inject-data-application/define-environment-variable-via-file/" />
[YamlSerializable]
public sealed class FileKeySelectorV1
{
    /// <summary>
    /// Gets or sets the name of the emptyDir volume containing the environment variable file.
    /// </summary>
    [YamlMember(Alias = "volumeName")]
    public string VolumeName { get; set; } = null!;

    /// <summary>
    /// Gets or sets the file path relative to the volume root.
    /// The path must not contain a parent directory component or start with <c>..</c>.
    /// </summary>
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = null!;

    /// <summary>
    /// Gets or sets the key whose value is selected from the environment variable file.
    /// Keys may contain printable ASCII characters except <c>=</c>.
    /// An invalid key prevents the pod from starting. In Kubernetes 1.34,
    /// where EnvFiles is alpha, keys are limited to 128 characters.
    /// </summary>
    [YamlMember(Alias = "key")]
    public string Key { get; set; } = null!;

    /// <summary>
    /// Gets or sets whether the file or key may be absent.
    /// When true, a missing file or key leaves the environment variable unset.
    /// When false or unspecified, a missing file or key prevents the container from starting.
    /// </summary>
    [YamlMember(Alias = "optional")]
    public bool? Optional { get; set; }
}
