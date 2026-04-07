// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Resolves commands from PATH and produces actionable error messages when they are missing.
/// </summary>
internal static class CommandPathResolver
{
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
        var normalizedCommand = Path.GetFileNameWithoutExtension(command);

        return normalizedCommand.ToLowerInvariant() switch
        {
            "npm" or "npx" => $"{normalizedCommand} is not installed or not found in PATH. Please install Node.js and try again.",
            _ => $"Command '{command}' not found. Please ensure it is installed and in your PATH."
        };
    }
}
