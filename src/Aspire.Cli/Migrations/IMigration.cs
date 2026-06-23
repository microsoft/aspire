// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Migrations;

/// <summary>
/// A single, self-contained migration that can detect whether it applies to the current project
/// and, when it does, bring the project up to the latest recommended Aspire conventions.
/// </summary>
/// <remarks>
/// Migrations are the shared unit of work behind both <c>aspire update --migrate</c> (which applies them)
/// and the <c>aspire doctor</c> pending-migrations check (which only detects them). New kinds of
/// migration — a future Java AppHost layout change, a C# version bump, or an integration breaking
/// change — are added by implementing this interface and registering it in DI; neither the
/// <c>migrate</c> command nor the doctor check need to change.
/// See: https://github.com/microsoft/aspire/issues/17842
/// </remarks>
internal interface IMigration
{
    /// <summary>
    /// A stable, machine-readable identifier for this migration (e.g. <c>typescript-apphost-mts</c>).
    /// Surfaced as the check name in <c>aspire doctor --format json</c>, so treat it as a contract.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Relative execution order. Lower values run first. Use this when one migration must be applied
    /// before another (e.g. a layout move before a dependent rewrite).
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Detects whether this migration applies to the current project.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="MigrationDescriptor"/> describing what would change, or <see langword="null"/>
    /// when there is nothing to migrate. Detection must be side-effect free so it is safe to run
    /// repeatedly (e.g. from <c>aspire doctor</c>).
    /// </returns>
    Task<MigrationDescriptor?> DetectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies the migration to the current project. Implementations own their own progress,
    /// success, and best-effort failure messaging via <c>IInteractionService</c>. Applying must be
    /// idempotent: if there is nothing to migrate (for example because a previous run already
    /// completed), this should be a no-op.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyAsync(CancellationToken cancellationToken);
}
