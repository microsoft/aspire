// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Describes one row in the Aspire CLI install table, as surfaced by
/// <c>aspire --info --format json</c> and <c>aspire --info --self --format json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same record shape is used by both surfaces:
/// </para>
/// <list type="bullet">
///   <item><c>aspire --info --format json</c> emits an <c>InfoOutput</c>
///     whose <c>installs</c> array contains <see cref="InstallationInfo"/>
///     rows that may include both real CLI installs and orphan-hive rows.</item>
///   <item><c>aspire --info --self --format json</c> emits a bare array with exactly
///     one element describing the running binary.</item>
/// </list>
/// <para>
/// Fields use camelCase wire names via <see cref="JsonPropertyNameAttribute"/>
/// applied explicitly here so the schema stays decoupled from the project-wide
/// camelCase policy: another process may parse this output across CLI versions
/// and we don't want to rename fields by changing a global option.
/// </para>
/// <para>
/// The two status axes — <see cref="Status"/> (lifecycle) and
/// <see cref="PathStatus"/> (PATH-axis) — are always orthogonal on the wire.
/// Human renderings may collapse them; programmatic consumers should switch
/// on each axis independently.
/// </para>
/// <para>
/// Nullable fields may be <see langword="null"/> for any row, including rows
/// with <see cref="InstallationInfoStatus.Ok"/>. For example, a peer probed via
/// the <c>--version</c> fallback may leave <see cref="Channel"/> unknown.
/// Consumers should treat null fields as "unknown for this row" regardless of
/// <see cref="Status"/>. Orphan-hive rows (<see cref="Kind"/> equals
/// <c>"orphan-hive"</c>) have a <see langword="null"/> <see cref="Path"/> by
/// design — they represent a hive directory on disk with no matching CLI binary.
/// </para>
/// </remarks>
internal sealed record InstallationInfo
{
    /// <summary>
    /// Stable identifier for this row within an aggregate <c>aspire --info</c>
    /// payload. Typically the channel (<c>stable</c>, <c>pr-17461</c>, ...) or
    /// the install source (<c>script</c>) for ordinary installs, and the hive
    /// directory name for orphan-hive rows. Duplicates are disambiguated by a
    /// <c>-N</c> suffix. Not populated by the <c>--self</c> surface (a single
    /// row needs no identifier).
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>
    /// Row kind discriminator. For real installs this mirrors the install
    /// source (<c>script</c>, <c>pr</c>, <c>winget</c>, <c>homebrew</c>,
    /// <c>dotnet-tool</c>, <c>localhive</c>, ...). For hive directories on
    /// disk that no discovered install claims, the kind is <c>orphan-hive</c>.
    /// Not populated by the <c>--self</c> surface.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    /// Absolute path of the CLI binary as discovered (i.e., the path that
    /// appeared in <c>$PATH</c> or a well-known location). May be a symlink;
    /// resolved canonical form is in <see cref="CanonicalPath"/>.
    /// <see langword="null"/> only for orphan-hive rows
    /// (<see cref="Kind"/> equals <c>"orphan-hive"</c>), which describe a hive
    /// directory with no matching binary.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Symlink-resolved absolute path of the binary. Used for identity /
    /// deduplication so that two PATH entries pointing at the same backing
    /// file render as a single row.
    /// </summary>
    [JsonPropertyName("canonicalPath")]
    public string? CanonicalPath { get; init; }

    /// <summary>
    /// CLI version string (e.g., <c>13.0.0-preview.1.25366.3</c>). Always
    /// populated for the row representing the running CLI; for peer rows it
    /// is populated only when the peer was successfully probed.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// Identity channel baked into the CLI assembly: one of
    /// <c>stable</c>, <c>staging</c>, <c>daily</c>, <c>local</c>, or
    /// <c>pr-&lt;N&gt;</c>. Always populated for the running row; for peer
    /// rows it is populated only when the peer was successfully probed.
    /// Orphan-hive rows carry the hive's channel name here.
    /// </summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>
    /// Install source as recorded by the source's own sidecar
    /// (<c>.aspire-install.json</c>). Wire string from
    /// <see cref="InstallSourceExtensions.ToWireString"/>. May be
    /// <see langword="null"/> for PATH discoveries whose install metadata
    /// sidecar is missing or invalid — see <see cref="Status"/>.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>
    /// Hive directory under <c>~/.aspire/hives/&lt;channel&gt;</c> when the
    /// row corresponds to a hive on disk; <see langword="null"/> when the
    /// install has no matching hive. For orphan-hive rows this is the hive
    /// directory itself.
    /// </summary>
    [JsonPropertyName("hive")]
    public string? Hive { get; init; }

    /// <summary>
    /// Relationship between this binary and the user's <c>$PATH</c>.
    /// See <see cref="InstallationPathStatus"/>. Orthogonal to
    /// <see cref="Status"/>. Defaults to <see cref="InstallationPathStatus.NotOnPath"/>
    /// for orphan-hive rows.
    /// </summary>
    [JsonPropertyName("pathStatus")]
    public string PathStatus { get; init; } = InstallationPathStatus.NotOnPath;

    /// <summary>
    /// Lifecycle status for the row. <c>ok</c> means the binary is usable
    /// and any non-null fields on the row are correct, but nullable fields
    /// may still be absent. <c>notProbed</c> means the binary was listed but
    /// intentionally not executed because required install metadata was
    /// missing or invalid. <c>failed</c> means a probe was attempted but the
    /// peer did not return usable data. <c>no install found</c> is reserved
    /// for orphan-hive rows. Wire values are kept lowercase for stability.
    /// Orthogonal to <see cref="PathStatus"/>.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Free-form reason explaining a non-<c>ok</c> status; included only
    /// when present.
    /// </summary>
    [JsonPropertyName("statusReason")]
    public string? StatusReason { get; init; }

    /// <summary>
    /// Package manager that owns this install, when applicable. Typically
    /// <c>winget</c>, <c>homebrew</c>, or <c>dotnet-tool</c> — derived from
    /// <see cref="Source"/>. <see langword="null"/> for installs not managed
    /// by a recognised package manager.
    /// </summary>
    [JsonPropertyName("managedBy")]
    public string? ManagedBy { get; init; }
}

/// <summary>
/// Wire constants for <see cref="InstallationInfo.Status"/>.
/// </summary>
internal static class InstallationInfoStatus
{
    /// <summary>Usable row; nullable fields may still be absent.</summary>
    public const string Ok = "ok";

    /// <summary>Row was discovered but not probed because required install metadata was missing or invalid.</summary>
    public const string NotProbed = "notProbed";

    /// <summary>Probe was attempted, but the peer did not cooperate (timeout, non-zero exit, malformed JSON, etc.).</summary>
    public const string Failed = "failed";

    /// <summary>Row describes an orphan hive directory on disk that no discovered install claims.</summary>
    public const string NoInstallFound = "no install found";
}

/// <summary>
/// Wire constants for <see cref="InstallationInfo.PathStatus"/>.
/// </summary>
internal static class InstallationPathStatus
{
    /// <summary>This binary is the first <c>aspire</c> entry resolved from <c>$PATH</c>.</summary>
    public const string Active = "active";

    /// <summary>This binary is on <c>$PATH</c>, but an earlier <c>aspire</c> entry shadows it.</summary>
    public const string Shadowed = "shadowed";

    /// <summary>This binary was not discovered through <c>$PATH</c>.</summary>
    public const string NotOnPath = "notOnPath";
}

/// <summary>
/// Parses rows from the install discovery wire contract.
/// </summary>
internal static class InstallationInfoParser
{
    public static InstallationInfo Parse(JsonElement row)
    {
        string GetStringOr(string property, string fallback)
        {
            return row.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;
        }

        string? GetOptionalString(string property)
        {
            return row.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }

        var pathStatus = GetOptionalString("pathStatus") is { Length: > 0 } parsedPathStatus
            ? parsedPathStatus
            : InstallationPathStatus.NotOnPath;

        return new InstallationInfo
        {
            Id = GetOptionalString("id"),
            Kind = GetOptionalString("kind"),
            Path = GetOptionalString("path"),
            CanonicalPath = GetOptionalString("canonicalPath"),
            Version = GetOptionalString("version"),
            Channel = GetOptionalString("channel"),
            Source = GetOptionalString("source"),
            Hive = GetOptionalString("hive"),
            PathStatus = pathStatus,
            Status = GetStringOr("status", InstallationInfoStatus.Ok),
            StatusReason = GetOptionalString("statusReason"),
            ManagedBy = GetOptionalString("managedBy"),
        };
    }
}
