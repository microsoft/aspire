// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;

namespace Aspire.Shared.TerminalHost;

/// <summary>
/// Shared helpers for computing the on-disk paths used by per-replica terminal hosts.
/// </summary>
/// <remarks>
/// <para>
/// All per-replica terminal-host files live flat under <c>~/.aspire/trmnl/</c>. Each
/// replica is identified by a random 11-character base64url <em>replica id</em>. The four
/// files for a single replica share the same <c>{replicaId}.</c> prefix:
/// </para>
/// <list type="bullet">
///   <item><description><c>{replicaId}.dcp.sock</c> — producer UDS (host listens, DCP dials)</description></item>
///   <item><description><c>{replicaId}.host.sock</c> — consumer UDS (host listens, viewers dial)</description></item>
///   <item><description><c>{replicaId}.ctrl.sock</c> — control UDS (host listens, AppHost dials)</description></item>
///   <item><description><c>{replicaId}.metadata.json</c> — descriptor sidecar (resource name, replica index, dims, PID)</description></item>
/// </list>
/// <para>
/// A flat layout (no per-AppHost or per-replica sub-directories) keeps the absolute
/// path short enough to fit inside <c>sockaddr_un.sun_path</c> on macOS (104 bytes
/// including the trailing NUL). A typical macOS layout is
/// <c>/Users/&lt;you&gt;/.aspire/trmnl/AbCdEfGhIjK.ctrl.sock</c> ≈ 52 bytes.
/// </para>
/// <para>
/// Per-run identifiers prevent an older AppHost or terminal-host process from deleting or
/// rebinding a newer run's sockets. External tools discover terminals by listing metadata
/// sidecars, so stable filenames are unnecessary.
/// </para>
/// </remarks>
internal static class TerminalHostPaths
{
    /// <summary>
    /// Name of the user-profile-relative root directory for all Aspire per-user state.
    /// </summary>
    public const string DotAspireDirectoryName = ".aspire";

    /// <summary>
    /// Name of the sub-directory under <c>~/.aspire/</c> that holds terminal-host files.
    /// Kept short (<c>trmnl</c> instead of <c>terminals</c>) because the parent path
    /// counts against the <c>sun_path</c> limit on macOS.
    /// </summary>
    public const string TrmnlDirectoryName = "trmnl";

    /// <summary>Sockpurpose suffix for the producer UDS (DCP → host).</summary>
    public const string ProducerSockPurpose = "dcp";

    /// <summary>Sockpurpose suffix for the consumer UDS (host → viewers).</summary>
    public const string ConsumerSockPurpose = "host";

    /// <summary>Sockpurpose suffix for the control UDS (AppHost → host).</summary>
    public const string ControlSockPurpose = "ctrl";

    /// <summary>Suffix for the per-replica metadata sidecar (JSON).</summary>
    public const string MetadataSuffix = "metadata.json";

    /// <summary>Suffix for a metadata sidecar while it is being written atomically.</summary>
    public const string MetadataTemporarySuffix = MetadataSuffix + ".tmp";

    /// <summary>Internal configuration key that overrides the terminal artifact directory.</summary>
    public const string DirectoryOverrideConfigName = "AppHost:TerminalHostDirectory";

    /// <summary>
    /// Length in characters of the base64url replica identifier.
    /// </summary>
    /// <remarks>
    /// 8 bytes → ceil(8 / 3) * 4 = 12 base64 chars, minus one '=' = 11.
    /// </remarks>
    public const int ReplicaIdLength = 11;

    private const int ReplicaIdByteCount = 8;

    /// <summary>
    /// Gets the absolute path of <c>~/.aspire/trmnl/</c> for the given user home directory.
    /// </summary>
    /// <param name="homeDirectory">
    /// User's profile directory, typically
    /// <c>Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)</c>.
    /// </param>
    public static string GetTrmnlDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        return Path.Combine(homeDirectory, DotAspireDirectoryName, TrmnlDirectoryName);
    }

    /// <summary>
    /// Creates an 11-character random base64url replica identifier for one AppHost run.
    /// </summary>
    public static string CreateReplicaId()
    {
        Span<byte> bytes = stackalloc byte[ReplicaIdByteCount];
        RandomNumberGenerator.Fill(bytes);
        return ToBase64UrlIdentifier(bytes);
    }

    /// <summary>
    /// Gets the absolute socket path for a given replica id and sockpurpose.
    /// Format: <c>{trmnlDirectory}/{replicaId}.{sockPurpose}.sock</c>.
    /// </summary>
    /// <param name="trmnlDirectory">Terminal artifact directory.</param>
    /// <param name="replicaId">Output of <see cref="CreateReplicaId"/>.</param>
    /// <param name="sockPurpose">One of <see cref="ProducerSockPurpose"/>, <see cref="ConsumerSockPurpose"/>, <see cref="ControlSockPurpose"/>.</param>
    public static string GetSocketPath(string trmnlDirectory, string replicaId, string sockPurpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(trmnlDirectory);
        ArgumentException.ThrowIfNullOrEmpty(replicaId);
        ArgumentException.ThrowIfNullOrEmpty(sockPurpose);
        return Path.Combine(trmnlDirectory, $"{replicaId}.{sockPurpose}.sock");
    }

    /// <summary>
    /// Gets the absolute metadata-sidecar path for a given replica id.
    /// Format: <c>{trmnlDirectory}/{replicaId}.metadata.json</c>.
    /// </summary>
    public static string GetMetadataPath(string trmnlDirectory, string replicaId)
    {
        ArgumentException.ThrowIfNullOrEmpty(trmnlDirectory);
        ArgumentException.ThrowIfNullOrEmpty(replicaId);
        return Path.Combine(trmnlDirectory, $"{replicaId}.{MetadataSuffix}");
    }

    /// <summary>
    /// Gets the temporary path used while atomically writing a metadata sidecar.
    /// </summary>
    public static string GetMetadataTemporaryPath(string metadataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(metadataPath);
        return metadataPath + ".tmp";
    }

    /// <summary>
    /// Extracts and validates the replica identifier encoded in a metadata sidecar filename.
    /// </summary>
    public static bool TryGetReplicaIdFromMetadataPath(string metadataPath, out string replicaId)
        => TryGetReplicaId(metadataPath, MetadataSuffix, out replicaId);

    /// <summary>
    /// Extracts and validates the replica identifier encoded in a temporary metadata filename.
    /// </summary>
    public static bool TryGetReplicaIdFromMetadataTemporaryPath(string metadataTemporaryPath, out string replicaId)
        => TryGetReplicaId(metadataTemporaryPath, MetadataTemporarySuffix, out replicaId);

    private static bool TryGetReplicaId(string path, string suffix, out string replicaId)
    {
        var fileName = Path.GetFileName(path);
        var extension = "." + suffix;
        if (!fileName.EndsWith(extension, StringComparison.Ordinal))
        {
            replicaId = string.Empty;
            return false;
        }

        var candidate = fileName.AsSpan(0, fileName.Length - extension.Length);
        if (candidate.Length != ReplicaIdLength)
        {
            replicaId = string.Empty;
            return false;
        }

        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                replicaId = string.Empty;
                return false;
            }
        }

        replicaId = candidate.ToString();
        return true;
    }

    private static string ToBase64UrlIdentifier(ReadOnlySpan<byte> bytes)
    {
        // base64url: '+' → '-', '/' → '_', strip '=' padding.
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
