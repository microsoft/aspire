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
    public static string UniversalSkillsDirectory { get; } = Path.Combine(".agents", "skills");
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string GetManagedSettingsDirectory(CliExecutionContext context, IEnvironment environment, string productName, string unixName)
        => environment.IsWindows()
            ? Path.Combine(GetOverride("ProgramFiles", context, environment) ??
                Path.Combine(Path.GetPathRoot(context.HomeDirectory.FullName)!, "Program Files"), productName)
            : environment.IsMacOS()
                ? Path.Combine(Path.DirectorySeparatorChar.ToString(), "Library", "Application Support", productName)
                : Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", unixName);

    public static IEnumerable<DirectoryInfo> ProjectDirectories(DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot.FullName, workingDirectory.FullName);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            // An explicit --workspace-root can be outside the working directory.
            workingDirectory = workspaceRoot;
        }

        for (var directory = workingDirectory; directory is not null; directory = directory.Parent)
        {
            yield return directory;
            if (Path.GetRelativePath(workspaceRoot.FullName, directory.FullName) == ".")
            {
                yield break;
            }
        }
    }

    public static string? GetOverride(string variable, CliExecutionContext executionContext, IEnvironment environment)
        => environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? Expand(value, executionContext)
            : null;

    public static string Expand(string path, CliExecutionContext executionContext)
    {
        var homeDirectory = executionContext.HomeDirectory.FullName;
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

        return Path.IsPathFullyQualified(path) ? path : Path.Combine(executionContext.WorkingDirectory.FullName, path);
    }

    public static string Resolve(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!PathNormalizer.TryResolveSymlinks(fullPath, out var resolved))
        {
            throw new AgentConfigurationException(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_UnsafeLink, path));
        }

        // The shared resolver permits ordinary missing suffixes. A dangling link is
        // different: initializing its missing destination would change user-owned intent.
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is not { Exists: true })
            {
                throw new AgentConfigurationException(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_UnsafeLink, current));
            }
        }

        return ResolveCasing(resolved);
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
        var existing = path;
        while (!Path.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return path;
            }

            existing = parent;
        }

        var canonical = PathNormalizer.ResolvePathCasing(existing);
        return Path.Join(canonical, path[existing.Length..]);
    }
}
