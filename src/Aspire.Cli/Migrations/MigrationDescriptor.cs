// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Cli.Migrations;

/// <summary>
/// Describes a pending migration detected by <see cref="IMigration.DetectAsync"/>. The same
/// descriptor feeds two surfaces: <c>aspire update --migrate</c> lists <see cref="Title"/> before applying,
/// and <c>aspire doctor</c> reports <see cref="Detail"/> (plus <see cref="Metadata"/> for JSON
/// output) as a non-blocking warning.
/// </summary>
internal sealed class MigrationDescriptor
{
    /// <summary>
    /// A short, human-readable summary of what the migration does, shown as a list item by
    /// <c>aspire update --migrate</c> (e.g. <c>Migrate 'apphost.ts' to 'apphost.mts'</c>).
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// A fuller description used as the <c>aspire doctor</c> warning message, typically including
    /// the concrete location involved (e.g. the full path to the legacy AppHost file).
    /// </summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Optional structured metadata attached to the <c>aspire doctor --format json</c> output for
    /// programmatic consumers.
    /// </summary>
    public JsonObject? Metadata { get; init; }
}
