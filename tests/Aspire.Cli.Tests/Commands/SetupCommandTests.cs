// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;

namespace Aspire.Cli.Tests.Commands;

// `SetupCommand.GetDefaultInstallPath` returns parent-of-binary regardless of
// install source — distinct from the source-aware `BundleService.GetDefaultExtractDir`
// used by auto-extract. These tests pin the source-independent contract.
public class SetupCommandTests
{
    [Fact]
    public void GetDefaultInstallPath_ScriptSourceLayout_ReturnsParentOfBinaryDirectory()
    {
        var processPath = Path.Combine("home", ".aspire", "bin", "aspire");

        var result = SetupCommand.GetDefaultInstallPath(processPath);

        Assert.Equal(Path.Combine("home", ".aspire"), result);
    }

    [Fact]
    public void GetDefaultInstallPath_PRSourceLayout_ReturnsParentOfBinaryDirectory()
    {
        var processPath = Path.Combine("home", ".aspire", "dogfood", "pr-12345", "bin", "aspire");

        var result = SetupCommand.GetDefaultInstallPath(processPath);

        Assert.Equal(Path.Combine("home", ".aspire", "dogfood", "pr-12345"), result);
    }

    [Fact]
    public void GetDefaultInstallPath_ManagedSourceFlatLayout_ReturnsParentOfBinaryDirectory()
    {
        // Managed-source layout is flat (no bin/ subdir). `BundleService.GetDefaultExtractDir`
        // diverges here and keeps the payload inside the binary's dir so package-manager uninstall reaches it.
        var processPath = Path.Combine("Program Files", "WindowsApps", "Microsoft.Aspire_xyz", "aspire.exe");

        var result = SetupCommand.GetDefaultInstallPath(processPath);

        Assert.Equal(Path.Combine("Program Files", "WindowsApps"), result);
    }

    [Fact]
    public void GetDefaultInstallPath_BareFilename_ReturnsNull()
    {
        var result = SetupCommand.GetDefaultInstallPath("aspire");

        Assert.Null(result);
    }

    [Fact]
    public void GetDefaultInstallPath_EmptyProcessPath_ReturnsNull()
    {
        var result = SetupCommand.GetDefaultInstallPath(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void GetDefaultInstallPath_NullProcessPath_ReturnsNull()
    {
        var result = SetupCommand.GetDefaultInstallPath(null);

        Assert.Null(result);
    }
}
