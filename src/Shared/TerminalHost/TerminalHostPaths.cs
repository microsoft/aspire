// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Aspire.Shared.TerminalHost;

/// <summary>
/// Shared helpers for computing the on-disk paths used by per-replica terminal hosts.
/// </summary>
/// <remarks>
/// <para>
/// All per-replica terminal-host files live flat under <c>~/.aspire/trmnl/</c>. Each
/// replica is identified by an 11-character base64url <em>replica id</em> derived from
/// the tuple <c>(normalized AppHost path, resource name, replica index)</c>. The four
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
/// The hash inputs intentionally <em>exclude</em> PID and any random suffix so the
/// path is stable across AppHost restarts: this lets external tools (the <c>aspire
/// terminal</c> CLI, the dashboard, future log scrapers) discover terminals by
/// listing <c>~/.aspire/trmnl/*.metadata.json</c> without an active backchannel. The
/// trade-off is that the host MUST pre-delete any stale <c>.sock</c> at the same path
/// before binding (see <c>TerminalHostControlListener</c> and <c>TerminalReplica</c>).
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

    /// <summary>
    /// Length in characters of the base64url replica identifier.
    /// </summary>
    /// <remarks>
    /// 8 bytes of xxHash3 → ceil(8 / 3) * 4 = 12 base64 chars, minus one '=' = 11.
    /// Same encoding as <c>Aspire.Hosting.Backchannel.BackchannelConstants.ComputeAppHostId</c>
    /// so the two hashing schemes look visually consistent in logs.
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
    /// Computes the 11-character base64url replica identifier from the
    /// <c>(appHostPath, resourceName, replicaIndex)</c> tuple.
    /// </summary>
    /// <param name="appHostPath">Full path to the AppHost project file (typically from <c>configuration["AppHost:FilePath"]</c>).</param>
    /// <param name="resourceName">Aspire model resource name the terminal host serves.</param>
    /// <param name="replicaIndex">Zero-based replica index of the parent resource.</param>
    public static string ComputeReplicaId(string appHostPath, string resourceName, int replicaIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(appHostPath);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentOutOfRangeException.ThrowIfNegative(replicaIndex);

        // NUL separators between tuple components so ("foo", "bar") cannot collide with
        // ("foob", "ar"). NormalizePath uppercases on Windows so casing differences
        // between FileInfo.FullName (CLI side) and MSBuild metadata (AppHost side)
        // don't produce different ids on case-insensitive NTFS/ReFS. Mirrors the rule
        // in src/Shared/BackchannelConstants.cs::NormalizePath.
        var composed =
            NormalizePath(appHostPath)
            + "\0"
            + resourceName
            + "\0"
            + replicaIndex.ToString(CultureInfo.InvariantCulture);

        var xxHash = new XxHash3();
        xxHash.Append(Encoding.UTF8.GetBytes(composed));
        var hash = xxHash.GetCurrentHash();

        return ToBase64UrlIdentifier(hash.AsSpan(0, ReplicaIdByteCount));
    }

    /// <summary>
    /// Gets the absolute socket path for a given replica id and sockpurpose.
    /// Format: <c>{home}/.aspire/trmnl/{replicaId}.{sockPurpose}.sock</c>.
    /// </summary>
    /// <param name="homeDirectory">User's profile directory.</param>
    /// <param name="replicaId">Output of <see cref="ComputeReplicaId(string, string, int)"/>.</param>
    /// <param name="sockPurpose">One of <see cref="ProducerSockPurpose"/>, <see cref="ConsumerSockPurpose"/>, <see cref="ControlSockPurpose"/>.</param>
    public static string GetSocketPath(string homeDirectory, string replicaId, string sockPurpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(replicaId);
        ArgumentException.ThrowIfNullOrEmpty(sockPurpose);
        return Path.Combine(GetTrmnlDirectory(homeDirectory), $"{replicaId}.{sockPurpose}.sock");
    }

    /// <summary>
    /// Gets the absolute metadata-sidecar path for a given replica id.
    /// Format: <c>{home}/.aspire/trmnl/{replicaId}.metadata.json</c>.
    /// </summary>
    public static string GetMetadataPath(string homeDirectory, string replicaId)
    {
        ArgumentException.ThrowIfNullOrEmpty(replicaId);
        return Path.Combine(GetTrmnlDirectory(homeDirectory), $"{replicaId}.{MetadataSuffix}");
    }

    private static string NormalizePath(string path)
    {
        // On Windows: NTFS/ReFS treat paths as case-insensitive but neither FileInfo.FullName
        // (CLI side) nor MSBuild metadata (AppHost side) canonicalizes casing against disk,
        // so segments can differ between the two sides. ToUpperInvariant is deterministic
        // across machines and cultures, so both sides always agree on the hash input.
        // On macOS/Linux paths are returned as-is — APFS can be case-sensitive and Linux
        // filesystems are case-sensitive by default.
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
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
