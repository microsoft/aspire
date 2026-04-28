// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;
using Aspire.TypeSystem;

namespace Aspire.Cli.Tests.Projects;

public sealed class TypeScriptAppHostToolchainResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Resolve_WhenPackageManagerIsBun_ReturnsBun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"packageManager\": \"bun@1.2.0\" }");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot);

        Assert.Equal(TypeScriptAppHostToolchain.Bun, toolchain);
    }

    [Fact]
    public void Resolve_WhenPnpmLockExists_ReturnsPnpm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "pnpm-lock.yaml"), "lockfileVersion: '9.0'");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot);

        Assert.Equal(TypeScriptAppHostToolchain.Pnpm, toolchain);
    }

    [Fact]
    public void Resolve_WhenYarnDirectoryExists_ReturnsYarn()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".yarn"));

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot);

        Assert.Equal(TypeScriptAppHostToolchain.Yarn, toolchain);
    }

    [Fact]
    public void Resolve_WhenNothingConfigured_ReturnsNpm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"name\": \"apphost\" }");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot);

        Assert.Equal(TypeScriptAppHostToolchain.Npm, toolchain);
    }

    [Fact]
    public void Resolve_WhenWorkspaceRootDefinesToolchain_ReturnsParentToolchain()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"packageManager\": \"pnpm@10.12.1\" }");

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apps").CreateSubdirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package.json"), "{ \"name\": \"apphost\" }");

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory);

        Assert.Equal(TypeScriptAppHostToolchain.Pnpm, toolchain);
    }

    [Fact]
    public void Resolve_WhenToolchainConfigurationIsBeyondMaxDepth_ReturnsNpm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"packageManager\": \"bun@1.2.0\" }");

        var currentDirectory = workspace.WorkspaceRoot;
        for (var i = 0; i < TypeScriptAppHostToolchainResolver.MaxParentSearchDepth + 1; i++)
        {
            currentDirectory = currentDirectory.CreateSubdirectory($"level{i}");
        }

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(currentDirectory);

        Assert.Equal(TypeScriptAppHostToolchain.Npm, toolchain);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenBunSelected_UsesBunCommandsAndPreservesExtensionLaunch()
    {
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Bun);

        Assert.Equal("TypeScript (Bun)", runtimeSpec.DisplayName);
        Assert.NotNull(runtimeSpec.InstallDependencies);
        Assert.Equal("bun", runtimeSpec.InstallDependencies?.Command);
        Assert.Equal(["install"], runtimeSpec.InstallDependencies!.Args);
        Assert.Equal("bun", runtimeSpec.Execute.Command);
        Assert.Equal(["run", "{appHostFile}"], runtimeSpec.Execute.Args);
        Assert.NotNull(runtimeSpec.WatchExecute);
        Assert.Equal("bun", runtimeSpec.WatchExecute?.Command);
        Assert.Equal(["--watch", "run", "{appHostFile}"], runtimeSpec.WatchExecute!.Args);
        Assert.Equal("node", runtimeSpec.ExtensionLaunchCapability);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WhenYarnSelected_UsesYarnExecCommands()
    {
        var baseRuntimeSpec = CreateBaseRuntimeSpec();

        var runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(baseRuntimeSpec, TypeScriptAppHostToolchain.Yarn);

        Assert.Equal("yarn", runtimeSpec.Execute.Command);
        Assert.Equal(["exec", "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}"], runtimeSpec.Execute.Args);
        Assert.Equal("yarn", runtimeSpec.WatchExecute?.Command);
        Assert.Contains("yarn exec tsx --tsconfig tsconfig.apphost.json {appHostFile}", runtimeSpec.WatchExecute?.Args ?? []);
    }

    private static RuntimeSpec CreateBaseRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = KnownLanguageId.TypeScript,
            DisplayName = "TypeScript (Node.js)",
            CodeGenLanguage = "TypeScript",
            DetectionPatterns = ["apphost.ts"],
            InstallDependencies = new CommandSpec
            {
                Command = "npm",
                Args = ["install"]
            },
            Execute = new CommandSpec
            {
                Command = "npx",
                Args = ["--no-install", "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}"]
            },
            WatchExecute = new CommandSpec
            {
                Command = "npx",
                Args = ["--no-install", "nodemon", "--exec", "npx --no-install tsx --tsconfig tsconfig.apphost.json {appHostFile}"]
            },
            ExtensionLaunchCapability = "node"
        };
    }
}
