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
/// End-to-end regression guard for the silent route-demotion and
/// package-manager binary-clobber bugs on <c>aspire update --self</c>.
/// </summary>
public class UpdateCommandRouteRegressionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("script", "stable", false, true, null)]
    [InlineData("script", "stable", true, true, null)]
    [InlineData("brew", "stable", false, false, "brew upgrade --cask aspire")]
    [InlineData("brew", "stable", true, true, null)]
    [InlineData("winget", "stable", false, false, "winget upgrade Microsoft.Aspire")]
    [InlineData("winget", "stable", true, true, null)]
    [InlineData("dotnet-tool", "stable", false, false, "dotnet tool update -g Aspire.Cli")]
    [InlineData("dotnet-tool", "stable", true, true, null)]
    [InlineData("pr", "pr-16817", false, false, "get-aspire-cli-pr.sh 16817    # or: get-aspire-cli-pr.ps1 -PRNumber 16817")]
    [InlineData("pr", "pr-16817", true, true, null)]
    [InlineData("localhive", "local", false, false, "localhive.sh")]
    [InlineData("localhive", "local", true, true, null)]
    [InlineData(null, "stable", false, false, "Aspire couldn't determine how this CLI was installed")]
    [InlineData(null, "stable", true, true, null)]
    public async Task SelfUpdate_RouteGate_RefusesUnlessScriptOrForced(
        string? sidecarSource,
        string identityChannel,
        bool force,
        bool expectInProcess,
        string? expectedOutput)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting(
            Path.Combine(workspace.WorkspaceRoot.FullName, "bin", "aspire"));

        var selfInfo = new InstallationInfo
        {
            Path = Path.Combine(workspace.WorkspaceRoot.FullName, "bin", "aspire"),
            CanonicalPath = Path.Combine(workspace.WorkspaceRoot.FullName, "bin", "aspire"),
            Route = sidecarSource,
            Channel = identityChannel,
            Status = sidecarSource is null ? InstallationInfoStatus.NotProbed : InstallationInfoStatus.Ok,
        };

        var downloadAttempted = false;
        TestInteractionService? interactionService = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ =>
            {
                interactionService = new TestInteractionService();
                return interactionService;
            };

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
                    new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "sdks")),
                    logsDirectory,
                    logFilePath,
                    identityChannel: identityChannel);
            };

            options.CliDownloaderFactory = sp =>
            {
                var executionContext = sp.GetRequiredService<CliExecutionContext>();
                return new TestCliDownloader(new DirectoryInfo(Path.Combine(executionContext.WorkingDirectory.FullName, "tmp")))
                {
                    DownloadLatestCliAsyncCallback = (_, _) =>
                    {
                        downloadAttempted = true;
                        throw new InvalidOperationException("download attempted");
                    }
                };
            };
        });

        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(selfInfo));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var args = force ? "update --self --force --yes --channel stable" : "update --self --yes --channel stable";
        var exitCode = await command.Parse(args).InvokeAsync().DefaultTimeout();

        Assert.NotNull(interactionService);
        Assert.Equal(expectInProcess, downloadAttempted);

        var allOutput = string.Join("\n", interactionService!.DisplayedPlainText.Concat(interactionService.DisplayedMessages.Select(m => m.Message)));
        if (expectedOutput is not null)
        {
            Assert.Equal(0, exitCode);
            Assert.Contains(expectedOutput, allOutput, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEqual(0, exitCode);
            Assert.DoesNotContain("winget upgrade", allOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("brew upgrade", allOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet tool update", allOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("get-aspire-cli-pr", allOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("localhive.sh", allOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("couldn't determine how this CLI was installed", allOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Legacy compatibility check: when the running binary has no sidecar
    /// (e.g., a dotnet-tool install that predates the sidecar contract)
    /// BUT path-shape inspection identifies the binary as a dotnet tool,
    /// the route is fixed up to <see cref="InstallSource.DotnetTool"/> and
    /// refused with the dotnet-tool command rather than falling through to
    /// the in-process update flow.
    /// </summary>
    [Fact]
    public async Task SelfUpdate_NoSidecar_LegacyDotnetToolPathShape_RefusesWithDotnetToolHint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting(
            "/home/test/.dotnet/tools/.store/aspire.cli/9.4.0/aspire.cli.linux-x64/9.4.0/tools/net10.0/linux-x64/aspire");

        var selfInfo = new InstallationInfo
        {
            Path = "/home/test/.dotnet/tools/.store/aspire.cli/9.4.0/aspire.cli.linux-x64/9.4.0/tools/net10.0/linux-x64/aspire",
            CanonicalPath = "/home/test/.dotnet/tools/.store/aspire.cli/9.4.0/aspire.cli.linux-x64/9.4.0/tools/net10.0/linux-x64/aspire",
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
        Assert.Contains(
            interactionService!.DisplayedPlainText,
            line => line.Contains("dotnet tool update -g Aspire.Cli", StringComparison.Ordinal));
    }
}
