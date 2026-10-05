// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Ats;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

[Trait("Partition", "5")]
public class AtsInteractionExportsTests
{
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
