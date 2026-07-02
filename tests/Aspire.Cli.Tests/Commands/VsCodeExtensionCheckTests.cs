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
        var check = new VsCodeExtensionCheck(() => new VsCodeExtensionDetection(VsCodeInstalled: false, ExtensionInstalled: false));

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenExtensionMissing()
    {
        var check = new VsCodeExtensionCheck(() => new VsCodeExtensionDetection(VsCodeInstalled: true, ExtensionInstalled: false));

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
        var check = new VsCodeExtensionCheck(() => new VsCodeExtensionDetection(VsCodeInstalled: true, ExtensionInstalled: true));

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckCategories.DevelopmentTools, result.Category);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata["vsCodeInstalled"]!.GetValue<bool>());
        Assert.True(result.Metadata["extensionInstalled"]!.GetValue<bool>());
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
