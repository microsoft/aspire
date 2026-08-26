// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Describes the outcome of a container image inspection operation.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum ContainerImageInspectionStatus
{
    /// <summary>
    /// The inspection completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The container runtime does not support the requested inspection.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The inspection failed.
    /// </summary>
    Failed
}

/// <summary>
/// Represents typed container image configuration metadata.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerImageConfig
{
    internal ContainerImageConfig(
        IReadOnlyList<string> entrypoint,
        IReadOnlyList<string> command,
        string? workingDirectory)
    {
        Entrypoint = entrypoint;
        Command = command;
        WorkingDirectory = workingDirectory;
    }

    /// <summary>
    /// Gets the image entrypoint.
    /// </summary>
    public IReadOnlyList<string> Entrypoint { get; }

    /// <summary>
    /// Gets the default image command.
    /// </summary>
    public IReadOnlyList<string> Command { get; }

    /// <summary>
    /// Gets the image working directory.
    /// </summary>
    public string? WorkingDirectory { get; }
}

/// <summary>
/// Represents the result of inspecting a container image configuration.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerImageConfigInspectionResult
{
    private readonly Func<ContainerImageConfig?>? _configAccessor;

    internal ContainerImageConfigInspectionResult(
        ContainerImageInspectionStatus status,
        string? rawJson,
        string? errorMessage,
        Func<ContainerImageConfig?>? configAccessor)
    {
        Status = status;
        RawJson = rawJson;
        ErrorMessage = errorMessage;
        _configAccessor = configAccessor;
    }

    /// <summary>
    /// Gets the inspection status.
    /// </summary>
    public ContainerImageInspectionStatus Status { get; }

    /// <summary>
    /// Gets the runtime-native JSON returned by the inspection command, when available.
    /// </summary>
    public string? RawJson { get; }

    /// <summary>
    /// Gets the failure description when <see cref="Status"/> is <see cref="ContainerImageInspectionStatus.Failed"/>.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Attempts to retrieve the typed image configuration.
    /// </summary>
    /// <param name="config">The image configuration when inspection succeeded.</param>
    /// <returns><see langword="true"/> when typed configuration metadata is available; otherwise, <see langword="false"/>.</returns>
    public bool TryGetConfig([NotNullWhen(true)] out ContainerImageConfig? config)
    {
        config = Status == ContainerImageInspectionStatus.Succeeded
            ? _configAccessor?.Invoke()
            : null;

        return config is not null;
    }

    internal static ContainerImageConfigInspectionResult Unsupported { get; } = new(
        ContainerImageInspectionStatus.Unsupported,
        rawJson: null,
        errorMessage: null,
        configAccessor: null);
}

/// <summary>
/// Represents a platform-specific container image manifest.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerImageManifest
{
    internal ContainerImageManifest(string digest, string operatingSystem, string architecture)
    {
        Digest = digest;
        OperatingSystem = operatingSystem;
        Architecture = architecture;
    }

    /// <summary>
    /// Gets the immutable manifest digest.
    /// </summary>
    public string Digest { get; }

    /// <summary>
    /// Gets the target operating system.
    /// </summary>
    public string OperatingSystem { get; }

    /// <summary>
    /// Gets the target architecture.
    /// </summary>
    public string Architecture { get; }
}

/// <summary>
/// Represents the result of inspecting a container image manifest.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerImageManifestInspectionResult
{
    private readonly Func<string, string, ContainerImageManifest?>? _manifestAccessor;

    internal ContainerImageManifestInspectionResult(
        ContainerImageInspectionStatus status,
        string? rawJson,
        string? errorMessage,
        Func<string, string, ContainerImageManifest?>? manifestAccessor)
    {
        Status = status;
        RawJson = rawJson;
        ErrorMessage = errorMessage;
        _manifestAccessor = manifestAccessor;
    }

    /// <summary>
    /// Gets the inspection status.
    /// </summary>
    public ContainerImageInspectionStatus Status { get; }

    /// <summary>
    /// Gets the runtime-native JSON returned by the inspection command, when available.
    /// </summary>
    public string? RawJson { get; }

    /// <summary>
    /// Gets the failure description when <see cref="Status"/> is <see cref="ContainerImageInspectionStatus.Failed"/>.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Attempts to retrieve a manifest for the requested platform.
    /// </summary>
    /// <param name="operatingSystem">The target operating system.</param>
    /// <param name="architecture">The target architecture.</param>
    /// <param name="manifest">The matching manifest when one is available.</param>
    /// <returns><see langword="true"/> when a matching manifest is available; otherwise, <see langword="false"/>.</returns>
    public bool TryGetManifest(
        string operatingSystem,
        string architecture,
        [NotNullWhen(true)] out ContainerImageManifest? manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatingSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        manifest = Status == ContainerImageInspectionStatus.Succeeded
            ? _manifestAccessor?.Invoke(operatingSystem, architecture)
            : null;

        return manifest is not null;
    }

    internal static ContainerImageManifestInspectionResult Unsupported { get; } = new(
        ContainerImageInspectionStatus.Unsupported,
        rawJson: null,
        errorMessage: null,
        manifestAccessor: null);
}
