// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Projects;

public sealed class TypeScriptAppHostToolchainResolverTests(ITestOutputHelper outputHelper)
{
    private const string BuildOutputDirectory = "./node_modules/.tmp/aspire-apphost";
    private const string BuildTsBuildInfoFileName = "./node_modules/.tmp/aspire-apphost.tsbuildinfo";

    // Matches Aspire.Shared.TypeScriptAppHostBuildCleanup.AppendShellCleanupOnFailure: `--noEmitOnError`
    // only blocks output from a failing compile, it doesn't remove output an earlier, successful
    // compile left behind, so a shell `||` fallback deletes the stale directory when tsc fails.
    private const string CleanupOnFailureSuffix =
        " || node -e \"process.exitCode=1;require('fs').rmSync('" + BuildOutputDirectory + "',{recursive:true,force:true})\"";

    [Fact]
    public void Resolve_WhenPackageManagerIsBun_ReturnsBun()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"packageManager\": \"bun@1.2.0\" }");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Bun, toolchain);
    }

    [Fact]
    public void Resolve_WhenPnpmLockExists_ReturnsPnpm()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "pnpm-lock.yaml"), "lockfileVersion: '9.0'");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Pnpm, toolchain);
    }

    [Fact]
    public void Resolve_WhenPackageManagerIsYarnClassic_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageJsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, "package.json");
        File.WriteAllText(packageJsonPath, "{ \"packageManager\": \"yarn@1.22.22\" }");

        var exception = Assert.Throws<YarnVersionNotSupportedException>(() => TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null));

        Assert.Equal($"Yarn 4.18.0 or later is required for TypeScript AppHosts. Upgrade 'yarn@1.22.22' in {packageJsonPath}, or use npm, pnpm, or Bun.", exception.Message);
    }

    [Fact]
    public void Resolve_WhenPackageManagerIsModernYarn_ReturnsYarn()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"),
            """{ "packageManager": "yarn@4.18.0", "devDependencies": { "@typescript/native": "npm:typescript@^7.0.2" } }""");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Yarn, toolchain);
    }

    [Fact]
    public void Resolve_WhenLegacyAppHostUsesOlderModernYarn_ReturnsYarn()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"),
            """{ "packageManager": "yarn@4.14.1", "devDependencies": { "typescript": "^5.9.3" } }""");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Yarn, toolchain);
    }

    [Fact]
    public void Resolve_WhenPackageManagerPredatesTypeScriptAliasFixes_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageJsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, "package.json");
        File.WriteAllText(
            packageJsonPath,
            """{ "packageManager": "yarn@4.17.1", "devDependencies": { "@typescript/native": "npm:typescript@^7.0.2" } }""");

        var exception = Assert.Throws<YarnVersionNotSupportedException>(() => TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null));

        Assert.Equal($"Yarn 4.18.0 or later is required for TypeScript AppHosts. Upgrade 'yarn@4.17.1' in {packageJsonPath}, or use npm, pnpm, or Bun.", exception.Message);
    }

    [Fact]
    public void Resolve_WhenParentPackageManagerPredatesTypeScriptAliasFixes_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(
            Path.Combine(appHostDirectory.FullName, "package.json"),
            """{ "devDependencies": { "@typescript/native": "npm:typescript@^7.0.2" } }""");
        var parentPackageJsonPath = Path.Combine(appHostDirectory.Parent!.FullName, "package.json");
        File.WriteAllText(parentPackageJsonPath, """{ "packageManager": "yarn@4.17.1" }""");

        var exception = Assert.Throws<YarnVersionNotSupportedException>(() => TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory, new TestEnvironment(), logger: null));

        Assert.Equal($"Yarn 4.18.0 or later is required for TypeScript AppHosts. Upgrade 'yarn@4.17.1' in {parentPackageJsonPath}, or use npm, pnpm, or Bun.", exception.Message);
    }

    [Fact]
    public void Resolve_WhenLegacyAppHostUsesOlderModernYarnFromParent_ReturnsYarn()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(
            Path.Combine(appHostDirectory.FullName, "package.json"),
            """{ "devDependencies": { "typescript": "^5.9.3" } }""");
        File.WriteAllText(Path.Combine(appHostDirectory.Parent!.FullName, "package.json"), """{ "packageManager": "yarn@4.14.1" }""");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Yarn, toolchain);
    }

    [Fact]
    public void Resolve_WhenYarnLockIsClassic_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        var yarnLockPath = Path.Combine(workspace.WorkspaceRoot.FullName, "yarn.lock");
        File.WriteAllText(yarnLockPath, "# THIS IS AN AUTOGENERATED FILE. DO NOT EDIT THIS FILE DIRECTLY.\n# yarn lockfile v1\n");

        var exception = Assert.Throws<YarnVersionNotSupportedException>(() => TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null));

        Assert.Equal($"Yarn 4.18.0 or later is required for TypeScript AppHosts. Upgrade the Yarn lockfile at {yarnLockPath}, or use npm, pnpm, or Bun.", exception.Message);
    }

    [Fact]
    public void Resolve_WhenParentYarnLockIsClassic_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        var parentDirectory = appHostDirectory.Parent!;
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(parentDirectory.FullName, "package.json"), "{ \"name\": \"workspace\" }");
        var yarnLockPath = Path.Combine(parentDirectory.FullName, "yarn.lock");
        File.WriteAllText(yarnLockPath, "# THIS IS AN AUTOGENERATED FILE. DO NOT EDIT THIS FILE DIRECTLY.\n# yarn lockfile v1\n");

        var exception = Assert.Throws<YarnVersionNotSupportedException>(() => TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory, new TestEnvironment(), logger: null));

        Assert.Equal($"Yarn 4.18.0 or later is required for TypeScript AppHosts. Upgrade the Yarn lockfile at {yarnLockPath}, or use npm, pnpm, or Bun.", exception.Message);
    }

    [Fact]
    public void Resolve_WhenPackageLockExists_ReturnsNpm()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        var parentDirectory = appHostDirectory.Parent!;
        File.WriteAllText(Path.Combine(parentDirectory.FullName, "package.json"), "{ \"name\": \"workspace\" }");
        File.WriteAllText(Path.Combine(parentDirectory.FullName, "yarn.lock"), string.Empty);
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package-lock.json"), "{}");

        var resolution = TypeScriptAppHostToolchainResolver.ResolveWithReason(appHostDirectory, new TestEnvironment());

        Assert.Equal(TypeScriptAppHostToolchain.Npm, resolution.Toolchain);
        Assert.Equal($"package-lock.json found in {appHostDirectory.FullName}", resolution.Reason);
    }

    [Fact]
    public void Resolve_WhenPackageLockAndYarnLockExistInSameDirectory_ReturnsYarn()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package-lock.json"), "{}");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "yarn.lock"), string.Empty);

        var resolution = TypeScriptAppHostToolchainResolver.ResolveWithReason(workspace.WorkspaceRoot, new TestEnvironment());

        Assert.Equal(TypeScriptAppHostToolchain.Yarn, resolution.Toolchain);
        Assert.Equal($"yarn.lock found in {workspace.WorkspaceRoot.FullName}", resolution.Reason);
    }

    [Fact]
    public void Resolve_WhenNativeTypeScriptUsesYarnLockWithoutVersionMetadata_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageJsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, "package.json");
        File.WriteAllText(
            packageJsonPath,
            """{ "devDependencies": { "@typescript/native": "npm:typescript@^7.0.2" } }""");
        var yarnLockPath = Path.Combine(workspace.WorkspaceRoot.FullName, "yarn.lock");
        File.WriteAllText(yarnLockPath, string.Empty);

        var exception = Assert.Throws<YarnVersionNotSupportedException>(() => TypeScriptAppHostToolchainResolver.ResolveWithReason(workspace.WorkspaceRoot, new TestEnvironment()));

        Assert.Equal(
            $"Yarn 4.18.0 or later is required for TypeScript AppHosts. Set \"packageManager\": \"yarn@4.18.0\" in {packageJsonPath} " +
            $"so Aspire can verify the Yarn version selected for {yarnLockPath}, or use npm, pnpm, or Bun.",
            exception.Message);
    }

    [Fact]
    public void Resolve_WhenNativeTypeScriptUsesParentYarnLockWithoutVersionMetadata_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(
            Path.Combine(appHostDirectory.FullName, "package.json"),
            """{ "devDependencies": { "@typescript/native": "npm:typescript@^7.0.2" } }""");
        var parentDirectory = appHostDirectory.Parent!;
        var parentPackageJsonPath = Path.Combine(parentDirectory.FullName, "package.json");
        File.WriteAllText(parentPackageJsonPath, """{ "name": "workspace" }""");
        var yarnLockPath = Path.Combine(parentDirectory.FullName, "yarn.lock");
        File.WriteAllText(yarnLockPath, string.Empty);

        var exception = Assert.Throws<YarnVersionNotSupportedException>(() => TypeScriptAppHostToolchainResolver.ResolveWithReason(appHostDirectory, new TestEnvironment()));

        Assert.Equal(
            $"Yarn 4.18.0 or later is required for TypeScript AppHosts. Set \"packageManager\": \"yarn@4.18.0\" in {parentPackageJsonPath} " +
            $"so Aspire can verify the Yarn version selected for {yarnLockPath}, or use npm, pnpm, or Bun.",
            exception.Message);
    }

    [Fact]
    public void Resolve_WhenLegacyAppHostUsesParentYarnLockWithoutVersionMetadata_ReturnsYarn()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(
            Path.Combine(appHostDirectory.FullName, "package.json"),
            """{ "devDependencies": { "typescript": "^5.9.3" } }""");
        var parentDirectory = appHostDirectory.Parent!;
        File.WriteAllText(Path.Combine(parentDirectory.FullName, "package.json"), """{ "name": "workspace" }""");
        File.WriteAllText(Path.Combine(parentDirectory.FullName, "yarn.lock"), string.Empty);

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Yarn, toolchain);
    }

    [Fact]
    public void Resolve_WhenYarnDirectoryExists_ReturnsNpm()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".yarn"));

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Npm, toolchain);
    }

    [Fact]
    public void Resolve_WhenNothingConfigured_ReturnsNpm()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Npm, toolchain);
    }

    [Fact]
    public void Resolve_WhenMarkerExists_LogsReason()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "yarn.lock"), string.Empty);

        var sink = new TestSink();
        var logger = new TestLogger(nameof(TypeScriptAppHostToolchainResolverTests), sink, logLevel => logLevel == LogLevel.Debug);

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot, new TestEnvironment(), logger);

        Assert.Equal(TypeScriptAppHostToolchain.Yarn, toolchain);
        var write = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Debug, write.LogLevel);
        Assert.Equal($"Selected TypeScript AppHost package manager 'yarn' because yarn.lock found in {workspace.WorkspaceRoot.FullName}.", write.Message);
    }

    [Fact]
    public void Resolve_WhenParentDirectoryDefinesToolchain_ReturnsParentToolchain()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDirectory.Parent!.FullName, "package.json"), "{ \"packageManager\": \"pnpm@10.12.1\" }");
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package.json"), "{ \"name\": \"apphost\" }");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Pnpm, toolchain);
    }

    [Fact]
    public void Resolve_WhenAppHostAndParentDefineDifferentToolchains_ReturnsAppHostToolchain()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDirectory.Parent!.FullName, "package.json"), "{ \"packageManager\": \"pnpm@10.12.1\" }");
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package.json"), "{ \"packageManager\": \"bun@1.2.0\" }");

        var resolution = TypeScriptAppHostToolchainResolver.ResolveWithReason(appHostDirectory, new TestEnvironment());

        Assert.Equal(TypeScriptAppHostToolchain.Bun, resolution.Toolchain);
        Assert.Equal($"packageManager 'bun@1.2.0' found in {Path.Combine(appHostDirectory.FullName, "package.json")}", resolution.Reason);
    }

    [Fact]
    public void Resolve_WhenAppHostLockFileAndParentPackageManagerDiffer_ReturnsAppHostLockFileToolchain()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDirectory.Parent!.FullName, "package.json"), "{ \"packageManager\": \"yarn@4.18.0\" }");
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "pnpm-lock.yaml"), "lockfileVersion: '9.0'");

        var resolution = TypeScriptAppHostToolchainResolver.ResolveWithReason(appHostDirectory, new TestEnvironment());

        Assert.Equal(TypeScriptAppHostToolchain.Pnpm, resolution.Toolchain);
        Assert.Equal($"pnpm-lock.yaml found in {appHostDirectory.FullName}", resolution.Reason);
    }

    [Fact]
    public void Resolve_WhenGrandparentDirectoryDefinesToolchain_ReturnsNpm()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"packageManager\": \"bun@1.2.0\" }");

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory, new TestEnvironment(), logger: null);

        Assert.Equal(TypeScriptAppHostToolchain.Npm, toolchain);
    }

    [Fact]
    public void ShouldSearchParentDirectory_WhenDirectoryIsRoot_ReturnsFalse()
    {
        var directory = new DirectoryInfo(Path.GetPathRoot(Path.GetTempPath())!);

        var shouldSearch = TypeScriptAppHostToolchainResolver.ShouldSearchParentDirectory(directory, new TestEnvironment());

        Assert.False(shouldSearch);
    }

    [Fact]
    public void ShouldSearchParentDirectory_WhenDirectoryIsHome_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var shouldSearch = TypeScriptAppHostToolchainResolver.ShouldSearchParentDirectory(
            workspace.WorkspaceRoot,
            new TestEnvironment(),
            homeDirectory: workspace.WorkspaceRoot.FullName);

        Assert.False(shouldSearch);
    }

    [Fact]
    public void ShouldSearchParentDirectory_WhenDirectoryIsHomeWithDifferentCasingOnWindows_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var shouldSearch = TypeScriptAppHostToolchainResolver.ShouldSearchParentDirectory(
            workspace.WorkspaceRoot,
            TestEnvironment.CreateWindows(),
            homeDirectory: InvertCasing(workspace.WorkspaceRoot.FullName));

        Assert.False(shouldSearch);
    }

    [Fact]
    public void ShouldSearchParentDirectory_WhenDirectoryIsHomeWithDifferentCasingOnMacOS_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var shouldSearch = TypeScriptAppHostToolchainResolver.ShouldSearchParentDirectory(
            workspace.WorkspaceRoot,
            TestEnvironment.CreateMacOS(),
            homeDirectory: InvertCasing(workspace.WorkspaceRoot.FullName));

        Assert.False(shouldSearch);
    }

    [Fact]
    public void ShouldSearchParentDirectory_WhenDirectoryIsHomeWithDifferentCasingOnLinux_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var shouldSearch = TypeScriptAppHostToolchainResolver.ShouldSearchParentDirectory(
            workspace.WorkspaceRoot,
            TestEnvironment.CreateLinux(),
            homeDirectory: InvertCasing(workspace.WorkspaceRoot.FullName));

        Assert.True(shouldSearch);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenBunSelected_UsesBunCommandsAndPreservesExtensionLaunch()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteNativeTypeScriptPackageJson(workspace);
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Bun, workspace.WorkspaceRoot);

        Assert.Equal("TypeScript (Bun)", runtimeSpec.DisplayName);
        Assert.NotNull(runtimeSpec.InstallDependencies);
        Assert.Equal("bun", runtimeSpec.InstallDependencies?.Command);
        Assert.Equal(["install"], runtimeSpec.InstallDependencies!.Args);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("bun", preExecute.Command);
        Assert.Equal(
            ["run", "tsc", "--incremental", "--tsBuildInfoFile", BuildTsBuildInfoFileName, "--outDir", BuildOutputDirectory, "--rootDir", ".", "--noEmit", "false", "--noEmitOnError", "--rewriteRelativeImportExtensions", "--sourceMap", "--inlineSources", "-p", "tsconfig.apphost.json"],
            preExecute.Args);
        Assert.Equal("bun", runtimeSpec.Execute.Command);
        Assert.Equal(["run", "{compiledAppHostFile}"], runtimeSpec.Execute.Args);
        Assert.NotNull(runtimeSpec.WatchExecute);
        Assert.Equal("bun", runtimeSpec.WatchExecute?.Command);
        Assert.Equal(
            [
                "run",
                "nodemon",
                "--signal", "SIGTERM",
                "--watch", ".",
                "--ext", "ts,mts",
                "--ignore", "node_modules/",
                "--ignore", ".aspire/modules/",
                "--exec", $"bun run tsc --incremental --tsBuildInfoFile {BuildTsBuildInfoFileName} --outDir {BuildOutputDirectory} --rootDir . --noEmit false --noEmitOnError --rewriteRelativeImportExtensions --sourceMap --inlineSources -p tsconfig.apphost.json{CleanupOnFailureSuffix} && bun run \"{{compiledAppHostFile}}\""
            ],
            runtimeSpec.WatchExecute!.Args);
        Assert.Equal("node-compiled-apphost.v1", runtimeSpec.ExtensionLaunchCapability);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenYarnSelected_UsesYarnBuildAndNodeExecution()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteNativeTypeScriptPackageJson(workspace);
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Yarn, workspace.WorkspaceRoot);

        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("yarn", preExecute.Command);
        Assert.Equal(
            ["run", "tsc", "--incremental", "--tsBuildInfoFile", BuildTsBuildInfoFileName, "--outDir", BuildOutputDirectory, "--rootDir", ".", "--noEmit", "false", "--noEmitOnError", "--rewriteRelativeImportExtensions", "--sourceMap", "--inlineSources", "-p", "tsconfig.apphost.json"],
            preExecute.Args);
        // Yarn 4 defaults to the Plug'n'Play linker, which plain `node` cannot resolve dependencies
        // under. Route the compiled AppHost through `yarn node` so it gets the same `.pnp.cjs`/
        // `.pnp.loader.mjs` hooks Yarn's own commands rely on.
        Assert.Equal("yarn", runtimeSpec.Execute.Command);
        Assert.Equal(["node", "{compiledAppHostFile}"], runtimeSpec.Execute.Args);
        Assert.Equal("yarn", runtimeSpec.WatchExecute?.Command);
        // Assert on the run half of the composite nodemon --exec string only (not the full string,
        // which also embeds the build command and is independently covered by other assertions/tests):
        // the compiled AppHost must launch via `yarn node`, matching the non-watch Execute command.
        var watchExecString = Assert.Single(runtimeSpec.WatchExecute?.Args ?? [], arg => arg.Contains("tsc", StringComparison.Ordinal));
        Assert.EndsWith("&& yarn node \"{compiledAppHostFile}\"", watchExecString);
        Assert.Contains(CleanupOnFailureSuffix, watchExecString);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenPnpmSelected_UsesPnpmBuildAndNodeExecution()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteNativeTypeScriptPackageJson(workspace);
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Pnpm, workspace.WorkspaceRoot);

        var installDependencies = Assert.IsType<CommandSpec>(runtimeSpec.InstallDependencies);
        Assert.Equal("pnpm", installDependencies.Command);
        Assert.Equal(["install", "--ignore-workspace"], installDependencies.Args);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("pnpm", preExecute.Command);
        Assert.Equal(
            ["exec", "tsc", "--incremental", "--tsBuildInfoFile", BuildTsBuildInfoFileName, "--outDir", BuildOutputDirectory, "--rootDir", ".", "--noEmit", "false", "--noEmitOnError", "--rewriteRelativeImportExtensions", "--sourceMap", "--inlineSources", "-p", "tsconfig.apphost.json"],
            preExecute.Args);
        Assert.Equal("node", runtimeSpec.Execute.Command);
        Assert.Equal(["{compiledAppHostFile}"], runtimeSpec.Execute.Args);
        Assert.Equal("pnpm", runtimeSpec.WatchExecute?.Command);
        Assert.Contains($"pnpm exec tsc --incremental --tsBuildInfoFile {BuildTsBuildInfoFileName} --outDir {BuildOutputDirectory} --rootDir . --noEmit false --noEmitOnError --rewriteRelativeImportExtensions --sourceMap --inlineSources -p tsconfig.apphost.json{CleanupOnFailureSuffix} && node \"{{compiledAppHostFile}}\"", runtimeSpec.WatchExecute?.Args ?? []);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenNpmSelected_UsesNpmBuildAndNodeExecution()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteNativeTypeScriptPackageJson(workspace);
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Npm, workspace.WorkspaceRoot);

        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("npx", preExecute.Command);
        Assert.Equal(
            ["--no-install", "tsc", "--incremental", "--tsBuildInfoFile", BuildTsBuildInfoFileName, "--outDir", BuildOutputDirectory, "--rootDir", ".", "--noEmit", "false", "--noEmitOnError", "--rewriteRelativeImportExtensions", "--sourceMap", "--inlineSources", "-p", "tsconfig.apphost.json"],
            preExecute.Args);
        Assert.Equal("node", runtimeSpec.Execute.Command);
        Assert.Equal(["{compiledAppHostFile}"], runtimeSpec.Execute.Args);
        Assert.Equal("npx", runtimeSpec.WatchExecute?.Command);
        Assert.Contains(
            $"npx --no-install tsc --incremental --tsBuildInfoFile {BuildTsBuildInfoFileName} --outDir {BuildOutputDirectory} --rootDir . --noEmit false --noEmitOnError --rewriteRelativeImportExtensions --sourceMap --inlineSources -p tsconfig.apphost.json{CleanupOnFailureSuffix} && node \"{{compiledAppHostFile}}\"",
            runtimeSpec.WatchExecute?.Args ?? []);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenBunSelectedAndNotNativeTypeScript_UsesLegacyBunFlow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // No package.json / no @typescript/native dependency: this is a brownfield AppHost that predates
        // the compile-then-run feature, so it must keep getting the original typecheck+run flow.
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Bun, workspace.WorkspaceRoot);

        Assert.Equal("TypeScript (Bun)", runtimeSpec.DisplayName);
        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("bun", preExecute.Command);
        Assert.Equal(["run", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"], preExecute.Args);
        Assert.Equal("bun", runtimeSpec.Execute.Command);
        Assert.Equal(["run", "{appHostFile}"], runtimeSpec.Execute.Args);
        Assert.NotNull(runtimeSpec.WatchExecute);
        Assert.Equal("bun", runtimeSpec.WatchExecute?.Command);
        Assert.Contains(
            "bun run tsc --noEmit -p tsconfig.apphost.json && bun run \"{appHostFile}\"",
            runtimeSpec.WatchExecute?.Args ?? []);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenYarnSelectedAndNotNativeTypeScript_UsesLegacyTsxFlow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Yarn, workspace.WorkspaceRoot);

        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("yarn", preExecute.Command);
        Assert.Equal(["run", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"], preExecute.Args);
        Assert.Equal("yarn", runtimeSpec.Execute.Command);
        Assert.Equal(["run", "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}"], runtimeSpec.Execute.Args);
        Assert.Contains(
            "yarn run tsc --noEmit -p tsconfig.apphost.json && yarn run tsx --tsconfig tsconfig.apphost.json \"{appHostFile}\"",
            runtimeSpec.WatchExecute?.Args ?? []);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenPnpmSelectedAndNotNativeTypeScript_UsesLegacyTsxFlow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Pnpm, workspace.WorkspaceRoot);

        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("pnpm", preExecute.Command);
        Assert.Equal(["exec", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"], preExecute.Args);
        Assert.Equal("pnpm", runtimeSpec.Execute.Command);
        Assert.Equal(["exec", "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}"], runtimeSpec.Execute.Args);
        Assert.Contains(
            "pnpm exec tsc --noEmit -p tsconfig.apphost.json && pnpm exec tsx --tsconfig tsconfig.apphost.json \"{appHostFile}\"",
            runtimeSpec.WatchExecute?.Args ?? []);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenNpmSelectedAndNotNativeTypeScript_UsesLegacyNpxTypecheckAndTsxFlow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Npm, workspace.WorkspaceRoot);

        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal("npx", preExecute.Command);
        Assert.Equal(["--no-install", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"], preExecute.Args);
        Assert.Equal("npx", runtimeSpec.Execute.Command);
        Assert.Equal(["--no-install", "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}"], runtimeSpec.Execute.Args);
        Assert.NotNull(runtimeSpec.WatchExecute);
        Assert.Equal("npx", runtimeSpec.WatchExecute?.Command);
        Assert.Contains(
            "npx --no-install tsc --noEmit -p tsconfig.apphost.json && npx --no-install tsx --tsconfig tsconfig.apphost.json \"{appHostFile}\"",
            runtimeSpec.WatchExecute?.Args ?? []);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenPackageJsonIsMalformed_FallsBackToLegacyFlow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ not valid json");
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Bun, workspace.WorkspaceRoot);

        // A malformed/unreadable package.json can't confirm @typescript/native, so this must land on the
        // legacy flow (never forces emit) rather than the compiled flow (which would need the version
        // guarantee that dependency provides).
        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal(["run", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"], preExecute.Args);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenNativeAliasTargetsOlderTypeScript_FallsBackToLegacyFlow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // The name "@typescript/native" alone isn't proof of TypeScript >= 7: this alias targets a TS5
        // release that predates --rewriteRelativeImportExtensions, so it must not take the compiled path.
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"),
            """{ "devDependencies": { "@typescript/native": "npm:typescript@^5.6.0" } }""");
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Bun, workspace.WorkspaceRoot);

        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal(["run", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"], preExecute.Args);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenNativeDependencyIsLocalReference_FallsBackToLegacyFlow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // A user can point "@typescript/native" at anything - a local/workspace build, a fork, a git ref.
        // None of those come with the version guarantee the compiled path relies on, so only the exact
        // "npm:typescript@<version>" alias shape qualifies.
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"),
            """{ "devDependencies": { "@typescript/native": "file:../local-typescript" } }""");
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Bun, workspace.WorkspaceRoot);

        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
        var preExecute = Assert.Single(runtimeSpec.PreExecute!);
        Assert.Equal(["run", "tsc", "--noEmit", "-p", "tsconfig.apphost.json"], preExecute.Args);
    }

    private static void WriteNativeTypeScriptPackageJson(TemporaryWorkspace workspace)
    {
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"),
            """{ "devDependencies": { "@typescript/native": "npm:typescript@^7.0.2" } }""");
    }

    private static RuntimeSpec CreateBaseRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = KnownLanguageId.TypeScript,
            DisplayName = "TypeScript (Node.js)",
            CodeGenLanguage = "TypeScript",
            DetectionPatterns = ["apphost.mts"],
            InstallDependencies = new CommandSpec
            {
                Command = "npm",
                Args = ["install"]
            },
            PreExecute =
            [
                new CommandSpec
                {
                    Command = "npx",
                    Args = ["--no-install", "tsc", "--incremental", "--tsBuildInfoFile", BuildTsBuildInfoFileName, "--outDir", BuildOutputDirectory, "--rootDir", ".", "--noEmit", "false", "-p", "tsconfig.apphost.json"]
                }
            ],
            Execute = new CommandSpec
            {
                Command = "node",
                Args = ["{compiledAppHostFile}"]
            },
            WatchExecute = new CommandSpec
            {
                Command = "npx",
                Args = ["--no-install", "nodemon", "--exec", $"npx --no-install tsc --incremental --tsBuildInfoFile {BuildTsBuildInfoFileName} --outDir {BuildOutputDirectory} --rootDir . --noEmit false -p tsconfig.apphost.json && node \"{{compiledAppHostFile}}\""]
            },
            ExtensionLaunchCapability = "node-compiled-apphost.v1"
        };
    }

    private static string InvertCasing(string value)
    {
        return new string(value.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
    }
}
