// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Commands;

public class VsCodeExtensionCheckTests(ITestOutputHelper outputHelper)
{
    private const string MicrosoftMarketplaceExtensionSource = "microsoft-marketplace";
    private const string OtherExtensionSource = "other";

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenReportedVersionIsOutOfDate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(
            new VsCodeExtensionMarketplaceVersions(
                SemVersion.Parse("1.3.0", SemVersionStyles.Strict),
                SemVersion.Parse("1.4.0", SemVersionStyles.Strict)));
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionOutOfDateFix, result.Fix);
        Assert.Equal("1.2.3", result.Metadata!["extensionVersion"]!.GetValue<string>());
        Assert.Equal("stable", result.Metadata["extensionChannel"]!.GetValue<string>());
        Assert.Equal(MicrosoftMarketplaceExtensionSource, result.Metadata["extensionSource"]!.GetValue<string>());
        Assert.Equal("1.3.0", result.Metadata["latestVersion"]!.GetValue<string>());
        Assert.True(result.Metadata["updateAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_UsesMatchingPreReleaseMarketplaceVersion_WhenStableIsNewer()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.3.0",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "prerelease",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(
            new VsCodeExtensionMarketplaceVersions(
                SemVersion.Parse("9.0.0", SemVersionStyles.Strict),
                SemVersion.Parse("1.4.0", SemVersionStyles.Strict)));
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("prerelease", result.Metadata!["latestVersionChannel"]!.GetValue<string>());
        Assert.Equal("1.4.0", result.Metadata["latestVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_UsesPrereleaseChannelVocabulary()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.3.0",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "prerelease",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(
            new VsCodeExtensionMarketplaceVersions(
                SemVersion.Parse("1.2.0", SemVersionStyles.Strict),
                SemVersion.Parse("1.4.0", SemVersionStyles.Strict)));
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("prerelease", result.Metadata!["extensionChannel"]!.GetValue<string>());
        Assert.Equal("prerelease", result.Metadata["latestVersionChannel"]!.GetValue<string>());
        Assert.Equal("1.4.0", result.Metadata["latestVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenReportedVersionIsCurrent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.3.0",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(
            new VsCodeExtensionMarketplaceVersions(
                SemVersion.Parse("1.3.0", SemVersionStyles.Strict),
                SemVersion.Parse("1.4.0", SemVersionStyles.Strict)));
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("1.3.0", result.Metadata!["latestVersion"]!.GetValue<string>());
        Assert.False(result.Metadata["updateAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenMarketplaceLookupFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new TestMarketplaceClient(new HttpRequestException("unavailable")),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails, result.Details);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
    }

    [Fact]
    public async Task CheckAsync_DistinguishesMissingStableVersionFromMarketplaceFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "results": [{
                    "extensions": [{
                      "versions": [{
                        "version": "1.4.0",
                        "properties": [{
                          "key": "Microsoft.VisualStudio.Code.PreRelease",
                          "value": "true"
                        }]
                      }]
                    }]
                  }]
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
        using var handler = new MockHttpMessageHandler(response);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler)),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(
            "The VS Code Marketplace did not report an Aspire extension version for the detected channel.",
            result.Details);
        Assert.False(result.Metadata!["latestVersionKnown"]!.GetValue<bool>());
        Assert.Null(result.Metadata["latestVersionError"]);
    }

    [Fact]
    public async Task CheckAsync_DoesNotFallBackToStable_WhenPreReleaseMarketplaceVersionIsMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "prerelease",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(
            new VsCodeExtensionMarketplaceVersions(
                SemVersion.Parse("9.0.0", SemVersionStyles.Strict),
                null));
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionLatestVersionNotFoundDetails, result.Details);
        Assert.False(result.Metadata!["latestVersionKnown"]!.GetValue<bool>());
        Assert.Null(result.Metadata["latestVersion"]);
        Assert.Null(result.Metadata["latestVersionChannel"]);
        Assert.Null(result.Metadata["latestVersionError"]);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{ "results": {} }""")]
    [InlineData("""{ "results": [{}] }""")]
    [InlineData("""{ "results": [{ "extensions": {} }] }""")]
    [InlineData("""{ "results": [{ "extensions": [{ "versions": {} }] }] }""")]
    public async Task CheckAsync_ReturnsWarning_WhenMarketplacePayloadHasUnexpectedShape(string payload)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var handler = new MockHttpMessageHandler(response);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler)),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails, result.Details);
        Assert.False(result.Metadata!["latestVersionKnown"]!.GetValue<bool>());
        Assert.Equal("unavailable", result.Metadata["latestVersionError"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenReportedVersionIsUnparseable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "not-a-version",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new TestMarketplaceClient(new InvalidOperationException("Marketplace must not be queried.")),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionVersionUnknownMessage, result.Message);
        Assert.False(result.Metadata!["extensionVersionKnown"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckAsync_PropagatesCallerCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(new OperationCanceledException());
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => check.CheckAsync(cancellation.Token));
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenHttpClientTimesOut()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = MicrosoftMarketplaceExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new TestMarketplaceClient(new TaskCanceledException("The request timed out.")),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails, result.Details);
    }

    [Fact]
    public async Task CheckAsync_SkipsMarketplace_WhenReportedSourceIsUnknown()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable"
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(new InvalidOperationException("Marketplace must not be queried."));
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Equal(
            "The active editor is not known to use the Microsoft VS Code Marketplace, so the update check was skipped.",
            result.Details);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
        Assert.Equal("unknown", result.Metadata!["extensionSource"]!.GetValue<string>());
        Assert.False(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
        Assert.Equal(0, marketplaceClient.RequestCount);
    }

    [Fact]
    public async Task CheckAsync_SkipsMarketplace_WhenReportedSourceIsNonMicrosoft()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable",
            [VsCodeExtensionCheck.ExtensionSourceEnvironmentVariable] = OtherExtensionSource
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var marketplaceClient = new TestMarketplaceClient(new InvalidOperationException("Marketplace must not be queried."));
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            marketplaceClient,
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Equal(
            "The active editor is not known to use the Microsoft VS Code Marketplace, so the update check was skipped.",
            result.Details);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
        Assert.Equal(OtherExtensionSource, result.Metadata!["extensionSource"]!.GetValue<string>());
        Assert.False(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
        Assert.Equal(0, marketplaceClient.RequestCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsEmpty_WhenVsCodeNotInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // No TERM_PROGRAM and nothing resolvable on PATH, so real detection reports VS Code absent.
        var environment = TestEnvironment.CreateLinux();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new TestMarketplaceClient(new InvalidOperationException("Marketplace must not be queried.")),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenExtensionMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // VS Code is present (TERM_PROGRAM) but the override extensions directory is empty.
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new TestMarketplaceClient(new InvalidOperationException("Marketplace must not be queried.")),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

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
    public async Task CheckAsync_ReturnsWarning_WhenDiskFallbackChannelIsUnknown()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        // VS Code is present and the Aspire extension is installed in the override extensions directory.
        Directory.CreateDirectory(Path.Combine(extensions.FullName, "microsoft-aspire.aspire-vscode-1.2.3"));
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(home, homeDirectory: home);
        var check = new VsCodeExtensionCheck(
            environment,
            executionContext,
            new TestMarketplaceClient(new InvalidOperationException("Marketplace must not be queried.")),
            NullLogger<VsCodeExtensionCheck>.Instance,
            _ => null);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckCategories.DevelopmentTools, result.Category);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.VsCodeExtensionInstalledMessage, result.Message);
        Assert.Null(result.Fix);
        Assert.Null(result.Link);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata["vsCodeInstalled"]!.GetValue<bool>());
        Assert.True(result.Metadata["extensionInstalled"]!.GetValue<bool>());
        Assert.Equal(VsCodeExtensionCheck.ExtensionId, result.Metadata["extensionId"]!.GetValue<string>());
        Assert.Equal("unknown", result.Metadata["extensionChannel"]!.GetValue<string>());
        Assert.False(result.Metadata["latestVersionKnown"]!.GetValue<bool>());
        Assert.Null(result.Metadata["latestVersionError"]);
        Assert.Equal(
            "The installed extension channel could not be determined, so the update check was skipped.",
            result.Details);
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

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("Unknown", detection.ReleaseChannel.ToString());
    }

    [Fact]
    public void Detect_PrefersManifestVersionOverExtensionDirectoryVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        var extension = Directory.CreateDirectory(Path.Combine(
            extensions.FullName,
            "microsoft-aspire.aspire-vscode-1.2.3"));
        File.WriteAllText(
            Path.Combine(extension.FullName, "package.json"),
            """{ "version": "9.8.7" }""");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("9.8.7", detection.ExtensionVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public void Detect_FallsBackToExtensionDirectoryVersion_WhenManifestIsMissingOrMalformed(string? manifest)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        var extension = Directory.CreateDirectory(Path.Combine(
            extensions.FullName,
            "microsoft-aspire.aspire-vscode-2.3.4"));
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(extension.FullName, "package.json"), manifest);
        }

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("2.3.4", detection.ExtensionVersion);
    }

    [Fact]
    public void Detect_SelectsHighestParsedManifestVersionAcrossInstalledExtensions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var extensions = workspace.CreateDirectory("extensions");
        var lowerManifest = Directory.CreateDirectory(Path.Combine(
            extensions.FullName,
            "microsoft-aspire.aspire-vscode-9.0.0"));
        File.WriteAllText(
            Path.Combine(lowerManifest.FullName, "package.json"),
            """{ "version": "1.0.0" }""");
        var higherManifest = Directory.CreateDirectory(Path.Combine(
            extensions.FullName,
            "microsoft-aspire.aspire-vscode-2.0.0"));
        File.WriteAllText(
            Path.Combine(higherManifest.FullName, "package.json"),
            """{ "version": "3.0.0" }""");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = extensions.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("3.0.0", detection.ExtensionVersion);
    }

    [Theory]
    [InlineData(".vscode")]
    [InlineData(".vscode-insiders")]
    [InlineData(".vscode-server")]
    [InlineData(".vscode-server-insiders")]
    public void Detect_FindsExtension_ViaEachDefaultExtensionsRoot(string rootFolder)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        // Exercise each default extensions root that GetExtensionDirectories composes (desktop
        // stable/Insiders and remote/server) rather than the VSCODE_EXTENSIONS override.
        Directory.CreateDirectory(Path.Combine(home.FullName, rootFolder, "extensions", "microsoft-aspire.aspire-vscode-1.2.3"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode"
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_IgnoresDefaultRoots_WhenVsCodeExtensionsOverrideSet()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var overrideDirectory = workspace.CreateDirectory("override");
        // The extension is present in the default desktop root but absent from the override directory.
        // VSCODE_EXTENSIONS makes VS Code load only the override, so detection must report it missing.
        Directory.CreateDirectory(Path.Combine(home.FullName, ".vscode", "extensions", "microsoft-aspire.aspire-vscode-1.2.3"));

        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["VSCODE_EXTENSIONS"] = overrideDirectory.FullName
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home);

        Assert.True(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
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
        var environment = TestEnvironment.CreateLinux();
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
        var environment = TestEnvironment.CreateLinux();

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.False(detection.VsCodeInstalled);
        Assert.False(detection.ExtensionInstalled);
    }

    [Fact]
    public void Detect_UsesReportedExtensionVersion_WhenVsCodeCannotBeProbed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            [VsCodeExtensionCheck.ExtensionVersionEnvironmentVariable] = "1.2.3",
            [VsCodeExtensionCheck.ExtensionChannelEnvironmentVariable] = "stable"
        });

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.VsCodeInstalled);
        Assert.True(detection.ExtensionInstalled);
        Assert.Equal("1.2.3", detection.ExtensionVersion);
        Assert.Equal(VsCodeExtensionReleaseChannel.Stable, detection.ReleaseChannel);
    }

    [Theory]
    [InlineData("Visual Studio Code.app")]
    [InlineData("Visual Studio Code - Insiders.app")]
    public void Detect_DetectsMacOSApplication_WhenShellCommandIsNotInstalled(string applicationName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        Directory.CreateDirectory(Path.Combine(home.FullName, "Applications", applicationName));
        var environment = TestEnvironment.CreateMacOS();

        var detection = VsCodeExtensionCheck.Detect(environment, home, _ => null);

        Assert.True(detection.VsCodeInstalled);
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

    private sealed class TestMarketplaceClient : IVsCodeExtensionMarketplaceClient
    {
        private readonly VsCodeExtensionMarketplaceVersions? _versions;
        private readonly Exception? _exception;

        public int RequestCount { get; private set; }

        public TestMarketplaceClient(VsCodeExtensionMarketplaceVersions versions)
        {
            _versions = versions;
        }

        public TestMarketplaceClient(Exception exception)
        {
            _exception = exception;
        }

        public Task<VsCodeExtensionMarketplaceVersions> GetLatestVersionsAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            return _exception is null
                ? Task.FromResult(_versions!)
                : Task.FromException<VsCodeExtensionMarketplaceVersions>(_exception);
        }
    }
}
