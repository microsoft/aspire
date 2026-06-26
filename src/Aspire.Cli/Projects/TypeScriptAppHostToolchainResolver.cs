// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Utils;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal enum TypeScriptAppHostToolchain
{
    Npm,
    Bun,
    Yarn,
    Pnpm
}

internal static class TypeScriptAppHostToolchainResolver
{
    private const string PackageJsonFileName = "package.json";
    private const string BunLockFileName = "bun.lock";
    private const string BunBinaryLockFileName = "bun.lockb";
    private const string YarnLockFileName = "yarn.lock";
    private const string YarnClassicLockFileVersionLine = "# yarn lockfile v1";
    private const string YarnConfigFileName = ".yarnrc.yml";
    private const string PackageLockFileName = "package-lock.json";
    private const string PnpmLockFileName = "pnpm-lock.yaml";

    public static bool IsTypeScriptLanguage(LanguageInfo? language)
    {
        return language is not null &&
            (language.LanguageId.Value.Equals(KnownLanguageId.TypeScript, StringComparison.OrdinalIgnoreCase) ||
             language.LanguageId.Value.Equals(KnownLanguageId.TypeScriptAlias, StringComparison.OrdinalIgnoreCase));
    }

    public static TypeScriptAppHostToolchain Resolve(DirectoryInfo appHostDirectory, ILogger? logger)
    {
        var resolution = ResolveWithReason(appHostDirectory);
        logger?.LogDebug(
            "Selected TypeScript AppHost package manager '{PackageManager}' because {Reason}.",
            GetCommandName(resolution.Toolchain),
            resolution.Reason);

        return resolution.Toolchain;
    }

    internal static TypeScriptAppHostToolchainResolution ResolveWithReason(DirectoryInfo appHostDirectory)
    {
        return ResolveWithReason(appHostDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    // The home directory is a parameter so tests can simulate the upward walk halting at the
    // user's home directory without creating files under the real home directory.
    internal static TypeScriptAppHostToolchainResolution ResolveWithReason(DirectoryInfo appHostDirectory, string? homeDirectory)
    {
        foreach (var candidateDirectory in EnumerateCandidateDirectories(appHostDirectory, homeDirectory))
        {
            if (TryGetToolchainFromPackageJson(candidateDirectory, out var configuredToolchain, out var reason))
            {
                return new(configuredToolchain, reason);
            }

            if (File.Exists(Path.Combine(candidateDirectory.FullName, BunLockFileName)))
            {
                return CreateLockFileResolution(TypeScriptAppHostToolchain.Bun, BunLockFileName, candidateDirectory);
            }

            if (File.Exists(Path.Combine(candidateDirectory.FullName, BunBinaryLockFileName)))
            {
                return CreateLockFileResolution(TypeScriptAppHostToolchain.Bun, BunBinaryLockFileName, candidateDirectory);
            }

            if (File.Exists(Path.Combine(candidateDirectory.FullName, PnpmLockFileName)))
            {
                return CreateLockFileResolution(TypeScriptAppHostToolchain.Pnpm, PnpmLockFileName, candidateDirectory);
            }

            var yarnLockFilePath = Path.Combine(candidateDirectory.FullName, YarnLockFileName);
            if (File.Exists(yarnLockFilePath))
            {
                if (IsYarnClassicLockFile(yarnLockFilePath))
                {
                    throw CreateYarnClassicNotSupportedException($"the Yarn lockfile at {yarnLockFilePath}");
                }

                return CreateLockFileResolution(TypeScriptAppHostToolchain.Yarn, YarnLockFileName, candidateDirectory);
            }

            if (File.Exists(Path.Combine(candidateDirectory.FullName, YarnConfigFileName)))
            {
                return CreateLockFileResolution(TypeScriptAppHostToolchain.Yarn, YarnConfigFileName, candidateDirectory);
            }

            if (File.Exists(Path.Combine(candidateDirectory.FullName, PackageLockFileName)))
            {
                return CreateLockFileResolution(TypeScriptAppHostToolchain.Npm, PackageLockFileName, candidateDirectory);
            }
        }

        return new(TypeScriptAppHostToolchain.Npm, $"no package manager marker found in {appHostDirectory.FullName} or an eligible parent directory");
    }

    public static string[] GetRequiredCommands(TypeScriptAppHostToolchain toolchain)
    {
        return toolchain switch
        {
            TypeScriptAppHostToolchain.Npm => ["npm", "npx"],
            _ => [GetCommandName(toolchain)]
        };
    }

    public static string GetCommandName(TypeScriptAppHostToolchain toolchain)
    {
        return toolchain switch
        {
            TypeScriptAppHostToolchain.Npm => "npm",
            TypeScriptAppHostToolchain.Bun => "bun",
            TypeScriptAppHostToolchain.Yarn => "yarn",
            TypeScriptAppHostToolchain.Pnpm => "pnpm",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };
    }

    public static string GetInstallCommand(TypeScriptAppHostToolchain toolchain)
    {
        return $"{GetCommandName(toolchain)} install";
    }

    public static string GetDisplayName(TypeScriptAppHostToolchain toolchain)
    {
        return toolchain switch
        {
            TypeScriptAppHostToolchain.Npm => "Node.js",
            TypeScriptAppHostToolchain.Bun => "Bun",
            TypeScriptAppHostToolchain.Yarn => "Yarn",
            TypeScriptAppHostToolchain.Pnpm => "pnpm",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };
    }

    // Builds the interactive language-prompt label for a TypeScript AppHost that would be created under
    // <paramref name="appHostDirectory"/>, naming the package manager that will actually run it (e.g.
    // "TypeScript (Bun)") instead of the static "TypeScript (Node.js)" catalog label. The prompt is shown
    // before any project is scaffolded, so resolution failures - most notably an unsupported classic Yarn
    // workspace detected mid-walk - fall back to the default "TypeScript (Node.js)" label rather than
    // aborting the prompt; the unsupported-toolchain error is surfaced later, during scaffolding.
    public static string GetTypeScriptDisplayName(DirectoryInfo appHostDirectory)
    {
        TypeScriptAppHostToolchain toolchain;
        try
        {
            toolchain = ResolveWithReason(appHostDirectory).Toolchain;
        }
        catch (YarnClassicNotSupportedException)
        {
            toolchain = TypeScriptAppHostToolchain.Npm;
        }

        return $"TypeScript ({GetDisplayName(toolchain)})";
    }

    public static RuntimeSpec ApplyToRuntimeSpec(RuntimeSpec baseRuntimeSpec, TypeScriptAppHostToolchain toolchain)
    {
        if (toolchain == TypeScriptAppHostToolchain.Npm)
        {
            return baseRuntimeSpec;
        }

        var tsConfigFileName = GetTsConfigFileName(baseRuntimeSpec);

        return new RuntimeSpec
        {
            Language = baseRuntimeSpec.Language,
            DisplayName = $"TypeScript ({GetDisplayName(toolchain)})",
            CodeGenLanguage = baseRuntimeSpec.CodeGenLanguage,
            DetectionPatterns = baseRuntimeSpec.DetectionPatterns,
            Initialize = baseRuntimeSpec.Initialize,
            InstallDependencies = CreateInstallCommand(toolchain),
            PreExecute = CreatePreExecuteCommands(toolchain, tsConfigFileName),
            Execute = CreateExecuteCommand(toolchain, tsConfigFileName),
            WatchExecute = CreateWatchCommand(toolchain, tsConfigFileName),
            PublishExecute = baseRuntimeSpec.PublishExecute,
            ExtensionLaunchCapability = baseRuntimeSpec.ExtensionLaunchCapability,
            MigrationFiles = baseRuntimeSpec.MigrationFiles
        };
    }

    private static CommandSpec CreateInstallCommand(TypeScriptAppHostToolchain toolchain)
    {
        // pnpm resolves a parent pnpm-workspace.yaml when install runs in a nested package.
        // The generated brownfield AppHost intentionally lives outside the user's workspace
        // package graph, so install only that package instead of requiring edits to the
        // user's workspace file. See https://pnpm.io/workspaces.
        string[] args = toolchain == TypeScriptAppHostToolchain.Pnpm
            ? ["install", "--ignore-workspace"]
            : ["install"];

        return new CommandSpec
        {
            Command = GetCommandName(toolchain),
            Args = args
        };
    }

    private static CommandSpec[] CreatePreExecuteCommands(TypeScriptAppHostToolchain toolchain, string tsConfigFileName)
    {
        return
        [
            toolchain switch
            {
                TypeScriptAppHostToolchain.Bun => new CommandSpec
                {
                    Command = "bun",
                    Args = ["run", "tsc", "--noEmit", "-p", tsConfigFileName]
                },
                TypeScriptAppHostToolchain.Yarn => new CommandSpec
                {
                    Command = "yarn",
                    Args = ["run", "tsc", "--noEmit", "-p", tsConfigFileName]
                },
                TypeScriptAppHostToolchain.Pnpm => new CommandSpec
                {
                    Command = "pnpm",
                    Args = ["exec", "tsc", "--noEmit", "-p", tsConfigFileName]
                },
                _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
            }
        ];
    }

    private static CommandSpec CreateExecuteCommand(TypeScriptAppHostToolchain toolchain, string tsConfigFileName)
    {
        return toolchain switch
        {
            TypeScriptAppHostToolchain.Bun => new CommandSpec
            {
                Command = "bun",
                Args = ["run", "{appHostFile}"]
            },
            TypeScriptAppHostToolchain.Yarn => new CommandSpec
            {
                Command = "yarn",
                Args = ["run", "tsx", "--tsconfig", tsConfigFileName, "{appHostFile}"]
            },
            TypeScriptAppHostToolchain.Pnpm => new CommandSpec
            {
                Command = "pnpm",
                Args = ["exec", "tsx", "--tsconfig", tsConfigFileName, "{appHostFile}"]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };
    }

    private static CommandSpec CreateWatchCommand(TypeScriptAppHostToolchain toolchain, string tsConfigFileName)
    {
        return toolchain switch
        {
            TypeScriptAppHostToolchain.Bun => new CommandSpec
            {
                Command = "bun",
                Args =
                [
                    "run",
                    "nodemon",
                    "--signal", "SIGTERM",
                    "--watch", ".",
                    "--ext", "ts,mts",
                    "--ignore", "node_modules/",
                    "--ignore", ".aspire/modules/",
                    "--exec", $"bun run tsc --noEmit -p {tsConfigFileName} && bun run \"{{appHostFile}}\""
                ]
            },
            TypeScriptAppHostToolchain.Yarn => new CommandSpec
            {
                Command = "yarn",
                Args =
                [
                    "exec",
                    "nodemon",
                    "--signal", "SIGTERM",
                    "--watch", ".",
                    "--ext", "ts,mts",
                    "--ignore", "node_modules/",
                    "--ignore", ".aspire/modules/",
                    "--exec", $"yarn run tsc --noEmit -p {tsConfigFileName} && yarn run tsx --tsconfig {tsConfigFileName} \"{{appHostFile}}\""
                ]
            },
            TypeScriptAppHostToolchain.Pnpm => new CommandSpec
            {
                Command = "pnpm",
                Args =
                [
                    "exec",
                    "nodemon",
                    "--signal", "SIGTERM",
                    "--watch", ".",
                    "--ext", "ts,mts",
                    "--ignore", "node_modules/",
                    "--ignore", ".aspire/modules/",
                    "--exec", $"pnpm exec tsc --noEmit -p {tsConfigFileName} && pnpm exec tsx --tsconfig {tsConfigFileName} \"{{appHostFile}}\""
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };
    }

    private static string GetTsConfigFileName(RuntimeSpec runtimeSpec)
    {
        var args = runtimeSpec.Execute.Args;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--tsconfig", StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return "tsconfig.apphost.json";
    }

    private static bool TryGetToolchainFromPackageJson(DirectoryInfo appHostDirectory, out TypeScriptAppHostToolchain toolchain, out string reason)
    {
        toolchain = default;
        reason = string.Empty;

        var packageJsonPath = Path.Combine(appHostDirectory.FullName, PackageJsonFileName);
        if (!File.Exists(packageJsonPath))
        {
            return false;
        }

        try
        {
            var packageJson = JsonNode.Parse(File.ReadAllText(packageJsonPath), documentOptions: ConfigurationHelper.ParseOptions) as JsonObject;
            if (packageJson?["packageManager"] is not JsonValue packageManagerValue ||
                !packageManagerValue.TryGetValue<string>(out var packageManager) ||
                string.IsNullOrWhiteSpace(packageManager))
            {
                return false;
            }

            var packageManagerName = packageManager.Split('@', 2)[0];
            if (TryParseToolchain(packageManagerName, out toolchain))
            {
                if (toolchain == TypeScriptAppHostToolchain.Yarn && IsYarnClassicPackageManager(packageManager))
                {
                    throw CreateYarnClassicNotSupportedException($"'{packageManager}' in {packageJsonPath}");
                }

                reason = $"packageManager '{packageManager}' found in {packageJsonPath}";
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is JsonException or IOException
            or UnauthorizedAccessException or SecurityException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryParseToolchain(string packageManagerName, out TypeScriptAppHostToolchain toolchain)
    {
        TypeScriptAppHostToolchain? result = packageManagerName.ToLowerInvariant() switch
        {
            "npm" => TypeScriptAppHostToolchain.Npm,
            "bun" => TypeScriptAppHostToolchain.Bun,
            "yarn" => TypeScriptAppHostToolchain.Yarn,
            "pnpm" => TypeScriptAppHostToolchain.Pnpm,
            _ => null
        };

        toolchain = result ?? default;
        return result.HasValue;
    }

    private static bool IsYarnClassicPackageManager(string packageManager)
    {
        const string yarnPackageManagerPrefix = "yarn@";

        if (!packageManager.StartsWith(yarnPackageManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = packageManager[yarnPackageManagerPrefix.Length..];
        return version.Length > 0 &&
            version[0] == '1' &&
            (version.Length == 1 || !char.IsAsciiDigit(version[1]));
    }

    private static YarnClassicNotSupportedException CreateYarnClassicNotSupportedException(string upgradeTarget)
    {
        return new YarnClassicNotSupportedException(
            $"Yarn Classic is not supported for TypeScript AppHosts. Upgrade {upgradeTarget} to Yarn 4 or later, or use npm, pnpm, or Bun.");
    }

    private static bool IsYarnClassicLockFile(string yarnLockFilePath)
    {
        try
        {
            var linesRead = 0;
            foreach (var line in File.ReadLines(yarnLockFilePath))
            {
                if (line.Trim().Equals(YarnClassicLockFileVersionLine, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                linesRead++;
                if (linesRead >= 5)
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SecurityException or NotSupportedException)
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<DirectoryInfo> EnumerateCandidateDirectories(DirectoryInfo appHostDirectory, string? homeDirectory)
    {
        yield return appHostDirectory;

        // Walk up the directory tree so a workspace-level marker (e.g. a Bun/pnpm/Yarn
        // monorepo lockfile or a packageManager field in the workspace root package.json)
        // is detected even when the AppHost is nested several directories below it. This is
        // common because brownfield scaffolding nests the AppHost in its own subdirectory,
        // and matches how the JS package managers themselves locate the workspace root by
        // walking up the tree. Stop before the filesystem root and the user's home directory
        // because those can hold unrelated user-level markers that should not govern a project.
        var parentDirectory = appHostDirectory.Parent;
        while (parentDirectory is not null && ShouldSearchParentDirectory(parentDirectory, homeDirectory))
        {
            yield return parentDirectory;
            parentDirectory = parentDirectory.Parent;
        }
    }

    internal static bool ShouldSearchParentDirectory(DirectoryInfo parentDirectory, string? homeDirectory = null)
    {
        var pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Root and home directories are not project folders. They can contain unrelated user-level
        // files, so package manager markers there should not influence TypeScript AppHost projects.
        var parentPath = Path.TrimEndingDirectorySeparator(parentDirectory.FullName);
        if (string.Equals(parentPath, Path.TrimEndingDirectorySeparator(parentDirectory.Root.FullName), pathComparison))
        {
            return false;
        }

        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(homeDirectory) ||
            !string.Equals(parentPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeDirectory)), pathComparison);
    }

    private static TypeScriptAppHostToolchainResolution CreateLockFileResolution(TypeScriptAppHostToolchain toolchain, string markerName, DirectoryInfo directory)
    {
        return new(toolchain, $"{markerName} found in {directory.FullName}");
    }
}

internal readonly record struct TypeScriptAppHostToolchainResolution(TypeScriptAppHostToolchain Toolchain, string Reason);
