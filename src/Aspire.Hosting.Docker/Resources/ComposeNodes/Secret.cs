// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ComposeNodes;

/// <summary>
/// Represents a Secret object in a Docker Compose configuration file.
/// </summary>
/// <remarks>
/// A Secret object is used to define sensitive information to be shared with containers,
/// such as passwords, keys, or certificates. These secrets can either be externally managed
/// or provided locally from a specific file.
/// </remarks>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class Secret
{
    /// <summary>
    /// Represents the file path associated with the secret.
    /// This specifies the location of the file on the host system
    /// that will be used as the source for the secret in the container.
    /// </summary>
    [YamlMember(Alias = "file")]
    public string? File { get; set; }

    /// <summary>
    /// Indicates whether the secret is managed externally.
    /// If set to true, the secret must already exist, as it will not be created or modified.
    /// </summary>
    [YamlMember(Alias = "external")]
    public bool? External { get; set; }

    /// <summary>
    /// Gets or sets the name of the secret in the Docker Compose configuration.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a collection of key-value pairs representing metadata or additional information
    /// associated with the secret. These labels can be used for categorization, identification,
    /// or other purposes as determined by the user.
    /// </summary>
    [YamlMember(Alias = "labels", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Labels { get; set; } = [];
}
