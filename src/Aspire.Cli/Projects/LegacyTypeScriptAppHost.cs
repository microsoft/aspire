// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
/// <c>aspire migrate</c>.
/// See: https://github.com/microsoft/aspire/issues/17842
/// </remarks>
internal static class LegacyTypeScriptAppHost
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
    /// The <c>.modules/</c> → <c>.aspire/modules/</c> substitution is safe because the modern
    /// path segment is <c>/modules/</c> (slash-prefixed), never <c>.modules/</c> (dot-prefixed).
    /// </remarks>
    internal static string RewriteAppHostContent(string content)
    {
        return content
            .Replace(".modules/", ".aspire/modules/", StringComparison.Ordinal)
            .Replace("aspire.js", "aspire.mjs", StringComparison.Ordinal)
            .Replace("base.js", "base.mjs", StringComparison.Ordinal)
            .Replace("transport.js", "transport.mjs", StringComparison.Ordinal);
    }

    /// <summary>
    /// Rewrites a single <c>tsconfig.apphost.json</c> <c>include</c> entry from the legacy layout
    /// to the modern one (e.g. <c>apphost.ts</c> → <c>apphost.mts</c> and
    /// <c>.modules/aspire.ts</c> → <c>.aspire/modules/aspire.mts</c>). Entries that don't match
    /// the legacy shape are returned unchanged.
    /// </summary>
    internal static string RewriteTsConfigIncludeEntry(string entry)
    {
        var rewritten = entry.Replace(".modules/", ".aspire/modules/", StringComparison.Ordinal);

        // Convert the TypeScript module/entry extensions (.ts → .mts) while leaving declaration
        // files (.d.ts) and already-modern (.mts) entries alone.
        if (rewritten.EndsWith(".ts", StringComparison.Ordinal) &&
            !rewritten.EndsWith(".mts", StringComparison.Ordinal) &&
            !rewritten.EndsWith(".d.ts", StringComparison.Ordinal))
        {
            rewritten = string.Concat(rewritten.AsSpan(0, rewritten.Length - ".ts".Length), ".mts");
        }

        return rewritten;
    }
}
