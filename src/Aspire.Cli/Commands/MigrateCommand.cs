// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Interaction;
using Aspire.Cli.Migrations;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Brings the current project up to the latest recommended Aspire conventions by detecting and
/// applying the available <see cref="IMigration"/> providers (for example, moving a legacy
/// TypeScript <c>apphost.ts</c> onto the modern <c>apphost.mts</c> layout).
/// </summary>
/// <remarks>
/// The command itself is migration-agnostic: it enumerates every registered <see cref="IMigration"/>,
/// lists the ones that apply, confirms, and applies them in <see cref="IMigration.Order"/>. New
/// migrations are added by registering an <see cref="IMigration"/> in DI — no changes here.
/// See https://github.com/microsoft/aspire/issues/17842.
/// </remarks>
internal sealed class MigrateCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private readonly IEnumerable<IMigration> _migrations;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<MigrateCommand> _logger;

    private static readonly Option<bool> s_yesOption = new("--yes", "-y")
    {
        Description = MigrateCommandStrings.YesOptionDescription
    };

    public MigrateCommand(
        IEnumerable<IMigration> migrations,
        IInteractionService interactionService,
        ILogger<MigrateCommand> logger,
        CommonCommandServices services)
        : base("migrate", MigrateCommandStrings.Description, services)
    {
        _migrations = migrations;
        _interactionService = interactionService;
        _logger = logger;

        Options.Add(s_yesOption);

        // Mirrors DestroyCommand/UpdateCommand: without --yes in a non-interactive context the
        // confirmation prompt would throw InteractiveInputNotSupported and surface as a generic
        // error. The validator produces the actionable "requires --yes in non-interactive mode"
        // message instead.
        AddNonInteractiveRequiresYesValidator(this, s_yesOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        // Detect once up front so we can show the full set before asking for a single confirmation.
        var pending = new List<(IMigration Migration, MigrationDescriptor Descriptor)>();
        foreach (var migration in _migrations.OrderBy(m => m.Order))
        {
            MigrationDescriptor? descriptor;
            try
            {
                descriptor = await migration.DetectAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A broken migration provider must not block the others or the command.
                _logger.LogDebug(ex, "Migration '{MigrationId}' detection failed", migration.Id);
                continue;
            }

            if (descriptor is not null)
            {
                pending.Add((migration, descriptor));
            }
        }

        if (pending.Count == 0)
        {
            _interactionService.DisplaySubtleMessage(MigrateCommandStrings.NothingToMigrate);
            return CommandResult.Success();
        }

        if (!parseResult.GetValue(s_yesOption))
        {
            _interactionService.DisplayMessage(KnownEmojis.Gear, MigrateCommandStrings.AvailableMigrationsHeader);
            foreach (var (_, descriptor) in pending)
            {
                _interactionService.DisplaySubtleMessage($"  - {descriptor.Title}");
            }

            var confirmed = await _interactionService.PromptConfirmAsync(MigrateCommandStrings.ConfirmApplyPrompt, cancellationToken: cancellationToken);
            if (!confirmed)
            {
                _interactionService.DisplaySubtleMessage(MigrateCommandStrings.MigrationCancelled);
                return CommandResult.Success();
            }
        }

        foreach (var (migration, _) in pending)
        {
            await migration.ApplyAsync(cancellationToken);
        }

        return CommandResult.Success();
    }
}
