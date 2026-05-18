// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Docker.Resources;
using ComposeConfig = Aspire.Hosting.Docker.Resources.ComposeNodes.Config;
using ComposeNetwork = Aspire.Hosting.Docker.Resources.ComposeNodes.Network;
using ComposeSecret = Aspire.Hosting.Docker.Resources.ComposeNodes.Secret;
using ComposeService = Aspire.Hosting.Docker.Resources.ComposeNodes.Service;
using ComposeVolume = Aspire.Hosting.Docker.Resources.ServiceNodes.Volume;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for customizing generated Docker Compose resource models.
/// </summary>
internal static class DockerComposeResourceExtensions
{
    /// <summary>
    /// Adds a top-level network definition to the Docker Compose file.
    /// </summary>
    /// <param name="composeFile">The Docker Compose file to customize.</param>
    /// <param name="name">The name of the network.</param>
    /// <param name="driver">The network driver to use.</param>
    /// <param name="external">A value indicating whether the network is managed outside the generated Compose file.</param>
    /// <param name="configure">An optional callback for further network customization.</param>
    /// <returns>The updated Docker Compose file.</returns>
    [AspireExport("addComposeFileNetwork", MethodName = "addNetwork", Description = "Adds a top-level network definition to the Docker Compose file", RunSyncOnBackgroundThread = true)]
    internal static ComposeFile AddNetwork(
        this ComposeFile composeFile,
        string name,
        string? driver = null,
        bool? external = null,
        Action<ComposeNetwork>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(composeFile);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var network = new ComposeNetwork
        {
            Name = name,
            Driver = driver,
            External = external
        };

        configure?.Invoke(network);

        return composeFile.AddNetwork(network);
    }

    /// <summary>
    /// Adds a service definition to the Docker Compose file.
    /// </summary>
    /// <param name="composeFile">The Docker Compose file to customize.</param>
    /// <param name="name">The name of the service.</param>
    /// <param name="image">The container image to use for the service.</param>
    /// <param name="configure">An optional callback for further service customization.</param>
    /// <returns>The updated Docker Compose file.</returns>
    [AspireExport("addComposeFileService", MethodName = "addService", Description = "Adds a service definition to the Docker Compose file", RunSyncOnBackgroundThread = true)]
    internal static ComposeFile AddService(
        this ComposeFile composeFile,
        string name,
        string? image = null,
        Action<ComposeService>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(composeFile);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var service = new ComposeService
        {
            Name = name,
            Image = image
        };

        configure?.Invoke(service);

        return composeFile.AddService(service);
    }

    /// <summary>
    /// Adds a top-level volume definition to the Docker Compose file.
    /// </summary>
    /// <param name="composeFile">The Docker Compose file to customize.</param>
    /// <param name="name">The name of the volume.</param>
    /// <param name="driver">The volume driver to use.</param>
    /// <param name="external">A value indicating whether the volume is managed outside the generated Compose file.</param>
    /// <param name="configure">An optional callback for further volume customization.</param>
    /// <returns>The updated Docker Compose file.</returns>
    [AspireExport("addComposeFileVolume", MethodName = "addVolume", Description = "Adds a top-level volume definition to the Docker Compose file", RunSyncOnBackgroundThread = true)]
    internal static ComposeFile AddVolume(
        this ComposeFile composeFile,
        string name,
        string? driver = null,
        bool? external = null,
        Action<ComposeVolume>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(composeFile);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var volume = new ComposeVolume
        {
            Name = name,
            Driver = driver,
            External = external
        };

        configure?.Invoke(volume);

        return composeFile.AddVolume(volume);
    }

    /// <summary>
    /// Adds a top-level config definition to the Docker Compose file.
    /// </summary>
    /// <param name="composeFile">The Docker Compose file to customize.</param>
    /// <param name="name">The name of the config.</param>
    /// <param name="file">The path to the config file.</param>
    /// <param name="content">The inline config content.</param>
    /// <param name="external">A value indicating whether the config is managed outside the generated Compose file.</param>
    /// <param name="configure">An optional callback for further config customization.</param>
    /// <returns>The updated Docker Compose file.</returns>
    [AspireExport("addComposeFileConfig", MethodName = "addConfig", Description = "Adds a top-level config definition to the Docker Compose file", RunSyncOnBackgroundThread = true)]
    internal static ComposeFile AddConfig(
        this ComposeFile composeFile,
        string name,
        string? file = null,
        string? content = null,
        bool? external = null,
        Action<ComposeConfig>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(composeFile);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var config = new ComposeConfig
        {
            Name = name,
            File = file,
            Content = content,
            External = external
        };

        configure?.Invoke(config);

        return composeFile.AddConfig(config);
    }

    /// <summary>
    /// Adds a top-level secret definition to the Docker Compose file.
    /// </summary>
    /// <param name="composeFile">The Docker Compose file to customize.</param>
    /// <param name="name">The name of the secret.</param>
    /// <param name="file">The path to the secret file.</param>
    /// <param name="external">A value indicating whether the secret is managed outside the generated Compose file.</param>
    /// <param name="configure">An optional callback for further secret customization.</param>
    /// <returns>The updated Docker Compose file.</returns>
    [AspireExport("addComposeFileSecret", MethodName = "addSecret", Description = "Adds a top-level secret definition to the Docker Compose file", RunSyncOnBackgroundThread = true)]
    internal static ComposeFile AddSecret(
        this ComposeFile composeFile,
        string name,
        string? file = null,
        bool? external = null,
        Action<ComposeSecret>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(composeFile);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var secret = new ComposeSecret
        {
            Name = name,
            File = file,
            External = external
        };

        configure?.Invoke(secret);

        composeFile.Secrets[name] = secret;
        return composeFile;
    }

    /// <summary>
    /// Adds a volume mount to a generated Docker Compose service.
    /// </summary>
    /// <param name="service">The Docker Compose service to customize.</param>
    /// <param name="source">The source volume name or host path.</param>
    /// <param name="target">The target path inside the container.</param>
    /// <param name="type">The Docker Compose volume type, such as <c>volume</c> or <c>bind</c>.</param>
    /// <param name="isReadOnly">A value indicating whether the volume is mounted as read-only.</param>
    /// <param name="configure">An optional callback for further volume customization.</param>
    /// <returns>The updated Docker Compose service.</returns>
    [AspireExport("addComposeServiceVolume", MethodName = "addVolume", Description = "Adds a volume mount to a Docker Compose service", RunSyncOnBackgroundThread = true)]
    internal static ComposeService AddVolume(
        this ComposeService service,
        string source,
        string target,
        string? type = "volume",
        bool? isReadOnly = null,
        Action<ComposeVolume>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var volume = new ComposeVolume
        {
            Name = source,
            Source = source,
            Target = target,
            Type = type,
            ReadOnly = isReadOnly
        };

        configure?.Invoke(volume);

        return service.AddVolume(volume);
    }
}
