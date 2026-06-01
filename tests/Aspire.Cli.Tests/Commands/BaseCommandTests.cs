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
}
