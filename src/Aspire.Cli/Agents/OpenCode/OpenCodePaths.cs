// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.OpenCode;

internal static class OpenCodePaths
{
    internal const string ConfigEnvironmentVariable = "OPENCODE_CONFIG";
    internal const string ConfigDirectoryEnvironmentVariable = "OPENCODE_CONFIG_DIR";
    internal const string ConfigContentEnvironmentVariable = "OPENCODE_CONFIG_CONTENT";

    public static string GetMcpConfigPath(DirectoryInfo directory)
        => SelectConfigPath(GetConfigPaths(directory.FullName), directory);

    public static string GetProjectConfigPath(DirectoryInfo directory)
        => SelectConfigPath(GetProjectConfigPaths(directory), directory);

    public static IEnumerable<string> GetProjectConfigPaths(DirectoryInfo directory)
        => GetConfigPaths(directory.FullName).Concat(GetConfigPaths(Path.Combine(directory.FullName, ".opencode")));

    public static IEnumerable<string> GetConfigPaths(string directory)
    {
        // OpenCode loads JSON first, then JSONC, merging the latter with higher precedence.
        // https://github.com/anomalyco/opencode/blob/dev/packages/core/src/config.ts
        yield return Path.Combine(directory, "opencode.json");
        yield return Path.Combine(directory, "opencode.jsonc");
    }

    public static string GetConfigDirectory(CliExecutionContext context, IEnvironment environment)
        => Path.Combine(GetOverride("XDG_CONFIG_HOME", context, environment) ??
            Path.Combine(context.HomeDirectory.FullName, ".config"), "opencode");

    // https://opencode.ai/docs/config/#managed-settings
    public static string GetManagedSettingsDirectory(CliExecutionContext context, IEnvironment environment)
        => environment.IsWindows()
            ? Path.Combine(AgentPath.GetOverride("ProgramData", context, environment) ??
                Path.Combine(Path.GetPathRoot(context.HomeDirectory.FullName)!, "ProgramData"), "opencode")
            : environment.IsMacOS()
                ? Path.Combine(Path.DirectorySeparatorChar.ToString(), "Library", "Application Support", "opencode")
                : Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", "opencode");

    public static string GetGlobalMcpConfigPath(CliExecutionContext executionContext, IEnvironment environment)
    {
        if (GetOverride(ConfigEnvironmentVariable, executionContext, environment) is { } configFile)
        {
            return configFile;
        }

        var configDirectory = GetOverride(ConfigDirectoryEnvironmentVariable, executionContext, environment)
            ?? GetConfigDirectory(executionContext, environment);

        return GetMcpConfigPath(new DirectoryInfo(configDirectory));
    }

    // Match MCP destinations: relative overrides, including a literal "~", use the working directory.
    public static string? GetOverride(string name, CliExecutionContext executionContext, IEnvironment environment)
        => environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? Path.GetFullPath(value, executionContext.WorkingDirectory.FullName)
            : null;

    private static string SelectConfigPath(IEnumerable<string> candidates, DirectoryInfo directory)
        => candidates.LastOrDefault(File.Exists) ?? Path.Combine(directory.FullName, "opencode.jsonc");
}
