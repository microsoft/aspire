// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// End-to-end regression guard for the silent-PR-demotion and
/// package-manager binary-clobber bugs on <c>aspire update --self</c>.
/// Pre-fix: an in-process binary swap ran unconditionally for every
/// non-dotnet-tool route, overwriting WinGet / Homebrew / PR-pinned
/// binaries with the latest stable archive. Post-fix: each non-script
/// route gets refused with the installer-appropriate command and the
/// binary is left untouched.
/// </summary>
public class UpdateCommandRouteRegressionTests(ITestOutputHelper outputHelper)
{
    // Each row encodes (sidecar source, identityChannel for PR substitution,
    // expected refusal command). Script and Unknown stay in-process by design,
    // so they're excluded from this regression net.
    [Theory]
    [InlineData("pr", "pr-16817", "get-aspire-cli-pr.sh 16817    # or: get-aspire-cli-pr.ps1 -PRNumber 16817")]
    [InlineData("winget", "stable", "winget upgrade Microsoft.Aspire")]
    [InlineData("brew", "stable", "brew upgrade --cask aspire")]
    [InlineData("localhive", "local", "./localhive.sh   # re-run from your Aspire checkout")]
    public async Task SelfUpdate_OnGatedRoute_RefusesWithRouteAppropriateCommand(
        string sidecarSource,
        string identityChannel,
        string expectedCommand)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var selfInfo = new InstallationInfo
        {
            Path = "/test/aspire",
            CanonicalPath = "/test/aspire",
            Route = sidecarSource,
            Channel = identityChannel,
            Status = InstallationInfoStatus.Ok,
        };

        TestInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };

            // Force the running CLI's identity channel so the PR-route
            // substitution exercises the parsed PR number path.
            options.CliExecutionContextFactory = _ =>
            {
                var root = workspace.WorkspaceRoot;
                var hivesDirectory = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "hives"));
                var cacheDirectory = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "cache"));
                var logsDirectory = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "logs"));
                var logFilePath = Path.Combine(logsDirectory.FullName, "test.log");
                return new CliExecutionContext(
                    root,
                    hivesDirectory,
                    cacheDirectory,
                    new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")),
                    logsDirectory,
                    logFilePath,
                    identityChannel: identityChannel);
            };
        });

        // Replace the real InstallationDiscovery with a fake surfacing the
        // route under test. Last registration wins.
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(selfInfo));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var parsed = command.Parse("update --self");
        var exitCode = await parsed.InvokeAsync().DefaultTimeout();

        Assert.NotNull(interactionService);
        // Exit 0 by design — the CLI succeeded in telling the user what to
        // do (matches the existing dotnet-tool refusal contract).
        Assert.Equal(0, exitCode);

        // The expected command must appear verbatim in the displayed plain
        // text — this is the signal a user / CI script would actually
        // observe in stdout.
        Assert.Contains(
            interactionService!.DisplayedPlainText,
            line => line.Contains(expectedCommand, StringComparison.Ordinal));
    }

    /// <summary>
    /// Legacy compatibility check: when the running binary has no sidecar
    /// (e.g., a dotnet-tool install that predates the sidecar contract)
    /// BUT path-shape inspection identifies the binary as a dotnet tool,
    /// the route is fixed up to <see cref="InstallSource.DotnetTool"/> and
    /// refused with the dotnet-tool command rather than falling through to
    /// the in-process update flow (which would corrupt the
    /// package-manager-owned binary).
    /// </summary>
    [Fact]
    public async Task SelfUpdate_NoSidecar_LegacyDotnetToolPathShape_RefusesWithDotnetToolHint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting(
            "/home/test/.dotnet/tools/.store/aspire.cli/9.4.0/aspire.cli.linux-x64/9.4.0/tools/net10.0/linux-x64/aspire");

        // Discovery returns no route (no sidecar on disk). The fallback in
        // ResolveRunningInstall must then consult DotNetToolDetection via
        // the no-arg overload, which honors UseProcessPathForTesting.
        var selfInfo = new InstallationInfo
        {
            Path = "/test/aspire",
            CanonicalPath = "/test/aspire",
            Route = null,
            Status = InstallationInfoStatus.Ok,
        };

        TestInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };
        });
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(selfInfo));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse("update --self").InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(interactionService);
        // The refusal must surface the global dotnet-tool update command —
        // the binary IS under ~/.dotnet/tools/.store/ so DotNetToolDetection
        // classifies it as a global-tool install.
        Assert.Contains(
            interactionService!.DisplayedPlainText,
            line => line.Contains("dotnet tool update -g Aspire.Cli", StringComparison.Ordinal));
    }

    /// <summary>
    /// Pre-sidecar script install compat: when the running binary has no
    /// sidecar AND the process path doesn't match any dotnet-tool layout,
    /// the resolver yields <see cref="InstallSource.Unknown"/> and
    /// <see cref="SelfUpdateRouter.GetAction"/> routes Unknown to
    /// <see cref="SelfUpdateAction.InProcess"/>. <c>--self</c> must then
    /// reach the in-process flow rather than printing a refusal. We assert
    /// the SUT does NOT print any of the refusal-message prefixes
    /// (verifying it didn't take the gated branch) instead of trying to
    /// drive a full network download.
    /// </summary>
    [Fact]
    public async Task SelfUpdate_NoSidecar_NotDotnetTool_FallsThroughToInProcessFlow()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting("/tmp/random/aspire");

        var selfInfo = new InstallationInfo
        {
            Path = "/tmp/random/aspire",
            CanonicalPath = "/tmp/random/aspire",
            Route = null,
            Status = InstallationInfoStatus.Ok,
        };

        TestInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };
        });
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(selfInfo));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        // Invoke --self; we don't expect this to complete the actual update
        // (no real network in the test process), but we do expect it to NOT
        // emit any of the route-refusal messages. Any non-refusal outcome
        // (timeout / failure / success / cancellation) is acceptable here;
        // what matters is that the gated branch was not taken.
        try
        {
            await command.Parse("update --self --yes --channel stable").InvokeAsync().DefaultTimeout();
        }
        catch
        {
            // The in-process flow may throw for any number of network /
            // download / signature reasons; the test doesn't depend on
            // those succeeding.
        }

        Assert.NotNull(interactionService);
        // None of the route-specific refusal messages must appear: those
        // are the signal that the gated branch was taken.
        var allOutput = string.Join("\n", interactionService!.DisplayedPlainText);
        Assert.DoesNotContain("get-aspire-cli-pr.sh", allOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("winget upgrade", allOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("brew upgrade", allOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("./localhive.sh", allOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet tool update", allOutput, StringComparison.Ordinal);
    }
}
