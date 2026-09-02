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
    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageConfig"/> class.
    /// </summary>
    /// <param name="entrypoint">The image entrypoint.</param>
    /// <param name="command">The default image command.</param>
    /// <param name="workingDirectory">The image working directory.</param>
    public ContainerImageConfig(
        IReadOnlyList<string> entrypoint,
        IReadOnlyList<string> command,
        string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(entrypoint);
        ArgumentNullException.ThrowIfNull(command);

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

    /// <summary>
    /// Creates a successful image configuration inspection result.
    /// </summary>
    /// <param name="config">The inspected image configuration.</param>
    /// <param name="rawJson">The runtime-native JSON returned by the inspection command, when available.</param>
    /// <returns>A successful image configuration inspection result.</returns>
    public static ContainerImageConfigInspectionResult Success(ContainerImageConfig config, string? rawJson = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new(
            ContainerImageInspectionStatus.Succeeded,
            rawJson,
            errorMessage: null,
            () => config);
    }

    /// <summary>
    /// Creates a failed image configuration inspection result.
    /// </summary>
    /// <param name="errorMessage">The failure description.</param>
    /// <param name="rawJson">The runtime-native JSON returned by the inspection command, when available.</param>
    /// <returns>A failed image configuration inspection result.</returns>
    public static ContainerImageConfigInspectionResult Failure(string errorMessage, string? rawJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        return new(
            ContainerImageInspectionStatus.Failed,
            rawJson,
            errorMessage,
            configAccessor: null);
    }

    /// <summary>
    /// Gets an image configuration inspection result for a runtime that does not support inspection.
    /// </summary>
    public static ContainerImageConfigInspectionResult Unsupported { get; } = new(
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
    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageManifest"/> class.
    /// </summary>
    /// <param name="digest">The immutable manifest digest.</param>
    /// <param name="operatingSystem">The target operating system.</param>
    /// <param name="architecture">The target architecture.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="digest"/> is not a lowercase SHA-256 digest or another argument is empty.</exception>
    public ContainerImageManifest(string digest, string operatingSystem, string architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatingSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        if (!IsValidDigest(digest))
        {
            throw new ArgumentException(
                "The container image manifest digest must use the format 'sha256:' followed by 64 lowercase hexadecimal characters.",
                nameof(digest));
        }

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

    internal static bool IsValidDigest(string digest)
    {
        const string Prefix = "sha256:";
        if (digest.Length != Prefix.Length + 64 ||
            !digest.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in digest.AsSpan(Prefix.Length))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
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

    /// <summary>
    /// Creates a successful image manifest inspection result.
    /// </summary>
    /// <param name="manifests">The inspected platform-specific image manifests.</param>
    /// <param name="rawJson">The runtime-native JSON returned by the inspection command, when available.</param>
    /// <returns>A successful image manifest inspection result.</returns>
    public static ContainerImageManifestInspectionResult Success(
        IReadOnlyList<ContainerImageManifest> manifests,
        string? rawJson = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var manifestSnapshot = manifests.ToArray();
        return new(
            ContainerImageInspectionStatus.Succeeded,
            rawJson,
            errorMessage: null,
            (operatingSystem, architecture) => manifestSnapshot.FirstOrDefault(manifest =>
                string.Equals(manifest.OperatingSystem, operatingSystem, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(manifest.Architecture, architecture, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Creates a failed image manifest inspection result.
    /// </summary>
    /// <param name="errorMessage">The failure description.</param>
    /// <param name="rawJson">The runtime-native JSON returned by the inspection command, when available.</param>
    /// <returns>A failed image manifest inspection result.</returns>
    public static ContainerImageManifestInspectionResult Failure(string errorMessage, string? rawJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        return new(
            ContainerImageInspectionStatus.Failed,
            rawJson,
            errorMessage,
            manifestAccessor: null);
    }

    /// <summary>
    /// Gets an image manifest inspection result for a runtime that does not support inspection.
    /// </summary>
    public static ContainerImageManifestInspectionResult Unsupported { get; } = new(
        ContainerImageInspectionStatus.Unsupported,
        rawJson: null,
        errorMessage: null,
        manifestAccessor: null);
}
