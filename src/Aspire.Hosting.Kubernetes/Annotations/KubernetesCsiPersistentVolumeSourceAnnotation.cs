// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Annotations;

/// <summary>
/// Configures a <see cref="KubernetesPersistentVolumeResource"/> with a statically
/// provisioned Container Storage Interface (CSI) backing volume.
/// </summary>
/// <param name="driver">The CSI driver name.</param>
/// <param name="volumeHandle">The unique volume identifier understood by the CSI driver.</param>
/// <param name="volumeAttributes">Driver-specific volume attributes.</param>
/// <param name="mountOptions">Options passed to the filesystem mount operation.</param>
/// <param name="reclaimPolicy">The reclaim policy for the generated persistent volume.</param>
/// <param name="readOnly">Whether the CSI volume is read-only.</param>
/// <param name="fileSystemType">The filesystem type to mount, or <see langword="null"/> to let the driver choose.</param>
/// <param name="defaultStorageClassName">The storage class name used when the volume resource does not specify one.</param>
/// <param name="defaultAccessMode">The access mode used when the volume resource does not specify one.</param>
/// <remarks>
/// Values remain deferred until deployment, allowing deployment-target integrations to
/// reference infrastructure outputs without materializing them into published artifacts.
/// </remarks>
[Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal sealed class KubernetesCsiPersistentVolumeSourceAnnotation(
    string driver,
    ReferenceExpression volumeHandle,
    IReadOnlyDictionary<string, ReferenceExpression> volumeAttributes,
    IReadOnlyList<string>? mountOptions = null,
    PersistentVolumeReclaimPolicy reclaimPolicy = PersistentVolumeReclaimPolicy.Retain,
    bool readOnly = false,
    string? fileSystemType = null,
    string? defaultStorageClassName = null,
    PersistentVolumeAccessMode? defaultAccessMode = null) : IResourceAnnotation
{
    /// <summary>
    /// Gets the CSI driver name.
    /// </summary>
    public string Driver { get; } = string.IsNullOrWhiteSpace(driver)
        ? throw new ArgumentException("The CSI driver name cannot be empty.", nameof(driver))
        : driver;

    /// <summary>
    /// Gets the unique volume identifier understood by the CSI driver.
    /// </summary>
    public ReferenceExpression VolumeHandle { get; } = volumeHandle ?? throw new ArgumentNullException(nameof(volumeHandle));

    /// <summary>
    /// Gets the driver-specific volume attributes.
    /// </summary>
    public IReadOnlyDictionary<string, ReferenceExpression> VolumeAttributes { get; } =
        ValidateVolumeAttributes(volumeAttributes);

    /// <summary>
    /// Gets the options passed to the filesystem mount operation.
    /// </summary>
    public IReadOnlyList<string> MountOptions { get; } = mountOptions?.ToArray() ?? [];

    /// <summary>
    /// Gets the reclaim policy for the generated persistent volume.
    /// </summary>
    public PersistentVolumeReclaimPolicy ReclaimPolicy { get; } = reclaimPolicy;

    /// <summary>
    /// Gets a value indicating whether the CSI volume is read-only.
    /// </summary>
    public bool ReadOnly { get; } = readOnly;

    /// <summary>
    /// Gets the filesystem type to mount, or <see langword="null"/> to let the driver choose.
    /// </summary>
    public string? FileSystemType { get; } = fileSystemType;

    /// <summary>
    /// Gets the storage class name used when the volume resource does not specify one.
    /// </summary>
    public string? DefaultStorageClassName { get; } = defaultStorageClassName;

    /// <summary>
    /// Gets the access mode used when the volume resource does not specify one.
    /// </summary>
    public PersistentVolumeAccessMode? DefaultAccessMode { get; } = defaultAccessMode;

    private static IReadOnlyDictionary<string, ReferenceExpression> ValidateVolumeAttributes(
        IReadOnlyDictionary<string, ReferenceExpression> volumeAttributes)
    {
        ArgumentNullException.ThrowIfNull(volumeAttributes);

        var result = new Dictionary<string, ReferenceExpression>(StringComparer.Ordinal);
        foreach (var (key, value) in volumeAttributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            result.Add(key, value);
        }

        return result;
    }
}
