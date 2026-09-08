// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Shared;

namespace Aspire.Cli.Tests.LayoutTests;

/// <summary>
/// Covers bundle-relative discovery of Hex1b's PTY host, which CreateLayout stages at
/// <c>managed/hex1bpty.exe</c> for AppHost-owned terminals on Windows.
/// </summary>
public class Hex1bPtyHostDiscoveryTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void GetWindowsPtyHostPath_PlacesHostNextToAspireManaged()
    {
        var managedDir = Path.Combine("layout", BundleDiscovery.ManagedDirectoryName);

        var ptyHostPath = BundleDiscovery.GetWindowsPtyHostPath(managedDir);

        Assert.Equal(Path.Combine(managedDir, "hex1bpty.exe"), ptyHostPath);
    }

    [Fact]
    public void TryDiscoverWindowsPtyHost_FindsHostStagedInManagedDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "The PTY host is a Windows-only asset; discovery is expected to no-op elsewhere.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var expectedPath = CreateStagedPtyHost(layoutRoot);

        var found = BundleDiscovery.TryDiscoverWindowsPtyHostFromDirectory(layoutRoot, out var ptyHostPath);

        Assert.True(found);
        Assert.Equal(expectedPath, ptyHostPath);
    }

    [Fact]
    public void TryDiscoverWindowsPtyHost_ReturnsFalseWhenBundleIsMissingTheHost()
    {
        // A Windows bundle built before the PTY host was staged: managed/ exists and aspire-managed
        // is there, but the helper is not. Callers need to see that rather than a bogus path.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "The PTY host is a Windows-only asset; discovery is expected to no-op elsewhere.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var managedDir = Directory.CreateDirectory(
            Path.Combine(layoutRoot, BundleDiscovery.ManagedDirectoryName));
        File.WriteAllText(
            Path.Combine(managedDir.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            "stub");

        var found = BundleDiscovery.TryDiscoverWindowsPtyHostFromDirectory(layoutRoot, out var ptyHostPath);

        Assert.False(found);
        Assert.Null(ptyHostPath);
    }

    [Fact]
    public void TryDiscoverWindowsPtyHost_ReturnsFalseOnNonWindowsEvenWhenFilePresent()
    {
        // Unix drives terminals through the termios/ioctl interop shim, so a stray hex1bpty.exe in
        // the layout must never be handed back as a usable PTY host.
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "This asserts the non-Windows behavior of the discovery helper.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        CreateStagedPtyHost(layoutRoot);

        var found = BundleDiscovery.TryDiscoverWindowsPtyHostFromDirectory(layoutRoot, out var ptyHostPath);

        Assert.False(found);
        Assert.Null(ptyHostPath);
    }

    [Fact]
    public void TryDiscoverWindowsPtyHost_ReturnsFalseForMissingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var missingRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "not-a-layout");

        var found = BundleDiscovery.TryDiscoverWindowsPtyHostFromDirectory(missingRoot, out var ptyHostPath);

        Assert.False(found);
        Assert.Null(ptyHostPath);
    }

    private static string CreateStagedPtyHost(string layoutRoot)
    {
        var managedDir = Directory.CreateDirectory(
            Path.Combine(layoutRoot, BundleDiscovery.ManagedDirectoryName));
        var ptyHostPath = BundleDiscovery.GetWindowsPtyHostPath(managedDir.FullName);
        File.WriteAllText(ptyHostPath, "stub");
        return ptyHostPath;
    }
}
