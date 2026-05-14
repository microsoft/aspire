// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Describes one Aspire CLI installation, as surfaced by
/// <c>aspire info</c>. Each entry corresponds to a single binary either
/// running this process or discovered on the system.
/// </summary>
/// <remarks>
/// <para>
/// The JSON shape is part of the <c>aspire info --format json</c> contract:
/// the array is always emitted (single-element for the default no-args
/// invocation, multi-element for <c>--all</c>) so downstream consumers see a
/// stable schema. Fields use camelCase wire names via
/// <see cref="JsonPropertyNameAttribute"/> applied explicitly here so the
/// schema stays decoupled from the project-wide camelCase policy: another
/// process may parse this output across CLI versions and we don't want to
/// rename fields by changing a global option.
/// </para>
/// <para>
/// The <c>route</c> and other fields may be <see langword="null"/> for
/// untrusted PATH discoveries that were listed but not probed (see the
/// trust-gate behavior in <c>InstallationDiscovery</c>). Consumers should
/// treat null fields as "unknown for this row", not as errors.
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
    /// <see langword="null"/> for untrusted PATH discoveries (no sidecar
    /// present or unreadable) — see <see cref="Status"/>.
    /// </summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>
    /// Whether this binary is the one the current shell would resolve as
    /// <c>aspire</c> (canonical-path comparison against the first match in
    /// <c>$PATH</c>). False when no <c>aspire</c> is on <c>$PATH</c>.
    /// </summary>
    [JsonPropertyName("isOnPath")]
    public bool IsOnPath { get; init; }

    /// <summary>
    /// Lifecycle status for the row. <c>ok</c> means version / channel /
    /// route were populated successfully; <c>notProbed</c> means we found
    /// the binary but did not (or could not) ask it to self-describe (e.g.
    /// untrusted PATH discovery or peer-probe timeout). Wire values are
    /// kept lowercase for stability.
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
    /// <summary>Fully populated row.</summary>
    public const string Ok = "ok";

    /// <summary>Row was discovered but not probed (e.g., trust gate or peer-probe failure).</summary>
    public const string NotProbed = "notProbed";
}
