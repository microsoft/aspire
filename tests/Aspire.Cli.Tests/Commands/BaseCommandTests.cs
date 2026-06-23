// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class BaseCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("ps", false)]
    [InlineData("ps --format json", true)]
    [InlineData("ps --format table", false)]
    [InlineData("ps --format invalid", false)]
    [InlineData("docs --format json", false)]
    public async Task BaseCommand_FormatOption_SetsConsoleOutputCorrectly(string args, bool expectErrorConsole)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(args);

        await result.InvokeAsync().DefaultTimeout();

        var expected = expectErrorConsole ? ConsoleOutput.Error : ConsoleOutput.Standard;
        Assert.Equal(expected, testInteractionService.Console);
    }

    [Fact]
    public async Task BaseCommand_IntegrationListFormatJson_SetsConsoleOutputCorrectly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.SearchPackagesAsyncCallback = (_, _, _, _, _, _, _, _, _, _) => (0, []);
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("integration list --format json");

        await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ConsoleOutput.Error, testInteractionService.Console);
    }

    [Fact]
    public async Task BaseCommand_WithNoUpdateNotification_DoesNotDisplayTrailingBlankLine()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var testNotifier = new TestCliUpdateNotifier();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.CliUpdateNotifierFactory = _ => testNotifier;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop");

        await result.InvokeAsync().DefaultTimeout();

        Assert.True(testNotifier.NotifyWasCalled);
        Assert.Equal(0, testInteractionService.DisplayEmptyLineCount);
    }

    [Fact]
    public async Task BaseCommand_WithUpdateNotification_DoesNotDisplayTrailingBlankLine()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var testNotifier = new TestCliUpdateNotifier
        {
            IsUpdateAvailableCallback = () => true,
            NotifyIfUpdateAvailableCallback = () => testInteractionService.DisplayVersionUpdateNotification("13.3.0-preview.1", "aspire update")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.CliUpdateNotifierFactory = _ => testNotifier;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("stop");

        await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, testInteractionService.DisplayEmptyLineCount);
    }

    [Theory]
    [InlineData("run --format json", false)]
    [InlineData("run", true)]
    [InlineData("docs", false)]
    public async Task BaseCommand_UpdateNotification_RespectJsonFormat(string args, bool expectNotifyCalled)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var testNotifier = new TestCliUpdateNotifier
        {
            IsUpdateAvailableCallback = () => true,
            NotifyIfUpdateAvailableCallback = () => testInteractionService.DisplayVersionUpdateNotification("13.3.0-preview.1", "aspire update")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.CliUpdateNotifierFactory = _ => testNotifier;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(args);

        await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(expectNotifyCalled, testNotifier.NotifyWasCalled);
    }

    [Fact]
    public async Task BaseCommand_OnFailure_DisplaysLogFilePathOnStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(null, []))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);

        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var expectedLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, executionContext.LogFilePath);
        var logMessage = Assert.Single(testInteractionService.DisplayedMessages, m => m.Message == expectedLogMessage);
        Assert.Equal(ConsoleOutput.Error, logMessage.ConsoleOverride);
    }

    [Fact]
    public async Task BaseCommand_OnFailure_DisplaysAppHostLogFilePathOnStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(null, []))
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
            options.CliExecutionContextFactory = _ =>
            {
                var ctx = workspace.CreateExecutionContext();
                ctx.AppHostCliLogFilePath = "/tmp/aspire-logs/apphost.log";
                return ctx;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);

        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var expectedCliLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, executionContext.LogFilePath);
        var expectedAppHostLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeAppHostLogsAt, "/tmp/aspire-logs/apphost.log");

        var cliLogMessage = Assert.Single(testInteractionService.DisplayedMessages, m => m.Message == expectedCliLogMessage);
        Assert.Equal(ConsoleOutput.Error, cliLogMessage.ConsoleOverride);

        var appHostLogMessage = Assert.Single(testInteractionService.DisplayedMessages, m => m.Message == expectedAppHostLogMessage);
        Assert.Equal(ConsoleOutput.Error, appHostLogMessage.ConsoleOverride);
    }

    [Fact]
    public async Task BaseCommand_OnCancellationWithErrorExitCode_DisplaysCancellationMessageOnStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                throw new OperationCanceledException()
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("add SomePackage");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // add catches OperationCanceledException and returns CommandResult.Cancelled() (exit code 130)
        Assert.Equal(CliExitCodes.Cancelled, exitCode);

        var cancellationOverride = Assert.Single(testInteractionService.DisplayedCancellations);
        Assert.Equal(ConsoleOutput.Error, cancellationOverride);
    }

    [Fact]
    public async Task BaseCommand_OnCancellationWithSuccessExitCode_DisplaysCancellationMessageOnStdout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            // Throw with the cancellation token so RunCommand's
            // `catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)`
            // matches and returns CommandResult.Cancelled(CliExitCodes.Success).
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, ct) =>
                throw new OperationCanceledException(ct)
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // run catches OperationCanceledException and returns CommandResult.Cancelled(CliExitCodes.Success)
        var result = command.Parse("run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var cancellationOverride = Assert.Single(testInteractionService.DisplayedCancellations);
        Assert.Null(cancellationOverride);
    }

    [Fact]
    public async Task BaseCommand_OnSuccess_DoesNotDisplayLogFilePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // On success, no log file messages should be displayed
        Assert.DoesNotContain(testInteractionService.DisplayedMessages,
            m => m.ConsoleOverride == ConsoleOutput.Error);
    }

    [Fact]
    public async Task BaseCommand_OnUnexpectedException_ReturnsInvalidCommandExitCode_AndDisplaysError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var backchannelMonitor = new TestAuxiliaryBackchannelMonitor
        {
            ScanAsyncCallback = _ => throw new InvalidOperationException("Something went wrong")
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => backchannelMonitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);

        // Verify error message was displayed
        var expectedErrorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, "Something went wrong");
        Assert.Contains(expectedErrorMessage, testInteractionService.DisplayedErrors);

        // Verify log file path was displayed on stderr
        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var expectedLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, executionContext.LogFilePath);
        var logMessage = Assert.Single(testInteractionService.DisplayedMessages, m => m.Message == expectedLogMessage);
        Assert.Equal(ConsoleOutput.Error, logMessage.ConsoleOverride);
    }

    [Fact]
    public async Task BaseCommand_OnCancellation_DisplaysStoppingMessageAfterDelay()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var handlerEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectLocator = new TestProjectLocator
        {
            // Block inside the handler until cancellation, then wait 500ms before throwing
            // so the 200ms stopping-message timer fires.
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = async (_, _, _, ct) =>
            {
                handlerEnteredTcs.SetResult();
                try
                {
                    await AsyncTestHelpers.WaitForCancellationAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Simulate slow cleanup that takes longer than 200ms
                    await Task.Delay(500, CancellationToken.None);
                    throw;
                }

                return new AppHostProjectSearchResult(null, []);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        var invokeTask = result.InvokeAsync(cancellationToken: cts.Token);

        // Wait for the handler to start, then cancel
        await handlerEnteredTcs.Task.DefaultTimeout();
        await cts.CancelAsync();

        await invokeTask.DefaultTimeout();

        // The stopping message should have been shown exactly once (by the 200ms timer),
        // and not duplicated by the normal cancellation result path.
        Assert.Single(testInteractionService.DisplayedCancellations);
    }

    [Fact]
    public async Task BaseCommand_OnCancellation_DoesNotDisplayStoppingMessageIfHandlerCompletesQuickly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            // Throw OperationCanceledException immediately on cancellation (no delay).
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = async (_, _, _, ct) =>
            {
                await AsyncTestHelpers.WaitForCancellationAsync(ct);
                return new AppHostProjectSearchResult(null, []);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run");

        using var cts = new CancellationTokenSource();
        // Cancel immediately — handler will throw OperationCanceledException right away
        cts.Cancel();

        var exitCode = await result.InvokeAsync(cancellationToken: cts.Token).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // The cancellation message should still be displayed (from the normal result path)
        // since the handler completed before the 200ms timer.
        Assert.Single(testInteractionService.DisplayedCancellations);
    }

    [Theory]
    [InlineData("run --AppHost somepath", "--AppHost", "--apphost")]
    [InlineData("run --Apphost somepath", "--Apphost", "--apphost")]
    [InlineData("run --APPHOST somepath", "--APPHOST", "--apphost")]
    [InlineData("start --AppHost somepath", "--AppHost", "--apphost")]
    [InlineData("run --No-Build", "--No-Build", "--no-build")]
    [InlineData("run --Project somepath", "--Project", "--project")]
    [InlineData("run --Debug", "--Debug", "--debug")]
    [InlineData("run --AppHost=somepath", "--AppHost", "--apphost")]
    [InlineData("run --APPHOST=somepath", "--APPHOST", "--apphost")]
    public async Task BaseCommand_MiscasedOption_ReturnsErrorWithSuggestion(string args, string badOption, string correctOption)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(args);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        var expectedError = string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.UnrecognizedOptionDidYouMeanFormat, badOption, correctOption);
        Assert.Single(testInteractionService.DisplayedErrors, expectedError);
    }

    [Fact]
    public async Task BaseCommand_CorrectlyCasedOption_DoesNotReturnMiscasedError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, ct) =>
                throw new OperationCanceledException(ct)
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --apphost is the correct casing — should not trigger the miscased option check
        var result = command.Parse("run --apphost somepath");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Empty(testInteractionService.DisplayedErrors);
    }

    [Theory]
    [InlineData("--apphost", "somepath", false)]
    [InlineData("--AppHost", "somepath", true)]
    [InlineData("--APPHOST", "somepath", true)]
    public async Task BaseCommand_OptionWithEqualsValue_ParsedCorrectlyOrFlaggedAsMiscased(string optionName, string value, bool expectError)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, ct) =>
                throw new OperationCanceledException(ct)
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"run {optionName}={value}");

        if (expectError)
        {
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
            var expectedError = string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.UnrecognizedOptionDidYouMeanFormat, optionName, "--apphost");
            Assert.Single(testInteractionService.DisplayedErrors, expectedError);
        }
        else
        {
            // System.CommandLine accepted the --option=value syntax
            Assert.Empty(result.Errors);
            Assert.Empty(result.UnmatchedTokens);

            // The option value was parsed correctly
            var parsedValue = result.GetValue(AppHostLauncher.s_appHostOption);
            Assert.NotNull(parsedValue);
            Assert.Equal(value, parsedValue.Name);

            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Empty(testInteractionService.DisplayedErrors);
        }
    }

    [Fact]
    public async Task BaseCommand_UnrelatedUnmatchedToken_DoesNotReturnMiscasedError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, ct) =>
                throw new OperationCanceledException(ct)
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --custom-arg is a completely unknown option, not a miscased version of a known option
        var result = command.Parse("run --custom-arg value");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Empty(testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task BaseCommand_MiscasedOptionAfterDoubleDash_IsNotFlagged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, ct) =>
                throw new OperationCanceledException(ct)
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --AppHost after "--" is an intentional pass-through argument, not a typo
        var result = command.Parse("run -- --AppHost somepath");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Empty(testInteractionService.DisplayedErrors);
    }

    [Fact]
    public async Task BaseCommand_MiscasedOptionBeforeDoubleDash_WithSameTokenAfter_IsFlagged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --AppHost before "--" is a typo and should be flagged even though
        // the same token also appears after "--" as a pass-through argument.
        var result = command.Parse("run --AppHost foo -- --AppHost bar");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        var expectedError = string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.UnrecognizedOptionDidYouMeanFormat, "--AppHost", "--apphost");
        Assert.Single(testInteractionService.DisplayedErrors, expectedError);
    }

    [Fact]
    public void BaseCommand_TreatUnmatchedTokensAsErrorsTrue_DoesNotCheckMiscasedOptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // "add" has TreatUnmatchedTokensAsErrors = true (the default), so System.CommandLine
        // handles unrecognized options. The miscased check should not run.
        var result = command.Parse("add --AppHost somepath");

        // System.CommandLine itself should report the error.
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task BaseCommand_ResourceCommand_MiscasedOptionBeforeDoubleDash_IsFlagged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --AppHost before "--" collides case-insensitively with the CLI's --apphost option.
        // Users who need to pass an argument named AppHost to a resource command should use
        // the "--" separator to disambiguate.
        var result = command.Parse("resource myresource mycommand --AppHost primary");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        var expectedError = string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.UnrecognizedOptionDidYouMeanFormat, "--AppHost", "--apphost");
        Assert.Single(testInteractionService.DisplayedErrors, expectedError);
    }
}
