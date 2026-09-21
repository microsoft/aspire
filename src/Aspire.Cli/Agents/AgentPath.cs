// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Agents;

/// <summary>
/// Shares home expansion and physical file identity across agent configuration and managed payloads.
/// </summary>
internal static class AgentPath
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string? GetOverride(string variable, CliExecutionContext executionContext, IEnvironment environment)
        => environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? Expand(value, executionContext)
            : null;

    public static string Expand(string path, CliExecutionContext executionContext)
        => Expand(path, executionContext.HomeDirectory.FullName, executionContext.WorkingDirectory.FullName);

    public static string Expand(string path, string homeDirectory, string workingDirectory)
    {
        // Overrides accept "~", "~/config", and "~\config" against the injected home.
        // "~other-user" is not expanded. Defer path validation to each target's error boundary.
        if (path == "~")
        {
            return homeDirectory;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var relative = path[2..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.Join(homeDirectory, relative);
        }

        return Path.IsPathFullyQualified(path) ? path : Path.Combine(workingDirectory, path);
    }

    public static string GetManagedDirectory(CliExecutionContext executionContext, IEnvironment environment, string productName, string unixName)
    {
        if (environment.IsWindows())
        {
            return Path.Combine(GetOverride("ProgramFiles", executionContext, environment) ??
                Path.Combine(Path.GetPathRoot(executionContext.HomeDirectory.FullName)!, "Program Files"), productName);
        }

        return environment.IsMacOS()
            ? Path.Combine(Path.DirectorySeparatorChar.ToString(), "Library", "Application Support", productName)
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", unixName);
    }

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
