// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class RootCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RootCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RootCommandWithNoLogoArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--nologo --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task NoLogoEnvironmentVariable_ParsedCorrectly(string? value, bool expectedNoLogo)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ConfigurationCallback = config =>
            {
                config[CliConfigNames.NoLogo] = value;
            };
        });
        using var provider = services.BuildServiceProvider();

        var configuration = provider.GetRequiredService<IConfiguration>();
        var isNoLogo = configuration.GetBool(CliConfigNames.NoLogo, defaultValue: false);

        Assert.Equal(expectedNoLogo, isNoLogo);

        // Also verify command still works with the configuration
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--help");
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task FirstTimeUseNotice_BannerDisplayedWhenSentinelDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, []);

        Assert.True(bannerService.WasBannerDisplayed);
        Assert.True(sentinel.WasCreated);
    }

    [Fact]
    public async Task FirstTimeUseNotice_BannerNotDisplayedWhenSentinelExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = true };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, []);

        Assert.False(bannerService.WasBannerDisplayed);
        Assert.False(sentinel.WasCreated);
    }

    [Fact]
    public async Task FirstTimeUseNotice_BannerNotDisplayedWithNoLogoArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [CommonOptionNames.NoLogo]);

        Assert.False(bannerService.WasBannerDisplayed);
        Assert.True(sentinel.WasCreated);
    }

    [Fact]
    public async Task FirstTimeUseNotice_BannerNotDisplayedWithNoLogoEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
            options.ConfigurationCallback = config =>
            {
                config[CliConfigNames.NoLogo] = "1";
            };
        });
        using var provider = services.BuildServiceProvider();

        var configuration = provider.GetRequiredService<IConfiguration>();
        var noLogo = configuration.GetBool(CliConfigNames.NoLogo, defaultValue: false);

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, []);

        Assert.False(bannerService.WasBannerDisplayed);
        Assert.True(sentinel.WasCreated);
    }

    [Fact]
    public async Task Banner_DisplayedWhenExplicitlyRequested()
    {
        // When --banner is passed, banner should show even if not first run
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = true }; // Not first run
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [CommonOptionNames.Banner]);

        Assert.True(bannerService.WasBannerDisplayed);
        // Telemetry notice should NOT be shown since it's not first run
        var errorOutput = errorWriter.ToString();
        Assert.DoesNotContain("Telemetry", errorOutput);
    }

    [Fact]
    public async Task Banner_CanBeInvokedMultipleTimes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.BannerServiceFactory = _ => bannerService;
        });
        using var provider = services.BuildServiceProvider();

        // Invoke multiple times (simulating multiple --banner calls)
        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [CommonOptionNames.Banner]);
        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [CommonOptionNames.Banner]);
        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [CommonOptionNames.Banner]);

        Assert.Equal(3, bannerService.DisplayCount);
    }

    [Fact]
    public void BannerOption_HasCorrectDescription()
    {
        Assert.Equal("--banner", RootCommand.BannerOption.Name);
        Assert.NotNull(RootCommand.BannerOption.Description);
        Assert.NotEmpty(RootCommand.BannerOption.Description);
    }

    [Fact]
    public async Task Banner_DisplayedOnFirstRunAndExplicitRequest()
    {
        // When it's a first run AND user explicitly requests --banner,
        // the banner should be shown (only once via the explicit request logic)
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [CommonOptionNames.Banner]);

        Assert.True(bannerService.WasBannerDisplayed);
        Assert.True(sentinel.WasCreated);
        // Telemetry notice should be shown on first run
        var errorOutput = errorWriter.ToString();
        Assert.Contains("Telemetry", errorOutput);
    }

    [Fact]
    public async Task Banner_TelemetryNoticeShownOnFirstRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, []);

        var errorOutput = errorWriter.ToString();
        Assert.Contains("Telemetry", errorOutput);
    }

    [Fact]
    public async Task Banner_TelemetryNoticeNotShownOnSubsequentRuns()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var errorWriter = new StringWriter();
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = true }; // Not first run
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ErrorTextWriter = errorWriter;
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, []);

        var errorOutput = errorWriter.ToString();
        Assert.DoesNotContain("Telemetry", errorOutput);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public async Task InformationalFlag_SuppressesBannerAndDoesNotCreateSentinel(string flag)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [flag]);

        // Informational flags set noLogo, which suppresses banner and telemetry notice
        Assert.False(bannerService.WasBannerDisplayed);
        // Sentinel should NOT be created for informational commands
        Assert.False(sentinel.WasCreated);
    }

    [Fact]
    public async Task InformationalFlag_DoesNotCreateSentinel_OnSubsequentFirstRun()
    {
        // Verifies that running --version on first run doesn't mark first-run as complete,
        // so a subsequent normal invocation still shows the first-run experience.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        // First invocation with --version: should not create sentinel
        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, ["--version"]);
        Assert.False(sentinel.WasCreated);

        // Second invocation without informational flag: should create sentinel and show banner
        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, []);
        Assert.True(bannerService.WasBannerDisplayed);
        Assert.True(sentinel.WasCreated);
    }

    [Fact]
    public async Task FirstTimeUseNotice_BannerNotDisplayedInNonInteractiveEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = false };
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, []);

        Assert.False(bannerService.WasBannerDisplayed);
        Assert.True(sentinel.WasCreated);
    }

    [Fact]
    public async Task Banner_DisplayedWithExplicitBannerFlag_InNonInteractiveEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sentinel = new TestFirstTimeUseNoticeSentinel { SentinelExists = true }; // Not first run
        var bannerService = new TestBannerService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.FirstTimeUseNoticeSentinelFactory = _ => sentinel;
            options.BannerServiceFactory = _ => bannerService;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        await Program.DisplayFirstTimeUseNoticeIfNeededAsync(provider, [CommonOptionNames.Banner]);

        Assert.True(bannerService.WasBannerDisplayed);
    }

    [Fact]
    public void SetupCommand_NotAvailable_WhenBundleIsNotAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var hasSetupCommand = command.Subcommands.Any(cmd => cmd.Name == "setup");

        Assert.False(hasSetupCommand);
    }

    [Fact]
    public void SetupCommand_Available_WhenBundleIsAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.BundleServiceFactory = _ => new TestBundleService(isBundle: true);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var hasSetupCommand = command.Subcommands.Any(cmd => cmd.Name == "setup");

        Assert.True(hasSetupCommand);
    }

    [Fact]
    public void AllVisibleCommands_HaveHelpGroup()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.BundleServiceFactory = _ => new TestBundleService(isBundle: true);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();

        // Either the subcommand isn't a BaseCommand, or it hasn't specified a HelpGroup.
        var missingGroup = command.Subcommands
            .Where(sub => !sub.Hidden)
            .Where(cmd => cmd is not BaseCommand baseCmd || baseCmd.HelpGroup is HelpGroup.None)
            .Select(cmd => cmd.Name)
            .ToList();

        Assert.True(missingGroup.Count == 0,
            $"The following visible commands are missing a HelpGroup: {string.Join(", ", missingGroup)}. " +
            "Add 'internal override HelpGroup HelpGroup => HelpGroup.XXX;' to each command class.");
    }

    [Fact]
    public void GroupedHelp_ContainsAllVisibleCommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.BundleServiceFactory = _ => new TestBundleService(isBundle: true);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var helpWriter = new StringWriter();
        GroupedHelpWriter.WriteHelp(command, helpWriter, 120);

        var helpOutput = helpWriter.ToString();
        var visibleCommands = command.Subcommands.Where(sub => !sub.Hidden).ToList();

        foreach (var sub in visibleCommands)
        {
            Assert.Contains(sub.Name, helpOutput);
        }
    }
}
