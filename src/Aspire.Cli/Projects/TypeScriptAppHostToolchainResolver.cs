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
        foreach (var candidateDirectory in EnumerateCandidateDirectories(appHostDirectory))
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

            if (File.Exists(Path.Combine(candidateDirectory.FullName, YarnLockFileName)))
            {
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
        return new CommandSpec
        {
            Command = GetCommandName(toolchain),
            Args = ["install"]
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
                    "--ext", "ts",
                    "--ignore", "node_modules/",
                    "--ignore", ".modules/",
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
                    "--ext", "ts",
                    "--ignore", "node_modules/",
                    "--ignore", ".modules/",
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
                    "--ext", "ts",
                    "--ignore", "node_modules/",
                    "--ignore", ".modules/",
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

    private static IEnumerable<DirectoryInfo> EnumerateCandidateDirectories(DirectoryInfo appHostDirectory)
    {
        yield return appHostDirectory;

        // Only use the immediate parent as a fallback so a project folder can provide
        // workspace-level hints without inheriting unrelated markers from higher directories.
        var parentDirectory = appHostDirectory.Parent;
        if (parentDirectory is not null && ShouldSearchParentDirectory(parentDirectory))
        {
            yield return parentDirectory;
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
