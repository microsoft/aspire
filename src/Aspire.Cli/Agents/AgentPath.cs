// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Shares agent configuration paths, home expansion and project traversal.
/// </summary>
internal static class AgentPath
{
    public static string UniversalSkillsDirectory { get; } = Path.Combine(".agents", "skills");

    public static string GetManagedSettingsDirectory(CliExecutionContext context, IEnvironment environment, string productName, string unixName)
    {
        if (environment.IsWindows())
        {
            return Path.Combine(GetOverride("ProgramFiles", context, environment) ??
                Path.Combine(Path.GetPathRoot(context.HomeDirectory.FullName)!, "Program Files"), productName);
        }

        if (environment.IsMacOS())
        {
            return Path.Combine(Path.DirectorySeparatorChar.ToString(), "Library", "Application Support", productName);
        }

        return Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", unixName);
    }

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
}
