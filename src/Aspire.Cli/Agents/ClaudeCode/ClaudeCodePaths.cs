// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.ClaudeCode;

internal static class ClaudeCodePaths
{
    public const string ConfigDirectoryName = ".claude";

    public static string GetConfigDirectory(CliExecutionContext executionContext, IEnvironment environment)
        => GetConfigDirectoryOverride(executionContext, environment)
            ?? Path.Combine(executionContext.HomeDirectory.FullName, ConfigDirectoryName);

    public static string GetGlobalMcpConfigPath(CliExecutionContext executionContext, IEnvironment environment)
    {
        // CLAUDE_CONFIG_DIR relocates the state file into that directory, unlike ~/.claude.json.
        // https://code.claude.com/docs/en/env-vars
        var directory = GetConfigDirectoryOverride(executionContext, environment) ?? executionContext.HomeDirectory.FullName;

        return Path.Combine(directory, ".claude.json");
    }

    private static string? GetConfigDirectoryOverride(CliExecutionContext executionContext, IEnvironment environment)
        => environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } value
            ? Path.GetFullPath(value, executionContext.WorkingDirectory.FullName)
            : null;
}
