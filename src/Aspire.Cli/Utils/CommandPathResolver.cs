// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Resolves commands from PATH and produces actionable error messages when they are missing.
/// </summary>
internal static class CommandPathResolver
{
    private static readonly Dictionary<string, CommandMetadata> s_commandMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        ["npm"] = new("Node.js", "https://nodejs.org/en/download"),
        ["npx"] = new("Node.js", "https://nodejs.org/en/download"),
        ["bun"] = new("Bun", "https://bun.sh/docs/installation"),
        ["yarn"] = new("Yarn", "https://yarnpkg.com/getting-started/install"),
        ["pnpm"] = new("pnpm", "https://pnpm.io/installation")
    };

    /// <summary>
    /// Resolves a command from the system PATH.
    /// </summary>
    /// <param name="command">The command to resolve.</param>
    /// <param name="resolvedCommand">The resolved command path when found.</param>
    /// <param name="errorMessage">The user-facing error message when the command is missing.</param>
    /// <returns><see langword="true"/> when the command is found; otherwise, <see langword="false"/>.</returns>
    public static bool TryResolveCommand(string command, out string? resolvedCommand, out string? errorMessage)
    {
        return TryResolveCommand(command, PathLookupHelper.FindFullPathFromPath, out resolvedCommand, out errorMessage);
    }

    /// <summary>
    /// Resolves a command from a custom lookup source.
    /// </summary>
    /// <param name="command">The command to resolve.</param>
    /// <param name="commandResolver">The resolver used to find the command.</param>
    /// <param name="resolvedCommand">The resolved command path when found.</param>
    /// <param name="errorMessage">The user-facing error message when the command is missing.</param>
    /// <returns><see langword="true"/> when the command is found; otherwise, <see langword="false"/>.</returns>
    internal static bool TryResolveCommand(
        string command,
        Func<string, string?> commandResolver,
        out string? resolvedCommand,
        out string? errorMessage)
    {
        resolvedCommand = commandResolver(command);
        if (resolvedCommand is not null)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = GetMissingCommandMessage(command);
        return false;
    }

    /// <summary>
    /// Gets a user-facing error message for a missing command.
    /// </summary>
    /// <param name="command">The missing command.</param>
    /// <returns>An actionable error message.</returns>
    internal static string GetMissingCommandMessage(string command)
    {
        var normalizedCommand = NormalizeCommand(command);

        if (s_commandMetadata.TryGetValue(normalizedCommand, out var metadata))
        {
            return $"{normalizedCommand} is not installed or not found in PATH. Please install {metadata.InstallDisplayName} and try again.";
        }

        return $"Command '{command}' not found. Please ensure it is installed and in your PATH.";
    }

    internal static string? GetInstallationLink(string command)
    {
        return s_commandMetadata.TryGetValue(NormalizeCommand(command), out var metadata)
            ? metadata.InstallationLink
            : null;
    }

    private static string NormalizeCommand(string command)
    {
        return Path.GetFileNameWithoutExtension(command);
    }

    private sealed record CommandMetadata(string InstallDisplayName, string InstallationLink);
}
