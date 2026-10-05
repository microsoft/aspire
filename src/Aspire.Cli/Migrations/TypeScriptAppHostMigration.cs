// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Migrations;

/// <summary>
/// Migrates a legacy TypeScript AppHost (<c>apphost.ts</c> importing the generated SDK from
/// <c>./.modules/aspire.js</c>) to the modern <c>apphost.mts</c> layout (importing from
/// <c>./.aspire/modules/aspire.mjs</c>).
/// </summary>
/// <remarks>
/// The legacy layout keeps working via the compatibility path in
/// <see cref="GuestAppHostProject"/>, so this migration is the user-facing, automated way to move
/// onto the recommended format. See https://github.com/microsoft/aspire/issues/17842.
/// </remarks>
internal sealed class TypeScriptAppHostMigration : IMigration
{
    private const string TsConfigFileName = "tsconfig.apphost.json";
    private const string PackageJsonFileName = "package.json";
    private static readonly string[] s_eslintConfigFileNames = ["eslint.config.mjs", "eslint.config.js"];

    private readonly IProjectLocator _projectLocator;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly IInteractionService _interactionService;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<TypeScriptAppHostMigration> _logger;

    public TypeScriptAppHostMigration(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        IAppHostProjectFactory projectFactory,
        IInteractionService interactionService,
        CliExecutionContext executionContext,
        ILogger<TypeScriptAppHostMigration> logger)
    {
        _projectLocator = projectLocator;
        _languageDiscovery = languageDiscovery;
        _projectFactory = projectFactory;
        _interactionService = interactionService;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "typescript-apphost-mts";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public async Task<MigrationDescriptor?> DetectAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var appHostFile = await ResolveLegacyAppHostAsync(context, cancellationToken);
        if (appHostFile is null)
        {
            return null;
        }

        return new MigrationDescriptor
        {
            Title = string.Format(
                CultureInfo.CurrentCulture,
                MigrationStrings.TypeScriptMigrationTitleFormat,
                LegacyTypeScriptAppHost.LegacyAppHostFileName,
                LegacyTypeScriptAppHost.ModernAppHostFileName),
            Detail = string.Format(
                CultureInfo.CurrentCulture,
                DoctorCommandStrings.LegacyTypeScriptAppHostMessageFormat,
                appHostFile.FullName),
            Metadata = new JsonObject
            {
                ["language"] = KnownLanguageId.TypeScript,
                ["appHostPath"] = appHostFile.FullName
            }
        };
    }

    /// <inheritdoc />
    public async Task ApplyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        // Re-resolve rather than trusting an earlier DetectAsync result: applying must be safe to
        // run on its own and a no-op when there is nothing (left) to migrate.
        var appHostFile = await ResolveLegacyAppHostAsync(context, cancellationToken);
        if (appHostFile?.Directory is not { Exists: true } appHostDirectory)
        {
            return;
        }

        var modernAppHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, LegacyTypeScriptAppHost.ModernAppHostFileName));

        await _interactionService.ShowStatusAsync(
            MigrationStrings.MigratingStatus,
            () =>
            {
                MigrateFilesOnDisk(appHostFile, modernAppHostFile, appHostDirectory);
                return Task.FromResult(true);
            },
            emoji: KnownEmojis.Gear);

        _interactionService.DisplaySuccess(string.Format(
            CultureInfo.CurrentCulture,
            MigrationStrings.MigrationSucceededFormat,
            appHostFile.Name,
            modernAppHostFile.Name));

        // Regenerate the SDK into the modern `.aspire/modules/` folder so the project is
        // immediately runnable. This is best-effort: if the toolchain isn't available the
        // file migration above still stands and the user can run `aspire restore` later.
        await RegenerateSdkAsync(modernAppHostFile, appHostDirectory, cancellationToken);
    }

    /// <summary>
    /// Resolves the current TypeScript AppHost and returns it only when it is a legacy
    /// <c>apphost.ts</c> in a legacy layout (no modern <c>apphost.mts</c> sibling). Returns
    /// <see langword="null"/> otherwise.
    /// </summary>
    private async Task<FileInfo?> ResolveLegacyAppHostAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var appHostFile = context.AppHostFile;
        if (appHostFile is null)
        {
            appHostFile = await LegacyTypeScriptAppHost.ResolveTypeScriptAppHostAsync(
                _projectLocator, _languageDiscovery, _executionContext.WorkingDirectory, _logger, cancellationToken);
        }
        else if (!TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(_languageDiscovery.GetLanguageByFile(appHostFile)))
        {
            return null;
        }

        if (appHostFile?.Directory is not { Exists: true } appHostDirectory ||
            !LegacyTypeScriptAppHost.IsLegacyAppHostFile(appHostFile) ||
            !LegacyTypeScriptAppHost.IsLegacyLayout(appHostDirectory.FullName))
        {
            return null;
        }

        return appHostFile;
    }

    /// <summary>
    /// Performs the on-disk migration: rewrites metadata to point at the modern files, renames the
    /// AppHost, rewrites its SDK imports, and removes the legacy <c>.modules/</c> folder (regenerated
    /// under <c>.aspire/modules/</c> afterwards).
    /// </summary>
    private void MigrateFilesOnDisk(FileInfo legacyAppHostFile, FileInfo modernAppHostFile, DirectoryInfo appHostDirectory)
    {
        // Keep the destructive AppHost swap last: if an unexpected failure occurs while rewriting
        // metadata, the user-authored apphost.ts is still available and the migration can be retried.
        var modernAppHostContent = LegacyTypeScriptAppHost.RewriteAppHostContent(File.ReadAllText(legacyAppHostFile.FullName));

        // 1. Update aspire.config.json's appHost.path. We edit the JSON node directly rather than
        //    round-tripping through AspireConfigFile.Save so that unrelated config keys/values
        //    (profiles, packages, and any properties the typed model doesn't know about) are
        //    preserved. The file is re-serialized with indentation, so exact original whitespace
        //    is not retained.
        UpdateConfigAppHostPath(appHostDirectory);

        // 2. Update tsconfig.apphost.json include entries to point at the modern files.
        UpdateTsConfigIncludes(appHostDirectory);

        // 3. Update metadata that can reference the AppHost file name directly.
        UpdatePackageJsonScripts(appHostDirectory);
        UpdateEslintConfigFiles(appHostDirectory);

        // 4. Write the new apphost.mts and only then remove apphost.ts.
        File.WriteAllText(modernAppHostFile.FullName, modernAppHostContent);
        legacyAppHostFile.Delete();

        // 5. Remove the legacy .modules folder; the modern .aspire/modules is regenerated next.
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
                pathValue.GetValueKind() is not JsonValueKind.String)
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

            File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException ex)
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
            // tsconfig files are JSONC. Skipping comments makes common files parse successfully,
            // but comments are not preserved when this migration re-serializes the include array.
            if (JsonNode.Parse(
                    File.ReadAllText(tsConfigPath),
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    }) is not JsonObject root ||
                root["include"] is not JsonArray include)
            {
                return;
            }

            var rewritten = new JsonArray();
            foreach (var entry in include)
            {
                if (entry is JsonValue value && value.GetValueKind() is JsonValueKind.String)
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
            File.WriteAllText(tsConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to update include entries in {TsConfigPath} during migration", tsConfigPath);
        }
    }

    private void UpdatePackageJsonScripts(DirectoryInfo appHostDirectory)
    {
        var packageJsonPath = Path.Combine(appHostDirectory.FullName, PackageJsonFileName);
        if (!File.Exists(packageJsonPath))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(packageJsonPath)) is not JsonObject root ||
                root["scripts"] is not JsonObject scripts)
            {
                return;
            }

            var changed = false;
            foreach (var script in scripts.ToArray())
            {
                if (script.Value is JsonValue value &&
                    value.GetValueKind() is JsonValueKind.String)
                {
                    var current = value.GetValue<string>();
                    var rewritten = LegacyTypeScriptAppHost.RewriteAppHostFileNameReferences(current);
                    if (!string.Equals(current, rewritten, StringComparison.Ordinal))
                    {
                        scripts[script.Key] = (JsonNode?)JsonValue.Create(rewritten);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                File.WriteAllText(packageJsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to update script entries in {PackageJsonPath} during migration", packageJsonPath);
        }
    }

    private static void UpdateEslintConfigFiles(DirectoryInfo appHostDirectory)
    {
        foreach (var configFileName in s_eslintConfigFileNames)
        {
            var configPath = Path.Combine(appHostDirectory.FullName, configFileName);
            if (!File.Exists(configPath))
            {
                continue;
            }

            var current = File.ReadAllText(configPath);
            var rewritten = LegacyTypeScriptAppHost.RewriteAppHostFileNameReferences(current);
            if (!string.Equals(current, rewritten, StringComparison.Ordinal))
            {
                File.WriteAllText(configPath, rewritten);
            }
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
                MigrationStrings.RegeneratingStatus,
                async () => await guestProject.BuildAndGenerateSdkAsync(appHostDirectory, cancellationToken: cancellationToken),
                emoji: KnownEmojis.Gear);

            if (!success)
            {
                _interactionService.DisplayMessage(
                    KnownEmojis.Warning,
                    $"[yellow]{Markup.Escape(MigrationStrings.RegenerateFailedWarning)}[/]",
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
                $"[yellow]{Markup.Escape(MigrationStrings.RegenerateFailedWarning)}[/]",
                allowMarkup: true);
        }
    }
}
