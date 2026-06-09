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
    private const string YarnConfigurationFileName = ".yarnrc.yml";
    private const string YarnNodeModulesConfiguration = "nodeLinker: node-modules";

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
            ConfigureToolchainFiles(projectRoot, toolchain);
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

        ConfigureToolchainFiles(projectRoot, toolchain);
    }

    /// <summary>
    /// Gets the restore/install command for a toolchain.
    /// </summary>
    internal static string GetInstallCommand(string toolchain) =>
        $"{GetCommandName(toolchain)} install";

    /// <summary>
    /// Gets the no-emit type-check command for a toolchain.
    /// </summary>
    internal static string GetTypeCheckCommand(string toolchain, string tsConfigFileName)
    {
        _ = tsConfigFileName;

        return NormalizeToolchain(toolchain) switch
        {
            "bun" => "bun run aspire:typecheck",
            "yarn" => "yarn run aspire:typecheck",
            "pnpm" => "pnpm run aspire:typecheck",
            "npm" => "npm run aspire:typecheck",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, "Unsupported TypeScript AppHost toolchain.")
        };
    }

    /// <summary>
    /// Gets the script runner command for a toolchain.
    /// </summary>
    internal static string GetRunScriptCommand(string toolchain, string scriptName) =>
        $"{GetCommandName(toolchain)} run {scriptName}";

    /// <summary>
    /// Gets the primary lock file name a toolchain should produce after restore/install.
    /// </summary>
    internal static string GetLockFileName(string toolchain) =>
        NormalizeToolchain(toolchain) switch
        {
            "bun" => "bun.lock",
            "yarn" => "yarn.lock",
            "pnpm" => "pnpm-lock.yaml",
            "npm" => "package-lock.json",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, "Unsupported TypeScript AppHost toolchain.")
        };

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

    internal static bool UsesCorepack(string toolchain) =>
        NormalizeToolchain(toolchain) is "pnpm" or "yarn";

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

    private static void ConfigureToolchainFiles(string projectRoot, string toolchain)
    {
        var yarnConfigPath = Path.Combine(projectRoot, YarnConfigurationFileName);
        if (NormalizeToolchain(toolchain) == "yarn")
        {
            // Yarn 4 defaults to Plug'n'Play, but the generated AppHost/Vite workflows exercised
            // here expect node_modules resolution across tsx, nodemon, and Vite.
            File.WriteAllText(yarnConfigPath, $"{YarnNodeModulesConfiguration}{Environment.NewLine}");

            var yarnLockPath = Path.Combine(projectRoot, "yarn.lock");
            if (!File.Exists(yarnLockPath))
            {
                // Without a lockfile, Yarn walks up to the AppHost package.json and treats a nested
                // Vite app as an unlisted workspace instead of an independent package.
                File.WriteAllText(yarnLockPath, string.Empty);
            }
        }
        else if (File.Exists(yarnConfigPath))
        {
            File.Delete(yarnConfigPath);
        }
    }

    private static string GetCommandName(string toolchain) =>
        NormalizeToolchain(toolchain) switch
        {
            "bun" => "bun",
            "yarn" => "yarn",
            "pnpm" => "pnpm",
            "npm" => "npm",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, "Unsupported TypeScript AppHost toolchain.")
        };

    private static string NormalizeToolchain(string toolchain)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolchain);
        return toolchain.ToLowerInvariant();
    }
}
