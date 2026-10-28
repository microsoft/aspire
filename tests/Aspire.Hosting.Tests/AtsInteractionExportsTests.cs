// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Ats;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIRETERMINAL001 // Tests for terminal interaction adapters.

[Trait("Partition", "5")]
public class AtsInteractionExportsTests
{
    [Fact]
    public async Task PromptTerminal_MapsOptionsAndBorrowsRegisteredTerminal()
    {
        await using var terminals = TestTerminalService.Create();
        await using var services = new ServiceCollection().AddSingleton(terminals).BuildServiceProvider();
        var service = new InteractionService(NullLogger<InteractionService>.Instance, new DistributedApplicationOptions(),
            services, new ConfigurationBuilder().Build(), new TestInteractionFileUploadStore());
        var terminal = TerminalExports.CreateTerminal(terminals, new CreateTerminalOptions
        {
            Title = "Terminal", Executable = "must-not-be-started", Placement = TerminalPlacement.Dialog
        });

        for (var i = 0; i < 2; i++)
        {
            var result = await service.PromptTerminal("Working", terminal, new InteractionTerminalOptions
            {
                Title = "Setup",
                PrimaryButtonText = "Cancel",
                EnableMessageMarkdown = true,
                Work = context =>
                {
                    var interaction = Assert.Single(service.GetCurrentInteractions());
                    Assert.Equal(terminal.Id, Assert.IsType<Interaction.TerminalInteractionInfo>(interaction.InteractionInfo).TerminalId);
                    var options = Assert.IsType<TerminalInteractionOptions>(interaction.Options);
                    Assert.Equal("Setup", options.Title);
                    Assert.Equal("Cancel", options.PrimaryButtonText);
                    Assert.True(options.EnableMessageMarkdown);
                    Assert.True(context.CancellationToken.CanBeCanceled);
                    return Task.CompletedTask;
                }
            }).DefaultTimeout();

            Assert.False(result.Canceled);
            Assert.True(result.Value);
            Assert.Empty(service.GetCurrentInteractions());
            Assert.Same(terminal, TerminalExports.GetTerminal(terminals, terminal.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptTerminal_CancellationOrFailureDoesNotDisposeTerminal(bool fail)
    {
        await using var terminals = TestTerminalService.Create();
        await using var services = new ServiceCollection().AddSingleton(terminals).BuildServiceProvider();
        var service = new InteractionService(NullLogger<InteractionService>.Instance, new DistributedApplicationOptions(),
            services, new ConfigurationBuilder().Build(), new TestInteractionFileUploadStore());
        var terminal = TerminalExports.CreateTerminal(terminals, new CreateTerminalOptions
        {
            Title = "Terminal", Executable = "must-not-be-started", Placement = TerminalPlacement.Dialog
        });
        using var cts = new CancellationTokenSource();
        var failure = new InvalidOperationException("work failed");
        var prompt = service.PromptTerminal("Working", terminal, new InteractionTerminalOptions
        {
            Work = async context =>
            {
                if (fail)
                {
                    throw failure;
                }
                cts.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
        }, cts.Token);

        if (fail)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => prompt).DefaultTimeout());
        }
        else
        {
            Assert.True((await prompt.DefaultTimeout()).Canceled);
        }
        Assert.Empty(service.GetCurrentInteractions());
        Assert.Same(terminal, TerminalExports.GetTerminal(terminals, terminal.Id));
    }

    [Theory]
    [InlineData(TerminalPlacement.Dock)]
    [InlineData(TerminalPlacement.None)]
    public async Task PromptTerminal_RejectsNonDialogPlacement(TerminalPlacement placement)
    {
        await using var terminals = TestTerminalService.Create();
        await using var services = new ServiceCollection().AddSingleton(terminals).BuildServiceProvider();
        var service = new InteractionService(NullLogger<InteractionService>.Instance, new DistributedApplicationOptions(),
            services, new ConfigurationBuilder().Build(), new TestInteractionFileUploadStore());
        var terminal = TerminalExports.CreateTerminal(terminals, new CreateTerminalOptions
        {
            Title = "Terminal", Executable = "must-not-be-started", Placement = placement
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptTerminal("Working", terminal));
        Assert.Empty(service.GetCurrentInteractions());
        Assert.Same(terminal, TerminalExports.GetTerminal(terminals, terminal.Id));
    }

    [Fact]
    public async Task PromptTerminal_RejectsForeignAndDisposedTerminals()
    {
        await using var terminals = TestTerminalService.Create();
        await using var foreignTerminals = TestTerminalService.Create();
        await using var services = new ServiceCollection().AddSingleton(terminals).BuildServiceProvider();
        var service = new InteractionService(NullLogger<InteractionService>.Instance, new DistributedApplicationOptions(),
            services, new ConfigurationBuilder().Build(), new TestInteractionFileUploadStore());
        var options = new CreateTerminalOptions
        {
            Title = "Terminal", Executable = "must-not-be-started", Placement = TerminalPlacement.Dialog
        };
        var foreign = TerminalExports.CreateTerminal(foreignTerminals, options);
        var disposed = TerminalExports.CreateTerminal(terminals, options);
        await TerminalExports.Dispose(disposed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptTerminal("Working", foreign));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptTerminal("Working", disposed));

        Assert.Empty(service.GetCurrentInteractions());
        Assert.Same(foreign, TerminalExports.GetTerminal(foreignTerminals, foreign.Id));
        Assert.Null(TerminalExports.GetTerminal(terminals, disposed.Id));
    }

    [Fact]
    public async Task PromptProgress_TitleInOptions_MappedToProgressInteractionOptions()
    {
        var interactionService = new TestInteractionService();

        var result = await interactionService.PromptProgress(
            "Completing **work**...",
            new InteractionProgressOptions
            {
                Title = "Progress",
                PrimaryButtonText = "Cancel",
                EnableMessageMarkdown = true,
                Work = _ => Task.CompletedTask
            }).DefaultTimeout();

        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        Assert.Equal(InteractionType.Progress, interaction.Type);
        Assert.Equal("Progress", interaction.Title);
        Assert.Equal("Completing **work**...", interaction.Message);

        var options = Assert.IsType<ProgressInteractionOptions>(interaction.Options);
        Assert.Equal("Progress", options.Title);
        Assert.Equal("Cancel", options.PrimaryButtonText);
        Assert.True(options.EnableMessageMarkdown);

        Assert.False(result.Canceled);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task PromptProgress_NoTitleInOptions_LeavesTitleUnset()
    {
        var interactionService = new TestInteractionService();

        await interactionService.PromptProgress(
            "Completing work...",
            new InteractionProgressOptions
            {
                Work = _ => Task.CompletedTask
            }).DefaultTimeout();

        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        Assert.Equal(string.Empty, interaction.Title);

        var options = Assert.IsType<ProgressInteractionOptions>(interaction.Options);
        Assert.Null(options.Title);
    }
}
