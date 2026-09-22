// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Projects;

/// <summary>
/// Updates repository-local Aspire CLI references without installing a CLI executable.
/// </summary>
internal sealed class RepositoryToolUpdater(INpmRunner npmRunner, IInteractionService interactionService, ILogger<RepositoryToolUpdater> logger)
{
    internal const string DotNetPackageId = "Aspire.Cli";
    internal const string NpmPackageId = "@microsoft/aspire-cli";
    private static readonly string[] s_dependencySections = ["dependencies", "devDependencies", "optionalDependencies"];
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<IReadOnlyList<RepositoryToolManifest>> FindManifestsAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        logger.LogDebug("Finding repository CLI manifests from {Directory}", directory.FullName);
        var manifests = new List<RepositoryToolManifest>();
        var searchDotNet = true;
        var searchNpm = true;

        // Follow local-tool lookup order and isRoot, but never edit a manifest outside the
        // repository. A .git file marks a worktree just as a .git directory marks a checkout.
        // https://learn.microsoft.com/dotnet/core/tools/local-tools-how-to-use
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (searchDotNet)
            {
                foreach (var path in new[] { Path.Combine(current.FullName, ".config", "dotnet-tools.json"), Path.Combine(current.FullName, "dotnet-tools.json") })
                {
                    var manifest = await ReadManifestAsync(path, isNpm: false, cancellationToken);
                    if (manifest is null)
                    {
                        continue;
                    }

                    if (manifest.References.Count > 0)
                    {
                        manifests.Add(manifest);
                    }

                    if (manifest.References.Count > 0 || manifest.IsRoot)
                    {
                        searchDotNet = false;
                        break;
                    }
                }
            }

            if (searchNpm)
            {
                var manifest = await ReadManifestAsync(Path.Combine(current.FullName, "package.json"), isNpm: true, cancellationToken);
                if (manifest is { References.Count: > 0 })
                {
                    manifests.Add(manifest);
                    searchNpm = false;
                }
            }

            var gitPath = Path.Combine(current.FullName, ".git");
            if ((!searchDotNet && !searchNpm) || Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                break;
            }
        }

        return manifests;
    }

    public async Task UpdateAsync(IReadOnlyList<RepositoryToolManifest> manifests, PackageChannel channel, PromptBinding<bool> confirmBinding, CancellationToken cancellationToken)
    {
        var updateStep = await GetUpdateStepAsync(manifests, channel, cancellationToken);
        if (updateStep is null)
        {
            return;
        }

        interactionService.DisplayMessage(KnownEmojis.Package, updateStep.GetFormattedDisplayText(), allowMarkup: true);
        if (await interactionService.PromptConfirmAsync(UpdateCommandStrings.PerformUpdatesPrompt, confirmBinding, cancellationToken: cancellationToken))
        {
            await updateStep.Callback();
        }
    }

    /// <summary>
    /// Resolves repository CLI changes without writing files so they can join the project update plan.
    /// </summary>
    public async Task<UpdateStep?> GetUpdateStepAsync(IReadOnlyList<RepositoryToolManifest> manifests, PackageChannel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var updates = new List<(RepositoryToolManifest Manifest, RepositoryToolReference Reference, string Version)>();
        var skippedReference = false;
        foreach (var manifest in manifests)
        {
            string? targetVersion = null;
            foreach (var reference in manifest.References)
            {
                // Preserve simple npm range intent, e.g. "^13.5.0" or "~13.5.0". File,
                // workspace, alias, and compound ranges are not version pins we can safely rewrite.
                var prefix = manifest.IsNpm && reference.Version.StartsWith('^') ? "^"
                    : manifest.IsNpm && reference.Version.StartsWith('~') ? "~" : string.Empty;
                if (!SemVersion.TryParse(reference.Version[prefix.Length..], SemVersionStyles.Strict, out var currentVersion))
                {
                    interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture,
                        UpdateCommandStrings.UnsupportedToolVersionFormat, manifest.File.FullName, reference.Version));
                    skippedReference = true;
                    continue;
                }

                targetVersion ??= await GetTargetVersionAsync(manifest, channel, cancellationToken);
                var newVersion = SemVersion.Parse(targetVersion, SemVersionStyles.Strict);
                if (SemVersion.PrecedenceComparer.Compare(newVersion, currentVersion) == 0 ||
                    (channel.Type == PackageChannelType.Implicit && SemVersion.PrecedenceComparer.Compare(newVersion, currentVersion) < 0))
                {
                    continue;
                }

                updates.Add((manifest, reference, prefix + targetVersion));
            }
        }

        if (updates.Count == 0)
        {
            if (manifests.Count > 0 && !skippedReference)
            {
                interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, UpdateCommandStrings.RepositoryToolsUpToDate);
            }

            return null;
        }

        var displayText = string.Join(Environment.NewLine, updates.Select(update =>
            string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.RepositoryToolUpdateFormat,
                update.Manifest.File.FullName.EscapeMarkup(), update.Manifest.PackageId.EscapeMarkup(),
                update.Reference.Version.EscapeMarkup(), update.Version.EscapeMarkup())));
        return new RepositoryToolsUpdateStep(displayText, () => ApplyUpdatesAsync(updates, cancellationToken));
    }

    private async Task ApplyUpdatesAsync(
        IReadOnlyList<(RepositoryToolManifest Manifest, RepositoryToolReference Reference, string Version)> updates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changedManifests = new List<RepositoryToolManifest>();
        var originalFiles = new Dictionary<string, byte[]>();
        foreach (var manifestUpdates in updates.GroupBy(update => update.Manifest))
        {
            var original = manifestUpdates.Key;
            var manifest = await ReadManifestAsync(original.File.FullName, original.IsNpm, cancellationToken);
            // Guest regeneration can edit unrelated package.json fields before this step.
            // Preserve those edits, but reject changes to the CLI references the user approved.
            if (manifest is null || manifest.IsRoot != original.IsRoot ||
                !manifest.References.Select(reference => (reference.Properties.GetPath(), reference.Key, reference.Version))
                    .SequenceEqual(original.References.Select(reference => (reference.Properties.GetPath(), reference.Key, reference.Version))))
            {
                throw new ProjectUpdaterException(string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.ToolManifestChangedFormat, original.File.FullName));
            }

            originalFiles.Add(manifest.File.FullName, await File.ReadAllBytesAsync(manifest.File.FullName, cancellationToken));
            foreach (var (_, reference, version) in manifestUpdates)
            {
                var currentReference = manifest.References.Single(candidate =>
                    candidate.Key == reference.Key && candidate.Properties.GetPath() == reference.Properties.GetPath());
                currentReference.Properties[currentReference.Key] = version;
            }
            changedManifests.Add(manifest);
        }

        try
        {
            foreach (var manifest in changedManifests)
            {
                var newLine = manifest.OriginalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var content = manifest.Content.ToJsonString(s_jsonOptions).ReplaceLineEndings(newLine) + newLine;
                await File.WriteAllTextAsync(manifest.File.FullName, content, cancellationToken);
            }
        }
        catch
        {
            // A failed write or cancellation must not leave only some of the manifests updated.
            foreach (var (path, content) in originalFiles)
            {
                await File.WriteAllBytesAsync(path, content, CancellationToken.None);
            }

            throw;
        }

        foreach (var manifest in changedManifests)
        {
            logger.LogDebug("Updated repository CLI reference in {ManifestPath}", manifest.File.FullName);
        }

        interactionService.DisplaySuccess(UpdateCommandStrings.RepositoryToolsUpdated);
        if (changedManifests.Any(manifest => !manifest.IsNpm))
        {
            interactionService.DisplayMessage(KnownEmojis.Information, UpdateCommandStrings.RestoreRepositoryDotNetTool);
        }
        if (changedManifests.Any(manifest => manifest.IsNpm))
        {
            interactionService.DisplayMessage(KnownEmojis.Information, UpdateCommandStrings.RestoreRepositoryNpmTool);
        }
    }

    private async Task<string> GetTargetVersionAsync(RepositoryToolManifest manifest, PackageChannel channel, CancellationToken cancellationToken)
    {
        // npm's stable dist-tag is independent of NuGet publication. For an explicit
        // non-stable channel, require the exact channel version to exist on npm rather
        // than silently switching the repository back to stable.
        var npmStable = manifest.IsNpm && channel.PinnedVersion is null &&
            (channel.Type == PackageChannelType.Implicit || string.Equals(channel.Name, PackageChannelNames.Stable, StringComparisons.ChannelName));
        string? version = null;
        if (!npmStable)
        {
            var packages = await channel.GetPackagesAsync(DotNetPackageId, manifest.File.Directory!, cancellationToken);
            version = packages
                .Where(package => string.Equals(package.Id, DotNetPackageId, StringComparisons.NuGetPackageId))
                .OrderByDescending(package => SemVersion.Parse(package.Version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
                .FirstOrDefault()?.Version;
            if (version is null)
            {
                throw new ProjectUpdaterException(string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.NoPackageFoundFormat, DotNetPackageId, channel.Name));
            }
        }

        if (manifest.IsNpm)
        {
            var package = npmRunner.IsAvailable
                ? await npmRunner.ResolvePackageAsync(NpmPackageId, version ?? "latest", cancellationToken)
                : null;
            if (package is null)
            {
                throw new ProjectUpdaterException(string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.FailedResolveNpmToolFormat, version ?? "latest"));
            }

            return package.Version.ToString();
        }

        return version ?? throw new ProjectUpdaterException(string.Format(CultureInfo.CurrentCulture,
            UpdateCommandStrings.NoPackageFoundFormat, DotNetPackageId, channel.Name));
    }

    private static async Task<RepositoryToolManifest?> ReadManifestAsync(string path, bool isNpm, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var originalContent = await File.ReadAllTextAsync(path, cancellationToken);
            var content = JsonNode.Parse(originalContent)?.AsObject() ?? throw new JsonException("Expected a JSON object.");
            var isRoot = !isNpm && content["isRoot"]?.GetValue<bool>() == true;
            var references = new List<RepositoryToolReference>();
            if (isNpm)
            {
                foreach (var section in s_dependencySections)
                {
                    if (content[section] is JsonObject dependencies && dependencies[NpmPackageId] is { } version)
                    {
                        references.Add(new(dependencies, NpmPackageId, version.GetValue<string>()));
                    }
                }
            }
            else if (content["tools"] is JsonObject tools)
            {
                foreach (var (name, tool) in tools)
                {
                    if (string.Equals(name, DotNetPackageId, StringComparisons.NuGetPackageId) && tool is JsonObject properties)
                    {
                        references.Add(new(properties, "version", properties["version"]?.GetValue<string>() ?? throw new JsonException("Missing tool version.")));
                    }
                }
            }

            return new(new FileInfo(path), content, originalContent, references, isNpm, isRoot);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.FailedReadToolManifestFormat, path, ex.Message));
        }
    }

    private sealed record RepositoryToolsUpdateStep(string DisplayText, Func<Task> Callback)
        : UpdateStep(UpdateCommandStrings.UpdateRepositoryTools, Callback)
    {
        public override string GetFormattedDisplayText() => DisplayText;
    }
}

internal sealed record RepositoryToolManifest(FileInfo File, JsonObject Content, string OriginalContent, IReadOnlyList<RepositoryToolReference> References, bool IsNpm, bool IsRoot)
{
    public string PackageId => IsNpm ? RepositoryToolUpdater.NpmPackageId : RepositoryToolUpdater.DotNetPackageId;
}

internal sealed record RepositoryToolReference(JsonObject Properties, string Key, string Version);
