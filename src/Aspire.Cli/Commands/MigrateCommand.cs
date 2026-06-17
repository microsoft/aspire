// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Migrates a legacy TypeScript AppHost (<c>apphost.ts</c> importing the generated SDK from
/// <c>./.modules/aspire.js</c>) to the modern <c>apphost.mts</c> layout (importing from
/// <c>./.aspire/modules/aspire.mjs</c>).
/// </summary>
/// <remarks>
/// The legacy layout keeps working via the compatibility path in
/// <see cref="GuestAppHostProject"/>, so this command is the user-facing, automated way to move
/// onto the recommended format. See https://github.com/microsoft/aspire/issues/17842.
/// </remarks>
internal sealed class MigrateCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private const string TsConfigFileName = "tsconfig.apphost.json";

    private readonly IProjectLocator _projectLocator;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<MigrateCommand> _logger;

    private static readonly Option<bool> s_yesOption = new("--yes", "-y")
    {
        Description = MigrateCommandStrings.YesOptionDescription
    };

    public MigrateCommand(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        IAppHostProjectFactory projectFactory,
        IInteractionService interactionService,
        ILogger<MigrateCommand> logger,
        CommonCommandServices services)
        : base("migrate", MigrateCommandStrings.Description, services)
    {
        _projectLocator = projectLocator;
        _languageDiscovery = languageDiscovery;
        _projectFactory = projectFactory;
        _interactionService = interactionService;
        _logger = logger;

        Options.Add(s_yesOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var appHostFile = await LegacyTypeScriptAppHost.ResolveTypeScriptAppHostAsync(
            _projectLocator, _languageDiscovery, ExecutionContext.WorkingDirectory, _logger, cancellationToken);
        if (appHostFile?.Directory is not { Exists: true } appHostDirectory ||
            !LegacyTypeScriptAppHost.IsLegacyAppHostFile(appHostFile) ||
            !LegacyTypeScriptAppHost.IsLegacyLayout(appHostDirectory.FullName))
        {
            // Nothing to do — either no TypeScript AppHost, or it's already on apphost.mts.
            _interactionService.DisplaySubtleMessage(MigrateCommandStrings.NothingToMigrate);
            return CommandResult.Success();
        }

        var modernAppHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, LegacyTypeScriptAppHost.ModernAppHostFileName));

        if (!parseResult.GetValue(s_yesOption))
        {
            var prompt = string.Format(
                CultureInfo.CurrentCulture,
                MigrateCommandStrings.ConfirmPromptFormat,
                appHostFile.Name,
                modernAppHostFile.Name);

            var confirmed = await _interactionService.PromptConfirmAsync(prompt, cancellationToken: cancellationToken);
            if (!confirmed)
            {
                _interactionService.DisplaySubtleMessage(MigrateCommandStrings.MigrationCancelled);
                return CommandResult.Success();
            }
        }

        await _interactionService.ShowStatusAsync(
            MigrateCommandStrings.MigratingStatus,
            () =>
            {
                MigrateFilesOnDisk(appHostFile, modernAppHostFile, appHostDirectory);
                return Task.FromResult(true);
            },
            emoji: KnownEmojis.Gear);

        _interactionService.DisplaySuccess(string.Format(
            CultureInfo.CurrentCulture,
            MigrateCommandStrings.MigrationSucceededFormat,
            appHostFile.Name,
            modernAppHostFile.Name));

        // Regenerate the SDK into the modern `.aspire/modules/` folder so the project is
        // immediately runnable. This is best-effort: if the toolchain isn't available the
        // file migration above still stands and the user can run `aspire restore` later.
        await RegenerateSdkAsync(modernAppHostFile, appHostDirectory, cancellationToken);

        return CommandResult.Success();
    }

    /// <summary>
    /// Performs the on-disk migration: renames the AppHost, rewrites its SDK imports, points
    /// <c>aspire.config.json</c> and <c>tsconfig.apphost.json</c> at the modern files, and removes
    /// the legacy <c>.modules/</c> folder (regenerated under <c>.aspire/modules/</c> afterwards).
    /// </summary>
    private void MigrateFilesOnDisk(FileInfo legacyAppHostFile, FileInfo modernAppHostFile, DirectoryInfo appHostDirectory)
    {
        // 1. Rewrite the AppHost imports and write the new apphost.mts, then drop apphost.ts.
        var content = File.ReadAllText(legacyAppHostFile.FullName);
        File.WriteAllText(modernAppHostFile.FullName, LegacyTypeScriptAppHost.RewriteAppHostContent(content));
        legacyAppHostFile.Delete();

        // 2. Update aspire.config.json's appHost.path. We edit the JSON node directly rather than
        //    round-tripping through AspireConfigFile.Save so that unrelated config keys/values
        //    (profiles, packages, and any properties the typed model doesn't know about) are
        //    preserved. The file is re-serialized with indentation, so exact original whitespace
        //    is not retained.
        UpdateConfigAppHostPath(appHostDirectory);

        // 3. Update tsconfig.apphost.json include entries to point at the modern files.
        UpdateTsConfigIncludes(appHostDirectory);

        // 4. Remove the legacy .modules folder; the modern .aspire/modules is regenerated next.
        var legacyModulesDir = Path.Combine(appHostDirectory.FullName, LanguageInfo.LegacyGeneratedFolderName);
        if (Directory.Exists(legacyModulesDir))
        {
            Directory.Delete(legacyModulesDir, recursive: true);
        }
    }

    private void UpdateConfigAppHostPath(DirectoryInfo appHostDirectory)
    {
        var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
        var configPath = Path.Combine(configDirectory.FullName, AspireConfigFile.FileName);
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject root ||
                root["appHost"] is not JsonObject appHost ||
                appHost["path"] is not JsonValue pathValue ||
                pathValue.GetValueKind() is not System.Text.Json.JsonValueKind.String)
            {
                return;
            }

            var currentPath = pathValue.GetValue<string>();

            // Only touch a path that still references the legacy file name so we don't disturb a
            // path that has already been migrated or points elsewhere. Preserve any directory prefix.
            if (!currentPath.EndsWith(LegacyTypeScriptAppHost.LegacyAppHostFileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            appHost["path"] = string.Concat(
                currentPath.AsSpan(0, currentPath.Length - LegacyTypeScriptAppHost.LegacyAppHostFileName.Length),
                LegacyTypeScriptAppHost.ModernAppHostFileName);

            File.WriteAllText(configPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to update appHost.path in {ConfigPath} during migration", configPath);
        }
    }

    private void UpdateTsConfigIncludes(DirectoryInfo appHostDirectory)
    {
        var tsConfigPath = Path.Combine(appHostDirectory.FullName, TsConfigFileName);
        if (!File.Exists(tsConfigPath))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(tsConfigPath)) is not JsonObject root ||
                root["include"] is not JsonArray include)
            {
                return;
            }

            var rewritten = new JsonArray();
            foreach (var entry in include)
            {
                if (entry is JsonValue value && value.GetValueKind() is System.Text.Json.JsonValueKind.String)
                {
                    // Use JsonValue.Create + the non-generic Add(JsonNode?) overload so we stay
                    // trimming/AOT-safe (JsonArray.Add<T> on a string is flagged IL2026/IL3050).
                    rewritten.Add((JsonNode?)JsonValue.Create(LegacyTypeScriptAppHost.RewriteTsConfigIncludeEntry(value.GetValue<string>())));
                }
                else
                {
                    rewritten.Add(entry?.DeepClone());
                }
            }

            root["include"] = rewritten;
            File.WriteAllText(tsConfigPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to update include entries in {TsConfigPath} during migration", tsConfigPath);
        }
    }

    private async Task RegenerateSdkAsync(FileInfo modernAppHostFile, DirectoryInfo appHostDirectory, CancellationToken cancellationToken)
    {
        try
        {
            if (_projectFactory.TryGetProject(modernAppHostFile) is not GuestAppHostProject guestProject)
            {
                return;
            }

            var success = await _interactionService.ShowStatusAsync(
                MigrateCommandStrings.RegeneratingStatus,
                async () => await guestProject.BuildAndGenerateSdkAsync(appHostDirectory, cancellationToken: cancellationToken),
                emoji: KnownEmojis.Gear);

            if (!success)
            {
                _interactionService.DisplayMessage(
                    KnownEmojis.Warning,
                    $"[yellow]{Markup.Escape(MigrateCommandStrings.RegenerateFailedWarning)}[/]",
                    allowMarkup: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SDK regeneration after migration failed");
            _interactionService.DisplayMessage(
                KnownEmojis.Warning,
                $"[yellow]{Markup.Escape(MigrateCommandStrings.RegenerateFailedWarning)}[/]",
                allowMarkup: true);
        }
    }
}
