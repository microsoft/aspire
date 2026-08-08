// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Text.Json;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Commands;

public class VsCodeExtensionCheckTests(ITestOutputHelper outputHelper)
{
    private const string ReportedVersionVariable = VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable;

    [Fact]
    public async Task CheckAsync_ReturnsEmpty_WhenVsCodeNotInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // No TERM_PROGRAM and nothing resolvable on PATH, so real detection reports VS Code absent.
        var environment = new TestEnvironment(new Dictionary<string, string?>());
        var marketplaceClient = CreateUnusedMarketplaceClient();
        var check = CreateCheck(environment, home, marketplaceClient);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Equal(0, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenExtensionMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // VS Code is present (TERM_PROGRAM) but the extension contributed no version and the override
        // extensions directory is empty.
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });
        var marketplaceClient = CreateUnusedMarketplaceClient();
        var check = CreateCheck(environment, home, marketplaceClient);

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
        Assert.Equal(0, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenReportedVersionMatchesMarketplace()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.2.3", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

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
        Assert.Equal("1.2.3", result.Metadata["extensionVersion"]!.GetValue<string>());
        Assert.Equal("1.2.3", result.Metadata["latestVersion"]!.GetValue<string>());
        Assert.Equal("stable", result.Metadata["latestVersionChannel"]!.GetValue<string>());
        Assert.False(result.Metadata["updateAvailable"]!.GetValue<bool>());
        Assert.True(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
        Assert.Equal(1, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenReportedVersionIsOutOfDate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.5.0", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.VsCodeExtensionOutOfDateMessageFormat, "1.2.3", "1.5.0"),
            result.Message);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionOutOfDateFix, result.Fix);
        Assert.Equal(VsCodeExtensionCheck.MarketplaceUrl, result.Link);
        Assert.NotNull(result.Metadata);
        Assert.Equal("1.2.3", result.Metadata["extensionVersion"]!.GetValue<string>());
        Assert.Equal("1.5.0", result.Metadata["latestVersion"]!.GetValue<string>());
        Assert.True(result.Metadata["updateAvailable"]!.GetValue<bool>());
        Assert.Equal(1, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenReportedVersionIsNewerThanMarketplaceVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // A locally built or side-loaded extension can sit ahead of the gallery. That is not an
        // out-of-date install, so it must not produce a warning.
        var environment = CreateVsCodeEnvironmentWithReportedVersion("9.9.9");
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.5.0", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.False(result.Metadata!["updateAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_ComparesOnDiskVersion_WhenExtensionDidNotReportItsVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // Regression test for the reported defect: an extension predating the environment variable, or
        // a doctor run outside a VS Code-created process, must still be compared rather than passing.
        CreateInstalledExtension(extensions, "1.2.3");
        var environment = CreateVsCodeEnvironmentWithoutReportedVersion(extensions);
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.9.0", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                DoctorCommandStrings.VsCodeExtensionOutOfDateMessageFormat,
                "1.2.3",
                "1.9.0"),
            result.Message);
        Assert.Equal("1.2.3", result.Metadata!["extensionVersion"]!.GetValue<string>());
        Assert.Equal("manifest", result.Metadata["extensionVersionSource"]!.GetValue<string>());
        Assert.True(result.Metadata["updateAvailable"]!.GetValue<bool>());
        Assert.Equal(1, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenOnDiskVersionIsCurrent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        CreateInstalledExtension(extensions, "1.9.0");
        var environment = CreateVsCodeEnvironmentWithoutReportedVersion(extensions);
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.9.0", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Equal("1.9.0", result.Metadata!["extensionVersion"]!.GetValue<string>());
        Assert.False(result.Metadata["updateAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_PrefersManifestVersionOverFolderName()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // The folder name is a convention; the manifest is the contract, so the manifest wins.
        CreateInstalledExtension(extensions, folderVersion: "1.2.3", manifestVersion: "1.9.0");
        var environment = CreateVsCodeEnvironmentWithoutReportedVersion(extensions);
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.9.0", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("1.9.0", result.Metadata!["extensionVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_UsesFolderVersion_WhenManifestIsMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        CreateInstalledExtension(extensions, folderVersion: "1.2.3", manifestVersion: null);
        var environment = CreateVsCodeEnvironmentWithoutReportedVersion(extensions);
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.2.3", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("1.2.3", result.Metadata!["extensionVersion"]!.GetValue<string>());
    }

    [Theory]
    // VS Code leaves the previous directory behind after an upgrade, so the highest version wins.
    // The 1.9.0/1.10.0 pair is ordered by semver precedence, where an ordinal string sort is wrong.
    [InlineData(new[] { "1.9.0", "1.10.0" }, "1.10.0")]
    [InlineData(new[] { "1.10.0", "1.9.0" }, "1.10.0")]
    [InlineData(new[] { "1.2.3", "1.9.0", "1.10.2" }, "1.10.2")]
    public async Task CheckAsync_SelectsHighestInstalledVersion(string[] installedVersions, string expectedVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        foreach (var installedVersion in installedVersions)
        {
            CreateInstalledExtension(extensions, installedVersion);
        }

        var environment = CreateVsCodeEnvironmentWithoutReportedVersion(extensions);
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse(expectedVersion, SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(expectedVersion, result.Metadata!["extensionVersion"]!.GetValue<string>());
        Assert.False(result.Metadata["updateAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnknownWarning_WhenInstalledVersionCannotBeDetermined()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // The extension is installed but neither the manifest nor the folder name yields a version, so
        // the outcome must be a distinct "unknown" warning rather than a pass on absent evidence.
        var extensionDirectory = Directory.CreateDirectory(
            Path.Combine(extensions.FullName, "microsoft-aspire.aspire-vscode-1.x-dev"));
        File.WriteAllText(Path.Combine(extensionDirectory.FullName, "package.json"), "{ \"name\": \"aspire-vscode\" }");
        var environment = CreateVsCodeEnvironmentWithoutReportedVersion(extensions);
        var marketplaceClient = CreateUnusedMarketplaceClient();
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionVersionUnknownMessage, result.Message);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionVersionUnknownFix, result.Fix);
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                DoctorCommandStrings.VsCodeExtensionVersionUnknownSearchedDetailsFormat,
                extensions.FullName),
            result.Details);
        Assert.False(result.Metadata!["extensionVersionKnown"]!.GetValue<bool>());
        Assert.Equal("unknown", result.Metadata["extensionVersionSource"]!.GetValue<string>());
        Assert.Equal(0, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_FallsBackToDisk_WhenReportedVersionCannotBeParsed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // A corrupted variable must not be trusted, and must not short-circuit the disk scan either.
        CreateInstalledExtension(extensions, "1.2.3");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName,
            [ReportedVersionVariable] = "not-a-version"
        });
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.2.3", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("1.2.3", result.Metadata!["extensionVersion"]!.GetValue<string>());
        Assert.Equal("manifest", result.Metadata["extensionVersionSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_IgnoresPlatformSuffixedFolderName_WhenManifestIsMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // A platform-specific VSIX unpacks to "<id>-1.2.3-darwin-arm64", whose suffix parses as the
        // semver pre-release "1.2.3-darwin-arm64". Without a manifest that is not a usable version.
        Directory.CreateDirectory(
            Path.Combine(extensions.FullName, "microsoft-aspire.aspire-vscode-1.2.3-darwin-arm64"));
        var environment = CreateVsCodeEnvironmentWithoutReportedVersion(extensions);
        var marketplaceClient = CreateUnusedMarketplaceClient();
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionVersionUnknownMessage, result.Message);
        Assert.Equal(0, marketplaceClient.CallCount);
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("json")]
    [InlineData("timeout")]
    public async Task CheckAsync_ReturnsWarningWithDiagnostics_WhenMarketplaceLookupFails(string failureKind)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        Exception failure = failureKind switch
        {
            "dns" => new HttpRequestException("Name resolution failed."),
            "http" => new HttpRequestException("Marketplace returned 503.", null, HttpStatusCode.ServiceUnavailable),
            "io" => new IOException("Marketplace response stream failed."),
            "json" => new JsonException("Marketplace response was invalid JSON."),
            "timeout" => new TimeoutException("Marketplace request timed out."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromException<SemVersion>(failure)
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Equal(
            failureKind == "timeout"
                ? DoctorCommandStrings.VsCodeExtensionLatestVersionCheckTimedOutDetails
                : DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails,
            result.Details);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
        Assert.NotNull(result.Metadata);
        Assert.Equal("1.2.3", result.Metadata["extensionVersion"]!.GetValue<string>());
        Assert.Equal(failureKind == "timeout" ? "timeout" : "unavailable", result.Metadata["latestVersionError"]!.GetValue<string>());
        Assert.False(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
        Assert.Equal(1, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarningWithDiagnostics_WhenMarketplaceResponseIsInvalid()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        var failure = new InvalidDataException("Marketplace response did not contain the Aspire extension.");
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            GetLatestVersionsAsyncCallback = _ => Task.FromException<VsCodeExtensionMarketplaceVersions>(failure)
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails, result.Details);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
        Assert.NotNull(result.Metadata);
        Assert.Equal("1.2.3", result.Metadata["extensionVersion"]!.GetValue<string>());
        Assert.Equal("unavailable", result.Metadata["latestVersionError"]!.GetValue<string>());
        Assert.False(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
        Assert.Equal(1, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarningWithDiagnostics_WhenMarketplaceOmitsTheRequestedChannel()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // A semver pre-release tag selects the pre-release feed, but the gallery can report only a
        // stable version. Rather than silently comparing against the wrong channel, report the
        // lookup as unavailable.
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.3.0-preview.1.25601.3");
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromResult(SemVersion.Parse("1.2.3", SemVersionStyles.Strict))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails, result.Details);
        Assert.Equal("unavailable", result.Metadata!["latestVersionError"]!.GetValue<string>());
        Assert.False(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_PropagatesUnexpectedMarketplaceCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        using var internalCancellation = new CancellationTokenSource();
        internalCancellation.Cancel();
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = _ => Task.FromCanceled<SemVersion>(internalCancellation.Token)
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckAsync_PropagatesUnexpectedMarketplaceFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            GetLatestVersionsAsyncCallback = _ => throw new InvalidOperationException("Unexpected implementation failure.")
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Unexpected implementation failure.", exception.Message);
    }

    [Fact]
    public async Task CheckAsync_PropagatesCallerCancellationDuringMarketplaceLookup()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        using var cancellationTokenSource = new CancellationTokenSource();
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            StableVersionCallback = cancellationToken =>
            {
                cancellationTokenSource.Cancel();
                return Task.FromCanceled<SemVersion>(cancellationToken);
            }
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => check.CheckAsync(cancellationTokenSource.Token));
        Assert.Equal(1, marketplaceClient.CallCount);
    }

    [Fact]
    public async Task CheckAsync_DoesNotCheckMarketplace_WhenUpdateNotificationsAreDisabled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        var marketplaceClient = CreateUnusedMarketplaceClient();
        var features = new TestFeatures().SetFeature(KnownFeatures.UpdateNotificationsEnabled, false);
        var check = new VsCodeExtensionCheck(
            environment,
            TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home),
            marketplaceClient,
            features,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.False(result.Metadata!["updateCheckEnabled"]!.GetValue<bool>());
        Assert.False(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
        Assert.Equal(0, marketplaceClient.CallCount);
    }

    [Theory]
    [InlineData("1.2.3-preview.1", "1.3.0-preview.1", true)]
    [InlineData("1.2.3-preview.1", "1.2.3-preview.1", false)]
    [InlineData("1.3.0-preview.2", "1.3.0-preview.1", false)]
    public async Task CheckAsync_UsesPreReleaseChannel_WhenReportedVersionHasAPreReleaseTag(
        string installedVersion,
        string latestPreReleaseVersion,
        bool expectedUpdateAvailable)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion(installedVersion);
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            GetLatestVersionsAsyncCallback = _ => Task.FromResult(new VsCodeExtensionMarketplaceVersions(
                // Deliberately far ahead of every input so a stable-channel comparison would warn,
                // proving the pre-release feed is the one being used.
                StableVersion: SemVersion.Parse("99.0.0", SemVersionStyles.Strict),
                PreReleaseVersion: SemVersion.Parse(latestPreReleaseVersion, SemVersionStyles.Strict)))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            expectedUpdateAvailable ? EnvironmentCheckStatus.Warning : EnvironmentCheckStatus.Pass,
            result.Status);
        Assert.Equal(latestPreReleaseVersion, result.Metadata!["latestVersion"]!.GetValue<string>());
        Assert.Equal("pre-release", result.Metadata["latestVersionChannel"]!.GetValue<string>());
        Assert.Equal(expectedUpdateAvailable, result.Metadata["updateAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_UsesStableChannel_WhenReportedVersionHasNoPreReleaseTag()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.2.3");
        var marketplaceClient = new TestVsCodeExtensionMarketplaceClient
        {
            GetLatestVersionsAsyncCallback = _ => Task.FromResult(new VsCodeExtensionMarketplaceVersions(
                StableVersion: SemVersion.Parse("1.2.3", SemVersionStyles.Strict),
                // Ahead of the stable version, as the gallery requires. Comparing against this feed
                // would produce a spurious warning for a stable install.
                PreReleaseVersion: SemVersion.Parse("1.3.0-preview.1", SemVersionStyles.Strict)))
        };
        var check = CreateCheck(environment, home, marketplaceClient);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("1.2.3", result.Metadata!["latestVersion"]!.GetValue<string>());
        Assert.Equal("stable", result.Metadata["latestVersionChannel"]!.GetValue<string>());
        Assert.False(result.Metadata["updateAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public void Detect_UsesVersionReportedByTheRunningExtension()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion("1.16.0");

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("1.16.0", detection.ExtensionVersion);
    }

    [Fact]
    public void Detect_TrimsReportedVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = CreateVsCodeEnvironmentWithReportedVersion(" 1.16.0\n");

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.Equal("1.16.0", detection.ExtensionVersion);
    }

    [Fact]
    public void Detect_ReportsExtensionInstalled_WhenNothingIsOnDiskButTheExtensionReportedItself()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // Remote, portable, and --extensions-dir installs put the extension somewhere the CLI cannot
        // enumerate. The contributed version is authoritative precisely for those cases.
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName,
            [ReportedVersionVariable] = "1.16.0"
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("1.16.0", detection.ExtensionVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Detect_FallsBackToDisk_WhenReportedVersionIsBlank(string reportedVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        Directory.CreateDirectory(Path.Combine(extensions.FullName, "microsoft-aspire.aspire-vscode-1.2.3"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName,
            [ReportedVersionVariable] = reportedVersion
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("1.2.3", detection.ExtensionVersion);
        Assert.Equal(VsCodeExtensionVersionSource.Manifest, detection.VersionSource);
    }

    [Fact]
    public void Detect_FindsExtension_ViaVsCodeExtensionsOverride()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        Directory.CreateDirectory(Path.Combine(extensions.FullName, "microsoft-aspire.aspire-vscode-1.2.3"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("1.2.3", detection.ExtensionVersion);
        Assert.Equal(VsCodeExtensionVersionSource.Manifest, detection.VersionSource);
        Assert.Equal([extensions.FullName], detection.SearchedRoots);
    }

    [Fact]
    public void Detect_IgnoresDefaultRoots_WhenVsCodeExtensionsOverrideSet()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var overrideDirectory = workspace.CreateDirectory("override");
        // VSCODE_EXTENSIONS replaces the extension location outright, so an install under the home
        // default must not be reported when the override points somewhere else.
        Directory.CreateDirectory(Path.Combine(home.FullName, ".vscode", "extensions", "microsoft-aspire.aspire-vscode-1.2.3"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = overrideDirectory.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Theory]
    [InlineData(".vscode")]
    [InlineData(".vscode-insiders")]
    [InlineData(".vscode-oss")]
    [InlineData(".vscode-server")]
    [InlineData(".vscode-server-insiders")]
    [InlineData(".vscode-server-oss")]
    public void Detect_FindsExtension_ViaEachDefaultExtensionsRoot(string rootFolder)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // Exercise every default extensions root VsCodeInstallLayout composes: the desktop folders for
        // stable/Insiders/VSCodium and their remote/server counterparts.
        Directory.CreateDirectory(Path.Combine(home.FullName, rootFolder, "extensions", "microsoft-aspire.aspire-vscode-1.2.3"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode"
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("code-insiders")]
    public void Detect_DetectsVsCode_ViaPathFallback_WhenTermProgramNotVsCode(string launcherOnPath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // No TERM_PROGRAM, so detection falls back to probing the CLI launchers on PATH via the
        // injected resolver.
        var environment = new TestEnvironment(new Dictionary<string, string?>());
        string? Resolver(string command) => string.Equals(command, launcherOnPath, StringComparison.Ordinal) ? "/usr/bin/" + command : null;

        var detection = VsCodeExtensionCheck.Detect(environment, home, Resolver);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsVsCodeNotInstalled_WhenTermProgramAbsentAndNotOnPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>());

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.False(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_IgnoresReportedVersion_WhenVsCodeIsNotInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // A stale variable inherited from an unrelated parent process must not make the check claim
        // VS Code is present.
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [ReportedVersionVariable] = "1.16.0"
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.False(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
        Assert.Null(detection.ExtensionVersion);
    }

    [Fact]
    public void Detect_MatchesExtensionFolder_CaseInsensitively()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        Directory.CreateDirectory(Path.Combine(extensions.FullName, "Microsoft-Aspire.Aspire-VSCode-9.9.9"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsExtensionMissing_WhenOnlyUnrelatedExtensionsPresent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        Directory.CreateDirectory(Path.Combine(extensions.FullName, "ms-dotnettools.csharp-2.0.0"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsExtensionMissing_WhenFolderSharesPrefixWithDifferentId()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // A different extension whose id begins with ours. Without the digit boundary the prefix match
        // would incorrectly treat this as the Aspire extension.
        Directory.CreateDirectory(Path.Combine(extensions.FullName, "microsoft-aspire.aspire-vscode-extras-1.0.0"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_ReportsExtensionMissing_WhenExtensionsDirectoryDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // Point the override at a path that is never created so DirectoryContainsExtension hits the
        // Directory.Exists == false guard. VS Code being present must still yield a clean "missing"
        // result rather than throwing on the absent directory.
        var missingExtensionsDirectory = Path.Combine(home.FullName, "does-not-exist");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = missingExtensionsDirectory
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    private static TestEnvironment CreateVsCodeEnvironmentWithReportedVersion(string reportedVersion)
        => new(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [ReportedVersionVariable] = reportedVersion
        });

    // Points the extension root at a temporary directory so the scan never reads the real
    // ~/.vscode/extensions of the machine running the tests.
    private static TestEnvironment CreateVsCodeEnvironmentWithoutReportedVersion(DirectoryInfo extensions)
        => new(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

    private static void CreateInstalledExtension(DirectoryInfo extensionsRoot, string version)
        => CreateInstalledExtension(extensionsRoot, version, version);

    // Lays out an extension the way VS Code extracts a VSIX: a "<publisher>.<name>-<version>" folder
    // containing the manifest the extension host reads.
    private static void CreateInstalledExtension(
        DirectoryInfo extensionsRoot,
        string folderVersion,
        string? manifestVersion)
    {
        var directory = Directory.CreateDirectory(
            Path.Combine(extensionsRoot.FullName, $"microsoft-aspire.aspire-vscode-{folderVersion}"));

        if (manifestVersion is not null)
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "package.json"),
                $$"""{ "name": "aspire-vscode", "publisher": "microsoft-aspire", "version": "{{manifestVersion}}" }""");
        }
    }

    private static TestVsCodeExtensionMarketplaceClient CreateUnusedMarketplaceClient()
        => new()
        {
            StableVersionCallback = _ => throw new InvalidOperationException("Marketplace must not be queried.")
        };

    private static VsCodeExtensionCheck CreateCheck(
        TestEnvironment environment,
        DirectoryInfo home,
        TestVsCodeExtensionMarketplaceClient marketplaceClient)
        => new(
            environment,
            TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home),
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);
}
