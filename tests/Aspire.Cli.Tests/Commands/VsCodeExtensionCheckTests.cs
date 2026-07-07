// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Commands;

public class VsCodeExtensionCheckTests
{
    [Fact]
    public async Task CheckAsync_ReturnsEmpty_WhenVsCodeNotInstalled()
    {
        using var home = new TempDirectory();
        // No TERM_PROGRAM and nothing resolvable on PATH, so real detection reports VS Code absent.
        var environment = new TestEnvironment(new Dictionary<string, string?>());
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home.DirectoryInfo, homeDirectory: home.DirectoryInfo);
        var check = new VsCodeExtensionCheck(environment, executionContext, _ => null);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenExtensionMissing()
    {
        using var home = new TempDirectory();
        using var extensions = new TempDirectory();
        // VS Code is present (TERM_PROGRAM) but the override extensions directory is empty.
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.Path
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home.DirectoryInfo, homeDirectory: home.DirectoryInfo);
        var check = new VsCodeExtensionCheck(environment, executionContext, _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckCategories.DevelopmentTools, result.Category);
        Assert.Equal(VsCodeExtensionCheck.CheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionMissingMessage, result.Message);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionMissingFix, result.Fix);
        Assert.Equal(VsCodeExtensionCheck.MarketplaceUrl, result.Link);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata["vsCodeInstalled"]!.GetValue<bool>());
        Assert.False(result.Metadata["extensionInstalled"]!.GetValue<bool>());
        Assert.Equal(VsCodeExtensionCheck.ExtensionId, result.Metadata["extensionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenExtensionInstalled()
    {
        using var home = new TempDirectory();
        using var extensions = new TempDirectory();
        // VS Code is present and the Aspire extension is installed in the override extensions directory.
        Directory.CreateDirectory(Path.Combine(extensions.Path, "microsoft-aspire.aspire-vscode-1.2.3"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.Path
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home.DirectoryInfo, homeDirectory: home.DirectoryInfo);
        var check = new VsCodeExtensionCheck(environment, executionContext, _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckCategories.DevelopmentTools, result.Category);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata["vsCodeInstalled"]!.GetValue<bool>());
        Assert.True(result.Metadata["extensionInstalled"]!.GetValue<bool>());
        Assert.Equal(VsCodeExtensionCheck.ExtensionId, result.Metadata["extensionId"]!.GetValue<string>());
    }

    [Fact]
    public void Detect_FindsExtension_ViaVsCodeExtensionsOverride()
    {
        using var home = new TempDirectory();
        using var extensions = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(extensions.Path, "microsoft-aspire.aspire-vscode-1.2.3"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.Path
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
    }

    [Theory]
    [InlineData(".vscode")]
    [InlineData(".vscode-insiders")]
    [InlineData(".vscode-server")]
    [InlineData(".vscode-server-insiders")]
    public void Detect_FindsExtension_ViaEachDefaultExtensionsRoot(string rootFolder)
    {
        using var home = new TempDirectory();
        // Exercise each default extensions root that GetExtensionDirectories composes (desktop
        // stable/Insiders and remote/server) rather than the VSCODE_EXTENSIONS override.
        Directory.CreateDirectory(Path.Combine(home.Path, rootFolder, "extensions", "microsoft-aspire.aspire-vscode-1.2.3"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode"
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_IgnoresDefaultRoots_WhenVsCodeExtensionsOverrideSet()
    {
        using var home = new TempDirectory();
        using var overrideDirectory = new TempDirectory();
        // The extension is present in the default desktop root but absent from the override directory.
        // VSCODE_EXTENSIONS makes VS Code load only the override, so detection must report it missing.
        Directory.CreateDirectory(Path.Combine(home.Path, ".vscode", "extensions", "microsoft-aspire.aspire-vscode-1.2.3"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = overrideDirectory.Path
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("code-insiders")]
    public void Detect_DetectsVsCode_ViaPathFallback_WhenTermProgramNotVsCode(string launcherOnPath)
    {
        using var home = new TempDirectory();
        // No TERM_PROGRAM, so detection falls back to probing the CLI launchers on PATH via the
        // injected resolver.
        var environment = new TestEnvironment(new Dictionary<string, string?>());
        string? Resolver(string command) => string.Equals(command, launcherOnPath, StringComparison.Ordinal) ? "/usr/bin/" + command : null;

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo, Resolver);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsVsCodeNotInstalled_WhenTermProgramAbsentAndNotOnPath()
    {
        using var home = new TempDirectory();
        var environment = new TestEnvironment(new Dictionary<string, string?>());

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo, _ => null);

        Assert.False(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_MatchesExtensionFolder_CaseInsensitively()
    {
        using var home = new TempDirectory();
        using var extensions = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(extensions.Path, "Microsoft-Aspire.Aspire-VSCode-9.9.9"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.Path
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo);

        Assert.True(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsExtensionMissing_WhenOnlyUnrelatedExtensionsPresent()
    {
        using var home = new TempDirectory();
        using var extensions = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(extensions.Path, "ms-dotnettools.csharp-2.0.0"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.Path
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsExtensionMissing_WhenFolderSharesPrefixWithDifferentId()
    {
        using var home = new TempDirectory();
        using var extensions = new TempDirectory();
        // A different extension whose id begins with ours. Without the digit boundary the prefix match
        // would incorrectly treat this as the Aspire extension.
        Directory.CreateDirectory(Path.Combine(extensions.Path, "microsoft-aspire.aspire-vscode-extras-1.0.0"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.Path
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo);

        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsExtensionMissing_WhenExtensionsDirectoryDoesNotExist()
    {
        using var home = new TempDirectory();
        // Point the override at a path that is never created so DirectoryContainsExtension hits the
        // Directory.Exists == false guard. VS Code being present must still yield a clean "missing"
        // result rather than throwing on the absent directory.
        var missingExtensionsDirectory = Path.Combine(home.Path, "does-not-exist");

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = missingExtensionsDirectory
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home.DirectoryInfo);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            DirectoryInfo = Directory.CreateTempSubdirectory("aspire-vscode-check-tests");
        }

        public DirectoryInfo DirectoryInfo { get; }

        public string Path => DirectoryInfo.FullName;

        public void Dispose()
        {
            try
            {
                DirectoryInfo.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
