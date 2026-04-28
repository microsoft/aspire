// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
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
        var result = command.Parse("ps");

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
        var result = command.Parse("ps");

        await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, testInteractionService.DisplayEmptyLineCount);
    }
}
