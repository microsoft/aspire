// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Describes one Aspire CLI installation, as surfaced by
/// <c>aspire doctor --format json</c>. Each entry corresponds to a single
/// binary either running this process or discovered on the system.
/// </summary>
/// <remarks>
/// <para>
/// The JSON shape is part of the <c>installations</c> property in the
/// <c>aspire doctor --format json</c> contract. Fields use camelCase wire names via
/// <see cref="JsonPropertyNameAttribute"/> applied explicitly here so the
/// schema stays decoupled from the project-wide camelCase policy: another
/// process may parse this output across CLI versions and we don't want to
/// rename fields by changing a global option.
/// </para>
/// <para>
/// Nullable fields may be <see langword="null"/> for any row, including
/// rows with <see cref="InstallationInfoStatus.Ok"/>. For example, a legacy
/// peer may respond through the <c>--version</c> fallback and leave
/// <see cref="Channel"/> unknown. Consumers should treat null fields as
/// "unknown for this row" regardless of <see cref="Status"/>.
/// </para>
/// </remarks>
internal sealed record InstallationInfo
{
    /// <summary>
    /// Absolute path of the CLI binary as discovered (i.e., the path that
    /// appeared in <c>$PATH</c> or a well-known location). May be a symlink;
    /// resolved canonical form is in <see cref="CanonicalPath"/>.
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

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
    /// </summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>
    /// Install route as recorded by the route's own sidecar
    /// (<c>.aspire-install.json</c>). Wire string from
    /// <see cref="InstallSourceExtensions.ToWireString"/>. May be
    /// <see langword="null"/> for PATH discoveries whose install metadata
    /// sidecar is missing or invalid — see <see cref="Status"/>.
    /// </summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>
    /// Relationship between this binary and the user's <c>$PATH</c>.
    /// See <see cref="InstallationPathStatus"/>.
    /// </summary>
    [JsonPropertyName("pathStatus")]
    public string PathStatus { get; init; } = InstallationPathStatus.NotOnPath;

    /// <summary>
    /// Lifecycle status for the row. <c>ok</c> means the binary is usable
    /// and any non-null fields on the row are correct, but nullable fields
    /// may still be absent. <c>notProbed</c> means the binary was listed but
    /// intentionally not executed because required install metadata was
    /// missing or invalid. <c>failed</c> means a probe was attempted but the
    /// peer did not return usable data. Wire values are kept lowercase for
    /// stability.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Free-form reason explaining a non-<c>ok</c> status; included only
    /// when present.
    /// </summary>
    [JsonPropertyName("statusReason")]
    public string? StatusReason { get; init; }
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
/// Parses rows from the doctor installation discovery wire contract.
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
            Path = GetStringOr("path", string.Empty),
            CanonicalPath = GetOptionalString("canonicalPath"),
            Version = GetOptionalString("version"),
            Channel = GetOptionalString("channel"),
            Route = GetOptionalString("route"),
            PathStatus = pathStatus,
            Status = GetStringOr("status", InstallationInfoStatus.Ok),
            StatusReason = GetOptionalString("statusReason"),
        };
    }
}
