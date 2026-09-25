// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a service account token source within a projected volume.
/// </summary>
[YamlSerializable]
public sealed class ServiceAccountTokenProjectionV1
{
    /// <summary>
    /// Gets or sets the intended audience of the token.
    /// </summary>
    [YamlMember(Alias = "audience")]
    public string? Audience { get; set; }

    /// <summary>
    /// Gets or sets the requested token expiration time in seconds.
    /// </summary>
    [YamlMember(Alias = "expirationSeconds")]
    public long? ExpirationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the relative path where the token will be projected.
    /// </summary>
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = null!;
}
