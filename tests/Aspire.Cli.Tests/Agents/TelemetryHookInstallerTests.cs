// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class TelemetryHookInstallerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task EnsureInstalledAsync_MaterializesBothScriptsUnderAspireHooksDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var home = workspace.CreateDirectory("home");
        var installer = CreateInstaller(workspace, home);

        var scripts = await installer.EnsureInstalledAsync(CancellationToken.None).DefaultTimeout();

        var expectedDirectory = Path.Combine(home.FullName, ".aspire", "hooks");
        Assert.Equal(Path.Combine(expectedDirectory, "track-telemetry.sh"), scripts.ShellScriptPath);
        Assert.Equal(Path.Combine(expectedDirectory, "track-telemetry.ps1"), scripts.PowerShellScriptPath);
        Assert.True(File.Exists(scripts.ShellScriptPath));
        Assert.True(File.Exists(scripts.PowerShellScriptPath));
    }

    [Fact]
    public async Task EnsureInstalledAsync_ShellScriptUsesLfEndingsAndNoBom()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var home = workspace.CreateDirectory("home");
        var installer = CreateInstaller(workspace, home);

        var scripts = await installer.EnsureInstalledAsync(CancellationToken.None).DefaultTimeout();

        var bytes = await File.ReadAllBytesAsync(scripts.ShellScriptPath).DefaultTimeout();
        // A UTF-8 BOM (EF BB BF) before the shebang stops the kernel from honoring `#!`.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.DoesNotContain((byte)'\r', bytes);

        var content = await File.ReadAllTextAsync(scripts.ShellScriptPath).DefaultTimeout();
        Assert.StartsWith("#!", content);
    }

    [Fact]
    public async Task EnsureInstalledAsync_IsIdempotent_WhenContentUnchanged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var home = workspace.CreateDirectory("home");
        var installer = CreateInstaller(workspace, home);

        var first = await installer.EnsureInstalledAsync(CancellationToken.None).DefaultTimeout();
        var firstShellContent = await File.ReadAllTextAsync(first.ShellScriptPath).DefaultTimeout();

        var second = await installer.EnsureInstalledAsync(CancellationToken.None).DefaultTimeout();
        var secondShellContent = await File.ReadAllTextAsync(second.ShellScriptPath).DefaultTimeout();

        Assert.Equal(first.ShellScriptPath, second.ShellScriptPath);
        Assert.Equal(firstShellContent, secondShellContent);
    }

    [Fact]
    public async Task EnsureInstalledAsync_RewritesScript_WhenExistingContentDiffers()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var home = workspace.CreateDirectory("home");
        var installer = CreateInstaller(workspace, home);

        var hooksDirectory = Path.Combine(home.FullName, ".aspire", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var shellPath = Path.Combine(hooksDirectory, "track-telemetry.sh");
        await File.WriteAllTextAsync(shellPath, "stale-content").DefaultTimeout();

        var scripts = await installer.EnsureInstalledAsync(CancellationToken.None).DefaultTimeout();

        var content = await File.ReadAllTextAsync(scripts.ShellScriptPath).DefaultTimeout();
        Assert.NotEqual("stale-content", content);
        Assert.StartsWith("#!", content);
    }

    [Fact]
    public async Task EnsureInstalledAsync_SetsExecutableBit_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var home = workspace.CreateDirectory("home");
        var installer = CreateInstaller(workspace, home);

        var scripts = await installer.EnsureInstalledAsync(CancellationToken.None).DefaultTimeout();

        var mode = File.GetUnixFileMode(scripts.ShellScriptPath);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }

    private static TelemetryHookInstaller CreateInstaller(TemporaryWorkspace workspace, DirectoryInfo home)
    {
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: home);
        return new TelemetryHookInstaller(executionContext, NullLogger<TelemetryHookInstaller>.Instance);
    }
}
