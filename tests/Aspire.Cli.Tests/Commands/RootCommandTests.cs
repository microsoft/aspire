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
    public async Task RootCommandVersionOption_PrintsIdentityVersion_WhenIdentityOverridden()
    {
        // Emulates `ASPIRE_CLI_VERSION=13.4.2` so `--version` must report the identity version,
        // not the assembly's build-time stamp.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot,
                identityVersion: "13.4.2",
                identityOverridden: true);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--version");

        var output = new StringWriter();
        var exitCode = await result.InvokeAsync(new System.CommandLine.InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal("13.4.2", output.ToString().Trim());
    }

    [Fact]
    public async Task RootCommandVersionShortAlias_PrintsIdentityVersion_WhenIdentityOverridden()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot,
                identityVersion: "13.4.2",
                identityOverridden: true);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("-v");

        var output = new StringWriter();
        var exitCode = await result.InvokeAsync(new System.CommandLine.InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Equal("13.4.2", output.ToString().Trim());
    }

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

    [Fact]
    public async Task FeedbackCommand_Idea_PrintsIssueTemplateUrl()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("feedback idea --title \"My idea\" --body \"Make Aspire better\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        var message = Assert.Single(interactionService.DisplayedMessages).Message;
        Assert.Contains("https://github.com/microsoft/aspire/issues/new?", message, StringComparison.Ordinal);
        Assert.Contains("template=20_feature-request.yml", message, StringComparison.Ordinal);
        Assert.Contains("title=My%20idea", message, StringComparison.Ordinal);
        Assert.Contains("solution=Make%20Aspire%20better", message, StringComparison.Ordinal);
        Assert.Contains("additional-context=", message, StringComparison.Ordinal);
        Assert.Contains("Posted%20from%3A%20CLI", message, StringComparison.Ordinal);
        Assert.Empty(interactionService.ShownStatuses);
    }

    [Fact]
    public async Task FeedbackCommand_PromptsForKindAndRequiredValues()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService
        {
            PromptForSelectionCallback = (_, choices, formatter, _) => choices.Cast<object>().Single(choice => formatter(choice) == "Suggest an idea")
        };
        interactionService.SetupStringPromptResponse("A prompted idea");
        interactionService.SetupStringPromptResponse("Prompted idea body");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("feedback");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Collection(
            interactionService.StringPromptCalls,
            titlePrompt => Assert.Equal("Issue title", titlePrompt.PromptText),
            bodyPrompt => Assert.Equal("Describe the idea", bodyPrompt.PromptText));

        var message = Assert.Single(interactionService.DisplayedMessages).Message;
        Assert.Contains("template=20_feature-request.yml", message, StringComparison.Ordinal);
        Assert.Contains("title=A%20prompted%20idea", message, StringComparison.Ordinal);
        Assert.Contains("solution=Prompted%20idea%20body", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeedbackCommand_General_PrintsBlankIssueUrlWithoutTemplateParameter()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("feedback general --title \"General feedback\" --body \"Details\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        var message = Assert.Single(interactionService.DisplayedMessages).Message;
        Assert.Contains("https://github.com/microsoft/aspire/issues/new?", message, StringComparison.Ordinal);
        Assert.Contains("title=General%20feedback", message, StringComparison.Ordinal);
        Assert.Contains("body=Details", message, StringComparison.Ordinal);
        Assert.DoesNotContain("template=", message, StringComparison.Ordinal);
        Assert.DoesNotContain("blank.yml", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeedbackCommand_NonInteractiveWithoutKind_PrintsGenericChooserUrl()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("feedback --title \"My feedback\" --body \"Details\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Empty(interactionService.StringPromptCalls);
        var message = Assert.Single(interactionService.DisplayedMessages).Message;
        Assert.Equal("Open this link to send feedback: https://github.com/microsoft/aspire/issues/new/choose", message);
    }

    [Fact]
    public async Task CaptureProfileOptions_AreHiddenFromHelp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        var help = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.DoesNotContain("--capture-profile", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--capture-profile-output", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--capture-profile-delay", help, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureProfileOptions_ParseOnSubcommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --capture-profile --capture-profile-output profile.zip --capture-profile-delay 1");

        Assert.Empty(result.Errors);
        Assert.True(result.GetValue(RootCommand.CaptureProfileOption));
        Assert.Equal("profile.zip", result.GetValue(RootCommand.CaptureProfileOutputOption)?.Name);
        Assert.Equal(1, result.GetValue(RootCommand.CaptureProfileDelayOption));
        Assert.DoesNotContain("--capture-profile", result.UnmatchedTokens);
    }

    [Fact]
    public void StartDebugSessionOption_IsOnlyAddedInExtensionContext()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var normalServices = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var normalProvider = normalServices.BuildServiceProvider();

        var normalCommand = normalProvider.GetRequiredService<RootCommand>();
        Assert.DoesNotContain(normalCommand.Options, option => ReferenceEquals(option, RootCommand.StartDebugSessionOption));

        var extensionServices = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp);
        });
        using var extensionProvider = extensionServices.BuildServiceProvider();

        var extensionCommand = extensionProvider.GetRequiredService<RootCommand>();
        Assert.Contains(extensionCommand.Options, option => ReferenceEquals(option, RootCommand.StartDebugSessionOption));
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
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
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
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
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
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
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
    public async Task Banner_NotDisplayedWithExplicitBannerFlag_InNonInteractiveEnvironment()
    {
        // Even when --banner is explicitly passed, the banner should NOT display in a
        // non-interactive environment because Spectre.Console's Live display requires
        // valid console handles (e.g., stdout must not be redirected).
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

        Assert.False(bannerService.WasBannerDisplayed);
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

    [Fact]
    public void RootCommand_DoesNotExposeRemovedExecSubcommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();

        Assert.DoesNotContain(command.Subcommands, subcommand => subcommand.Name == "exec");
    }
}
