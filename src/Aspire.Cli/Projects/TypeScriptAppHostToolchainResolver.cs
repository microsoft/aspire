// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Utils;
using Aspire.Shared;
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
    private const string MinimumSupportedYarnVersion = "4.18.0";
    private const string NativeTypeScriptPackageName = "@typescript/native";
    private const string TypeScriptPackageName = "typescript";
    private const string TypeScript6AliasPrefix = "npm:@typescript/typescript6@";
    // Disposable incremental compiler state used to speed up subsequent type checks.
    private const string TypeCheckTsBuildInfoFileName = "./node_modules/.tmp/tsconfig.apphost.typecheck.tsbuildinfo";
    private static readonly string[] s_dependencySectionNames = ["dependencies", "devDependencies"];

    public static bool IsTypeScriptLanguage(LanguageInfo? language)
    {
        return language is not null &&
            (language.LanguageId.Value.Equals(KnownLanguageId.TypeScript, StringComparison.OrdinalIgnoreCase) ||
             language.LanguageId.Value.Equals(KnownLanguageId.TypeScriptAlias, StringComparison.OrdinalIgnoreCase));
    }

    public static TypeScriptAppHostToolchain Resolve(DirectoryInfo appHostDirectory, IEnvironment environment, ILogger? logger)
    {
        var resolution = ResolveWithReason(appHostDirectory, environment);
        logger?.LogDebug(
            "Selected TypeScript AppHost package manager '{PackageManager}' because {Reason}.",
            GetCommandName(resolution.Toolchain),
            resolution.Reason);

        return resolution.Toolchain;
    }

    internal static TypeScriptAppHostToolchainResolution ResolveWithReason(DirectoryInfo appHostDirectory, IEnvironment environment)
    {
        foreach (var candidateDirectory in EnumerateCandidateDirectories(appHostDirectory, environment))
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
                    throw CreateYarnVersionNotSupportedException($"the Yarn lockfile at {yarnLockFilePath}");
                }

                if (RequiresYarnTypeScriptAliasFixes(candidateDirectory))
                {
                    throw CreateYarnVersionMetadataRequiredException(yarnLockFilePath, candidateDirectory);
                }

                return CreateLockFileResolution(TypeScriptAppHostToolchain.Yarn, YarnLockFileName, candidateDirectory);
            }

            var yarnConfigFilePath = Path.Combine(candidateDirectory.FullName, YarnConfigFileName);
            if (File.Exists(yarnConfigFilePath))
            {
                if (RequiresYarnTypeScriptAliasFixes(candidateDirectory))
                {
                    throw CreateYarnVersionMetadataRequiredException(yarnConfigFilePath, candidateDirectory);
                }

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
            CreateTypeCheckCommand(toolchain, tsConfigFileName)
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
        var typeCheckCommand = CreateTypeCheckCommandString(toolchain, tsConfigFileName);
        var execCommand = toolchain == TypeScriptAppHostToolchain.Bun
            ? $"{typeCheckCommand} && bun run \"{{appHostFile}}\""
            : $"{typeCheckCommand} && {CreateTypeScriptRunCommandString(toolchain, tsConfigFileName)}";

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
                    "--exec", execCommand
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
                    "--exec", execCommand
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
                    "--exec", execCommand
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };
    }

    private static CommandSpec CreateTypeCheckCommand(TypeScriptAppHostToolchain toolchain, string tsConfigFileName)
    {
        return new CommandSpec
        {
            Command = toolchain == TypeScriptAppHostToolchain.Npm ? "npx" : GetCommandName(toolchain),
            Args = CreateTypeCheckCommandArgs(toolchain, tsConfigFileName)
        };
    }

    private static string[] CreateTypeCheckCommandArgs(TypeScriptAppHostToolchain toolchain, string tsConfigFileName)
    {
        var commandArgs = toolchain switch
        {
            TypeScriptAppHostToolchain.Npm => new List<string> { "--no-install", "tsc" },
            TypeScriptAppHostToolchain.Bun or TypeScriptAppHostToolchain.Yarn => new List<string> { "run", "tsc" },
            TypeScriptAppHostToolchain.Pnpm => new List<string> { "exec", "tsc" },
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };

        commandArgs.AddRange(
        [
            "--noEmit",
            "--incremental",
            "--tsBuildInfoFile", TypeCheckTsBuildInfoFileName,
            "-p", tsConfigFileName
        ]);

        return [.. commandArgs];
    }

    private static string CreateTypeCheckCommandString(TypeScriptAppHostToolchain toolchain, string tsConfigFileName)
    {
        var command = CreateTypeCheckCommand(toolchain, tsConfigFileName);
        return $"{command.Command} {string.Join(" ", command.Args)}";
    }

    private static string CreateTypeScriptRunCommandString(TypeScriptAppHostToolchain toolchain, string tsConfigFileName) =>
        toolchain switch
        {
            TypeScriptAppHostToolchain.Yarn => $"yarn run tsx --tsconfig {tsConfigFileName} \"{{appHostFile}}\"",
            TypeScriptAppHostToolchain.Pnpm => $"pnpm exec tsx --tsconfig {tsConfigFileName} \"{{appHostFile}}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };

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
                if (toolchain == TypeScriptAppHostToolchain.Yarn &&
                    (IsYarnClassicPackageManager(packageManager) ||
                     RequiresYarnTypeScriptAliasFixes(packageJson) && IsUnsupportedYarnPackageManager(packageManager)))
                {
                    throw CreateYarnVersionNotSupportedException($"'{packageManager}' in {packageJsonPath}");
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

    private static bool IsUnsupportedYarnPackageManager(string packageManager)
    {
        const string yarnPackageManagerPrefix = "yarn@";

        if (!packageManager.StartsWith(yarnPackageManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = packageManager[yarnPackageManagerPrefix.Length..];
        return !NpmVersionHelper.TryParseNpmVersion(version, out var yarnVersion)
            || !NpmVersionHelper.TryParseNpmVersion(MinimumSupportedYarnVersion, out var minimumVersion)
            || Semver.SemVersion.ComparePrecedence(yarnVersion, minimumVersion) < 0;
    }

    private static bool IsYarnClassicPackageManager(string packageManager)
    {
        const string yarnPackageManagerPrefix = "yarn@";

        return packageManager.StartsWith(yarnPackageManagerPrefix, StringComparison.OrdinalIgnoreCase)
            && NpmVersionHelper.TryParseNpmVersion(packageManager[yarnPackageManagerPrefix.Length..], out var yarnVersion)
            && yarnVersion.Major == 1;
    }

    private static bool RequiresYarnTypeScriptAliasFixes(DirectoryInfo directory)
    {
        var packageJsonPath = Path.Combine(directory.FullName, PackageJsonFileName);
        if (!File.Exists(packageJsonPath))
        {
            return false;
        }

        try
        {
            var packageJson = JsonNode.Parse(File.ReadAllText(packageJsonPath), documentOptions: ConfigurationHelper.ParseOptions) as JsonObject;
            return packageJson is not null && RequiresYarnTypeScriptAliasFixes(packageJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException
            or UnauthorizedAccessException or SecurityException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool RequiresYarnTypeScriptAliasFixes(JsonObject packageJson)
    {
        // Yarn 4.18 fixed both the optional TypeScript compatibility patch and direct-dependency binary
        // selection for the recommended side-by-side TypeScript 6/7 aliases. Older AppHosts that only
        // reference the JavaScript TypeScript package do not need those fixes and remain compatible.
        return HasDependency(packageJson, NativeTypeScriptPackageName)
            || GetDependencyVersion(packageJson, TypeScriptPackageName)?.StartsWith(TypeScript6AliasPrefix, StringComparison.OrdinalIgnoreCase) is true;
    }

    private static bool HasDependency(JsonObject packageJson, string packageName)
        => GetDependencyVersion(packageJson, packageName) is not null;

    private static string? GetDependencyVersion(JsonObject packageJson, string packageName)
    {
        foreach (var sectionName in s_dependencySectionNames)
        {
            if (packageJson[sectionName]?[packageName] is JsonValue value &&
                value.TryGetValue<string>(out var version))
            {
                return version;
            }
        }

        return null;
    }

    private static YarnVersionNotSupportedException CreateYarnVersionNotSupportedException(string upgradeTarget)
    {
        return new YarnVersionNotSupportedException(
            $"Yarn {MinimumSupportedYarnVersion} or later is required for TypeScript AppHosts. Upgrade {upgradeTarget}, or use npm, pnpm, or Bun.");
    }

    private static YarnVersionNotSupportedException CreateYarnVersionMetadataRequiredException(string markerPath, DirectoryInfo candidateDirectory)
    {
        var packageJsonPath = Path.Combine(candidateDirectory.FullName, PackageJsonFileName);
        return new YarnVersionNotSupportedException(
            $"Yarn {MinimumSupportedYarnVersion} or later is required for TypeScript AppHosts. " +
            $"Set \"packageManager\": \"yarn@{MinimumSupportedYarnVersion}\" in {packageJsonPath} so Aspire can verify the Yarn version selected for {markerPath}, or use npm, pnpm, or Bun.");
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

    private static IEnumerable<DirectoryInfo> EnumerateCandidateDirectories(DirectoryInfo appHostDirectory, IEnvironment environment)
    {
        yield return appHostDirectory;

        // Only use the immediate parent as a fallback so a project folder can provide
        // workspace-level hints without inheriting unrelated markers from higher directories.
        var parentDirectory = appHostDirectory.Parent;
        if (parentDirectory is not null && ShouldSearchParentDirectory(parentDirectory, environment))
        {
            yield return parentDirectory;
        }
    }

    internal static bool ShouldSearchParentDirectory(DirectoryInfo parentDirectory, IEnvironment environment, string? homeDirectory = null)
    {
        var isWindows = environment.IsWindows();
        var isMacOS = environment.IsMacOS();
        var pathComparison = isWindows || isMacOS
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
