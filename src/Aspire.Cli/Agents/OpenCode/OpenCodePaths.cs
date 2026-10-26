// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.OpenCode;

internal static class OpenCodePaths
{
    public static string GetMcpConfigPath(DirectoryInfo directory)
    {
        var jsoncPath = Path.Combine(directory.FullName, "opencode.jsonc");
        var jsonPath = Path.Combine(directory.FullName, "opencode.json");
        // OpenCode loads JSON first, then JSONC, merging the latter with higher precedence.
        // https://github.com/anomalyco/opencode/blob/dev/packages/core/src/config.ts
        return File.Exists(jsoncPath) || !File.Exists(jsonPath) ? jsoncPath : jsonPath;
    }

    public static string GetGlobalMcpConfigPath(CliExecutionContext executionContext, IEnvironment environment)
    {
        if (GetOverride("OPENCODE_CONFIG") is { } configFile)
        {
            return configFile;
        }

        var configDirectory = GetOverride("OPENCODE_CONFIG_DIR")
            ?? Path.Combine(GetOverride("XDG_CONFIG_HOME")
                ?? Path.Combine(executionContext.HomeDirectory.FullName, ".config"), "opencode");

        return GetMcpConfigPath(new DirectoryInfo(configDirectory));

        string? GetOverride(string name)
            => environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? Path.GetFullPath(value, executionContext.WorkingDirectory.FullName)
                : null;
    }
}
