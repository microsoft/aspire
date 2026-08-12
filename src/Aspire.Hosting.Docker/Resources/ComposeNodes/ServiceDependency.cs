// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ComposeNodes;

/// <summary>
/// Represents a service dependency in a Docker Compose file.
/// </summary>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class ServiceDependency
{
    /// <summary>
    /// Gets or sets the condition under which the service should be started.
    /// </summary>
    [YamlMember(Alias = "condition")]
    public string? Condition { get; set; }
}
