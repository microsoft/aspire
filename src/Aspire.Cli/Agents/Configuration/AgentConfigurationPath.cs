// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Resolves file and ancestor-directory links without replacing user-owned symlinks.
/// </summary>
internal static class AgentConfigurationPath
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Resolve(string path) => ResolveCasing(Resolve(path, 0));

    private static string Resolve(string path, int depth)
    {
        if (depth > 40)
        {
            throw new AgentConfigurationException(string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.UnsafeLink, path));
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var resolved = root;
        for (var index = 0; index < parts.Length; index++)
        {
            resolved = Path.Combine(resolved, parts[index]);
            FileSystemInfo info = index < parts.Length - 1 || Directory.Exists(resolved)
                ? new DirectoryInfo(resolved)
                : new FileInfo(resolved);
            if (info.LinkTarget is null)
            {
                continue;
            }

            // Following only the leaf is insufficient for dotfile managers that symlink the
            // entire configuration directory. A dangling link cannot safely be initialized.
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !target.Exists)
            {
                throw new AgentConfigurationException(string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.UnsafeLink, resolved));
            }

            resolved = Resolve(target.FullName, depth + 1);
        }

        return resolved;
    }

    private static string ResolveCasing(string path)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return path;
        }

        // Native targets often do not exist yet. Canonicalize the longest existing prefix
        // so aliases of its directory still share an identity before the first file write.
        // PathNormalizer queries actual filesystem casing rather than lowercasing paths,
        // which would collapse distinct files on case-sensitive macOS volumes.
        var missing = new Stack<string>();
        var existing = path;
        while (!Path.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return path;
            }

            missing.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var canonical = PathNormalizer.ResolvePathCasing(existing);
        foreach (var segment in missing)
        {
            canonical = Path.Combine(canonical, segment);
        }

        return canonical;
    }
}
