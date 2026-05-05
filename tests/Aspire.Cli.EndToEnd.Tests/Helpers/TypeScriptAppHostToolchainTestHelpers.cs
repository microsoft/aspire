// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Helpers for configuring TypeScript AppHost toolchains in E2E workspaces.
/// </summary>
internal static class TypeScriptAppHostToolchainTestHelpers
{
    private static readonly JsonSerializerOptions s_packageJsonSerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Sets the package manager metadata for a TypeScript AppHost.
    /// </summary>
    /// <param name="projectRoot">The root directory containing <c>package.json</c>.</param>
    /// <param name="toolchain">The toolchain name.</param>
    /// <param name="cleanInstallState">
    /// <see langword="true"/> to remove prior package-manager lock files and <c>node_modules</c>
    /// so the selected toolchain can restore from a clean state.
    /// </param>
    internal static void SetPackageManager(string projectRoot, string toolchain, bool cleanInstallState = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(toolchain);

        var packageJsonPath = Path.Combine(projectRoot, "package.json");
        var packageJson = JsonNode.Parse(File.ReadAllText(packageJsonPath))?.AsObject()
            ?? throw new InvalidOperationException($"Failed to parse package.json at '{packageJsonPath}'.");

        packageJson["packageManager"] = GetPackageManager(toolchain);
        File.WriteAllText(packageJsonPath, $"{packageJson.ToJsonString(s_packageJsonSerializerOptions)}{Environment.NewLine}");

        if (!cleanInstallState)
        {
            return;
        }

        foreach (var lockFileName in new[] { "package-lock.json", "bun.lock", "bun.lockb", "pnpm-lock.yaml", "yarn.lock" })
        {
            var lockFilePath = Path.Combine(projectRoot, lockFileName);
            if (File.Exists(lockFilePath))
            {
                File.Delete(lockFilePath);
            }
        }

        var nodeModulesPath = Path.Combine(projectRoot, "node_modules");
        if (Directory.Exists(nodeModulesPath))
        {
            Directory.Delete(nodeModulesPath, recursive: true);
        }
    }

    /// <summary>
    /// Gets the package manager metadata value for a toolchain.
    /// </summary>
    /// <param name="toolchain">The toolchain name.</param>
    /// <returns>The <c>packageManager</c> value.</returns>
    internal static string GetPackageManager(string toolchain) =>
        NormalizeToolchain(toolchain) switch
        {
            "bun" => "bun@1.2.0",
            "yarn" => "yarn@4.14.1",
            "pnpm" => "pnpm@10.0.0",
            "npm" => "npm@10.0.0",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, "Unsupported TypeScript AppHost toolchain.")
        };

    /// <summary>
    /// Gets the display name for a toolchain.
    /// </summary>
    /// <param name="toolchain">The toolchain name.</param>
    /// <returns>The user-facing display name.</returns>
    internal static string GetDisplayName(string toolchain) =>
        NormalizeToolchain(toolchain) switch
        {
            "bun" => "Bun",
            "yarn" => "Yarn",
            "pnpm" => "pnpm",
            "npm" => "Node.js",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, "Unsupported TypeScript AppHost toolchain.")
        };

    /// <summary>
    /// Gets the installation link for a toolchain.
    /// </summary>
    /// <param name="toolchain">The toolchain name.</param>
    /// <returns>The installation documentation URL.</returns>
    internal static string GetInstallationLink(string toolchain) =>
        NormalizeToolchain(toolchain) switch
        {
            "bun" => "https://bun.sh/docs/installation",
            "yarn" => "https://yarnpkg.com/getting-started/install",
            "pnpm" => "https://pnpm.io/installation",
            "npm" => "https://nodejs.org/en/download",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, "Unsupported TypeScript AppHost toolchain.")
        };

    private static string NormalizeToolchain(string toolchain)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolchain);
        return toolchain.ToLowerInvariant();
    }
}
