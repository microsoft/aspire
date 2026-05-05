// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents one source within a Kubernetes projected volume.
/// </summary>
/// <remarks>
/// Kubernetes requires exactly one of <see cref="ConfigMap"/>, <see cref="DownwardApi"/>,
/// <see cref="Secret"/>, or <see cref="ServiceAccountToken"/> to be set for each projection.
/// </remarks>
[YamlSerializable]
public sealed class VolumeProjectionV1
{
    /// <summary>
    /// Gets or sets the ConfigMap source projected into the volume.
    /// </summary>
    [YamlMember(Alias = "configMap")]
    public ConfigMapProjectionV1? ConfigMap { get; set; }

    /// <summary>
    /// Gets or sets the Downward API source projected into the volume.
    /// </summary>
    [YamlMember(Alias = "downwardAPI")]
    public DownwardApiProjectionV1? DownwardApi { get; set; }

    /// <summary>
    /// Gets or sets the Secret source projected into the volume.
    /// </summary>
    [YamlMember(Alias = "secret")]
    public SecretProjectionV1? Secret { get; set; }

    /// <summary>
    /// Gets or sets the service account token source projected into the volume.
    /// </summary>
    [YamlMember(Alias = "serviceAccountToken")]
    public ServiceAccountTokenProjectionV1? ServiceAccountToken { get; set; }
}
