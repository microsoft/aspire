// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public sealed class TypeScriptAppHostToolingCheckTests(ITestOutputHelper outputHelper)
{
    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.mts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.mts");

    [Theory]
    [InlineData("npm@10.5.0", nameof(TypeScriptAppHostToolchain.Npm))]
    [InlineData("bun@1.2.0", nameof(TypeScriptAppHostToolchain.Bun))]
    [InlineData("yarn@4.14.1", nameof(TypeScriptAppHostToolchain.Yarn))]
    [InlineData("pnpm@10.12.1", nameof(TypeScriptAppHostToolchain.Pnpm))]
    [InlineData("deno@2.9.0", nameof(TypeScriptAppHostToolchain.Deno))]
    public async Task CheckAsync_ReturnsPass_WhenConfiguredToolchainIsAvailable(string packageManagerSpec, string toolchainName)
    {
        var toolchain = Enum.Parse<TypeScriptAppHostToolchain>(toolchainName);
        var requiredCommands = TypeScriptAppHostToolchainResolver.GetRequiredCommands(toolchain);

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, $"{{ \"packageManager\": \"{packageManagerSpec}\" }}");

        var check = CreateCheck(
            workspace,
            appHostFile,
            commandResolver: command => requiredCommands.Contains(command, StringComparer.OrdinalIgnoreCase) ? $"/usr/bin/{command}" : null);

        var results = await check.CheckAsync().DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(TypeScriptAppHostToolingCheck.ToolsCheckName, result.Name);
        foreach (var command in requiredCommands)
        {
            Assert.Contains(command, result.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("npm@10.5.0", nameof(TypeScriptAppHostToolchain.Npm), "Node.js", "https://nodejs.org/en/download")]
    [InlineData("bun@1.2.0", nameof(TypeScriptAppHostToolchain.Bun), "Bun", "https://bun.sh/docs/installation")]
    [InlineData("yarn@4.14.1", nameof(TypeScriptAppHostToolchain.Yarn), "Yarn", "https://yarnpkg.com/getting-started/install")]
    [InlineData("pnpm@10.12.1", nameof(TypeScriptAppHostToolchain.Pnpm), "pnpm", "https://pnpm.io/installation")]
    [InlineData("deno@2.9.0", nameof(TypeScriptAppHostToolchain.Deno), "Deno", "https://docs.deno.com/runtime/getting_started/installation/")]
    public async Task CheckAsync_ReturnsFail_WhenConfiguredToolchainIsMissing(
        string packageManagerSpec,
        string toolchainName,
        string installDisplayName,
        string expectedInstallLink)
    {
        var toolchain = Enum.Parse<TypeScriptAppHostToolchain>(toolchainName);
        var requiredCommands = TypeScriptAppHostToolchainResolver.GetRequiredCommands(toolchain);

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, $"{{ \"packageManager\": \"{packageManagerSpec}\" }}");

        var check = CreateCheck(workspace, appHostFile, commandResolver: _ => null);

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Equal(requiredCommands.Length, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
            Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
            Assert.Equal(expectedInstallLink, result.Link);
            Assert.Equal(
                $"Install {installDisplayName} tooling and rerun 'aspire doctor'.",
                result.Fix);
            Assert.NotNull(result.Details);
            Assert.Contains(installDisplayName, result.Details!);
        });

        foreach (var command in requiredCommands)
        {
            var commandResult = Assert.Single(results, r => r.Name == TypeScriptAppHostToolingCheck.GetMissingCommandCheckName(command));
            Assert.Contains($"'{command}'", commandResult.Message, StringComparison.Ordinal);
            Assert.Contains(command, commandResult.Details!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("npx")]
    public async Task CheckAsync_ReturnsFailOnlyForMissingNpmCommand_WhenTheOtherIsPresent(string missingCommand)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, "{ \"packageManager\": \"npm@10.5.0\" }");

        var check = CreateCheck(
            workspace,
            appHostFile,
            commandResolver: command => command.Equals(missingCommand, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"/usr/bin/{command}");

        var results = await check.CheckAsync().DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(TypeScriptAppHostToolingCheck.GetMissingCommandCheckName(missingCommand), result.Name);
        Assert.Equal("https://nodejs.org/en/download", result.Link);
        Assert.Contains($"'{missingCommand}'", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_Skips_WhenNoTypeScriptAppHostExists()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var check = CreateCheck(workspace, appHostFile: null, commandResolver: _ => null);

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenPackageManagerIsYarnClassic()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, "{ \"packageManager\": \"yarn@1.22.22\" }");

        var check = CreateCheck(workspace, appHostFile, commandResolver: command => $"/usr/bin/{command}");

        var results = await check.CheckAsync().DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(TypeScriptAppHostToolingCheck.YarnClassicCheckName, result.Name);
        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal("https://yarnpkg.com/getting-started/install", result.Link);
        Assert.Equal("TypeScript AppHost does not support Yarn Classic.", result.Message);
        Assert.Contains("Yarn Classic is not supported", result.Details ?? string.Empty);
        Assert.Contains("yarn@1.22.22", result.Details ?? string.Empty);
        Assert.Equal("Upgrade to Yarn 4 or later, or switch to npm, pnpm, Bun, or Deno, then rerun 'aspire doctor'.", result.Fix);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenYarnClassicLockFileIsPresent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, "{ \"name\": \"apphost\" }");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "yarn.lock"),
            "# THIS IS AN AUTOGENERATED FILE. DO NOT EDIT THIS FILE DIRECTLY.\n# yarn lockfile v1\n");

        var check = CreateCheck(workspace, appHostFile, commandResolver: command => $"/usr/bin/{command}");

        var results = await check.CheckAsync().DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(TypeScriptAppHostToolingCheck.YarnClassicCheckName, result.Name);
        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal("https://yarnpkg.com/getting-started/install", result.Link);
        Assert.Contains("yarn.lock", result.Details ?? string.Empty);
        Assert.Equal("Upgrade to Yarn 4 or later, or switch to npm, pnpm, Bun, or Deno, then rerun 'aspire doctor'.", result.Fix);
    }

    [Theory]
    [InlineData("deno@0.224.0")]
    [InlineData("deno@1.46.3")]
    public async Task CheckAsync_ReturnsFail_WhenConfiguredPackageManagerIsPreV2Deno(string packageManager)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, $$"""{ "packageManager": "{{packageManager}}" }""");
        var check = CreateCheck(
            workspace,
            appHostFile,
            commandResolver: _ => "/usr/bin/deno",
            denoVersionResolver: (_, _) => Task.FromResult<string?>("deno 2.9.0\nv8 14.0.365.5-rusty\ntypescript 5.9.2"));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertDenoVersionFailure(result);
        Assert.Contains(packageManager, result.Details);
    }

    [Theory]
    [InlineData("deno.lock")]
    [InlineData("deno.json")]
    public async Task CheckAsync_ReturnsFail_WhenDetectedDenoExecutableIsV1(string markerFile)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, "{ \"name\": \"apphost\" }");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, markerFile), "{}");
        var check = CreateCheck(
            workspace,
            appHostFile,
            commandResolver: _ => "/usr/bin/deno",
            denoVersionResolver: (_, _) => Task.FromResult<string?>("deno 1.46.3\nv8 12.9.202.6\ntypescript 5.5.2"));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertDenoVersionFailure(result);
        Assert.Equal("Deno 1 is installed, but TypeScript AppHosts require Deno 2 or later.", result.Details);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenDenoVersionCannotBeDetermined()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateTypeScriptAppHost(workspace, "{ \"packageManager\": \"deno@2.9.0\" }");
        var check = CreateCheck(
            workspace,
            appHostFile,
            commandResolver: _ => "/usr/bin/deno",
            denoVersionResolver: (_, _) => Task.FromResult<string?>(null));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertDenoVersionFailure(result);
        Assert.Equal("The installed Deno version could not be determined. TypeScript AppHosts require Deno 2 or later.", result.Details);
    }

    [Theory]
    [InlineData("deno 2.9.0 (stable, release, aarch64-apple-darwin)\nv8 14.0.365.5-rusty\ntypescript 5.9.2", 2)]
    [InlineData("deno 10.1.2\nv8 15.0\ntypescript 6.0", 10)]
    public void TryParseDenoMajorVersion_ParsesVersionOutput(string output, int expectedMajorVersion)
    {
        Assert.True(TypeScriptAppHostToolingCheck.TryParseDenoMajorVersion(output, out var majorVersion));
        Assert.Equal(expectedMajorVersion, majorVersion);
    }

    private static FileInfo CreateTypeScriptAppHost(TemporaryWorkspace workspace, string packageJsonContent)
    {
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        File.WriteAllText(appHostPath, "await Promise.resolve();");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), packageJsonContent);
        return new FileInfo(appHostPath);
    }

    private static TypeScriptAppHostToolingCheck CreateCheck(
        TemporaryWorkspace workspace,
        FileInfo? appHostFile,
        Func<string, string?> commandResolver,
        Func<string, CancellationToken, Task<string?>>? denoVersionResolver = null)
    {
        var projectLocator = new TestProjectLocator
        {
            GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult(appHostFile)
        };

        return new TypeScriptAppHostToolingCheck(
            projectLocator,
            new TestLanguageDiscovery(s_typeScriptLanguage),
            CreateExecutionContext(workspace),
            new TestEnvironment(),
            NullLogger<TypeScriptAppHostToolingCheck>.Instance,
            commandResolver,
            denoVersionResolver ?? ((_, _) => Task.FromResult<string?>("deno 2.9.0\nv8 14.0.365.5-rusty\ntypescript 5.9.2")));
    }

    private static void AssertDenoVersionFailure(EnvironmentCheckResult result)
    {
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(TypeScriptAppHostToolingCheck.DenoVersionCheckName, result.Name);
        Assert.Equal("TypeScript AppHost requires Deno 2 or later.", result.Message);
        Assert.Equal("Upgrade to Deno 2 or later and rerun 'aspire doctor'.", result.Fix);
        Assert.Equal("https://docs.deno.com/runtime/getting_started/installation/", result.Link);
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace) =>
        new(
            workingDirectory: workspace.WorkspaceRoot,
            hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-hives"),
            cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-cache"),
            sdksDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-sdks"),
            logsDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-logs"),
            logFilePath: "test.log",
            identityChannel: "local");
}
