// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry.Tests;

public class PromptAgentConfigurationTests
{
    [Fact]
    public void Constructor_SetsModelAndInstructions()
    {
        var config = new PromptAgentConfiguration("gpt-4", "You are a helpful assistant.");

        Assert.Equal("gpt-4", config.Model);
        Assert.Equal("You are a helpful assistant.", config.Instructions);
    }

    [Fact]
    public void DefaultDescription_IsPromptAgent()
    {
        var config = new PromptAgentConfiguration("gpt-4", "test");
        Assert.Equal("Prompt Agent", config.Description);
    }

    [Fact]
    public void ToAgentVersionCreationOptions_ProducesValidOptions()
    {
        var config = new PromptAgentConfiguration("gpt-4", "You are a helpful assistant.")
        {
            Description = "My test agent"
        };

        var options = config.ToAgentVersionCreationOptions();

        Assert.NotNull(options);
        Assert.Equal("My test agent", options.Description);
    }

    [Fact]
    public void DefaultMetadata_ContainsDeployedByAndOn()
    {
        var config = new PromptAgentConfiguration("gpt-4", null);

        Assert.Contains(config.Metadata, kvp => kvp.Key == "DeployedBy");
        Assert.Contains(config.Metadata, kvp => kvp.Key == "DeployedOn");
    }

    [Fact]
    public void NullInstructions_DoesNotThrow()
    {
        var config = new PromptAgentConfiguration("gpt-4", null);

        var options = config.ToAgentVersionCreationOptions();
        Assert.NotNull(options);
    }
}
