// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Helpers for detecting and describing the legacy TypeScript AppHost layout.
/// </summary>
/// <remarks>
/// TypeScript AppHosts scaffolded before the move to <c>apphost.mts</c> ship an
/// <c>apphost.ts</c> that imports the generated SDK from <c>./.modules/aspire.js</c>.
/// The newer recommended layout uses <c>apphost.mts</c> importing from
/// <c>./.aspire/modules/aspire.mjs</c>. The legacy layout continues to work (see
/// <see cref="GuestAppHostProject.ConvertGeneratedFilesForLegacyTypeScriptAppHost"/>),
/// so detection here is only used to nudge users toward migrating via
/// <c>aspire update --migrate</c>.
/// See: https://github.com/microsoft/aspire/issues/17842
/// </remarks>
internal static partial class LegacyTypeScriptAppHost
{
    /// <summary>
    /// The legacy TypeScript AppHost entry point file name.
    /// </summary>
    internal const string LegacyAppHostFileName = "apphost.ts";

    /// <summary>
    /// The modern TypeScript AppHost entry point file name.
    /// </summary>
    internal const string ModernAppHostFileName = "apphost.mts";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="appPath"/> contains a legacy
    /// <c>apphost.ts</c> AND no modern <c>apphost.mts</c> sibling. The absence of
    /// <c>apphost.mts</c> is what keeps the CLI on the legacy generated-file layout.
    /// </summary>
    internal static bool IsLegacyLayout(string appPath)
    {
        return File.Exists(Path.Combine(appPath, LegacyAppHostFileName)) &&
            !File.Exists(Path.Combine(appPath, ModernAppHostFileName));
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="appHostFile"/> is a legacy
    /// <c>apphost.ts</c> entry point.
    /// </summary>
    internal static bool IsLegacyAppHostFile(FileInfo appHostFile)
    {
        return appHostFile.Name.Equals(LegacyAppHostFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rewrites the contents of a legacy <c>apphost.ts</c> so its SDK imports target the modern
    /// layout. Legacy AppHosts import from <c>./.modules/aspire.js</c>; the modern layout uses
    /// <c>./.aspire/modules/aspire.mjs</c>. This is the inverse of
    /// <see cref="GuestAppHostProject.ConvertGeneratedFilesForLegacyTypeScriptAppHost"/>.
    /// </summary>
    /// <remarks>
    /// Example transform:
    /// <code>
    /// import { createBuilder } from './.modules/aspire.js';
    /// // becomes
    /// import { createBuilder } from './.aspire/modules/aspire.mjs';
    /// </code>
    /// All rewrites are anchored to the <c>.modules/</c> path segment so that only the generated
    /// SDK imports are touched. Unanchored extension replacements would corrupt unrelated user
    /// imports — e.g. <c>./database.js</c> contains the substring <c>base.js</c> and would
    /// otherwise become <c>./database.mjs</c>. The <c>.modules/</c> → <c>.aspire/modules/</c>
    /// substitution itself is safe because the modern path segment is <c>/modules/</c>
    /// (slash-prefixed), never <c>.modules/</c> (dot-prefixed).
    /// </remarks>
    internal static string RewriteAppHostContent(string content)
    {
        return content
            // Rewrite the known generated SDK imports (folder + extension) in one anchored step.
            .Replace(".modules/aspire.js", ".aspire/modules/aspire.mjs", StringComparison.Ordinal)
            .Replace(".modules/base.js", ".aspire/modules/base.mjs", StringComparison.Ordinal)
            .Replace(".modules/transport.js", ".aspire/modules/transport.mjs", StringComparison.Ordinal)
            // Move any remaining .modules/ references to .aspire/modules/ without altering file
            // extensions, so user imports outside the generated SDK are never rewritten.
            .Replace(".modules/", ".aspire/modules/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Rewrites a single <c>tsconfig.apphost.json</c> <c>include</c> entry from the legacy layout
    /// to the modern one (e.g. <c>apphost.ts</c> → <c>apphost.mts</c> and
    /// <c>.modules/aspire.ts</c> → <c>.aspire/modules/aspire.mts</c>). Entries that don't match
    /// the legacy shape are returned unchanged.
    /// </summary>
    internal static string RewriteTsConfigIncludeEntry(string entry)
    {
        var isLegacyGeneratedModule = entry.Contains(".modules/", StringComparison.Ordinal);
        var isLegacyAppHost = EndsWithPathSegment(entry, LegacyAppHostFileName);
        var rewritten = entry.Replace(".modules/", ".aspire/modules/", StringComparison.Ordinal);

        // Only files the migration moves on disk should change extensions. Other user includes
        // (for example src/**/*.ts) must stay covered by TypeScript after migration.
        if ((isLegacyAppHost || isLegacyGeneratedModule) &&
            rewritten.EndsWith(".ts", StringComparison.Ordinal) &&
            !rewritten.EndsWith(".mts", StringComparison.Ordinal) &&
            !rewritten.EndsWith(".d.ts", StringComparison.Ordinal))
        {
            rewritten = string.Concat(rewritten.AsSpan(0, rewritten.Length - ".ts".Length), ".mts");
        }

        return rewritten;
    }

    /// <summary>
    /// Rewrites standalone references to the legacy AppHost file name in text-based metadata files.
    /// </summary>
    internal static string RewriteAppHostFileNameReferences(string content)
    {
        return LegacyAppHostFileNameRegex().Replace(content, ModernAppHostFileName);
    }

    private static bool EndsWithPathSegment(string path, string segment)
    {
        if (!path.EndsWith(segment, StringComparison.Ordinal))
        {
            return false;
        }

        var segmentStart = path.Length - segment.Length;
        return segmentStart is 0 || path[segmentStart - 1] is '/' or '\\';
    }

    /// <summary>
    /// Resolves the TypeScript AppHost entry point for the current working directory, if any.
    /// Prefers the AppHost recorded in settings (<c>aspire.config.json</c>) and falls back to a
    /// recursive file-system scan. Returns <see langword="null"/> when no TypeScript AppHost can
    /// be located. Both <c>aspire update --migrate</c> and the <c>aspire doctor</c> legacy-layout check
    /// share this so detection stays in lockstep.
    /// </summary>
    /// <param name="projectLocator">Used to read the configured AppHost from settings.</param>
    /// <param name="languageDiscovery">Used to detect the language and locate the AppHost file.</param>
    /// <param name="workingDirectory">The directory to resolve the AppHost relative to.</param>
    /// <param name="logger">Logs diagnostics when resolution fails unexpectedly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<FileInfo?> ResolveTypeScriptAppHostAsync(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        DirectoryInfo workingDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuredAppHost = await projectLocator.GetAppHostFromSettingsAsync(cancellationToken);
            if (configuredAppHost is not null &&
                TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(languageDiscovery.GetLanguageByFile(configuredAppHost)))
            {
                return configuredAppHost;
            }

            var detectedLanguageId = await languageDiscovery.DetectLanguageRecursiveAsync(workingDirectory, cancellationToken);
            if (detectedLanguageId is null)
            {
                return null;
            }

            var detectedLanguage = languageDiscovery.GetLanguageById(detectedLanguageId.Value);
            if (!TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(detectedLanguage))
            {
                return null;
            }

            var discoveredPath = detectedLanguage?.FindInDirectory(workingDirectory.FullName);
            return discoveredPath is not null ? new FileInfo(discoveredPath) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve TypeScript AppHost");
            return null;
        }
    }

    [GeneratedRegex(@"\bapphost\.ts\b", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyAppHostFileNameRegex();
}
