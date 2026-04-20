// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable OPENAI001 // ResponseTool is for evaluation purposes only

using Azure.AI.Projects;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry.Tests;

public class PromptAgentConfigurationTests
{
    [Fact]
    public void DefaultDescription_IsPromptAgent()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", null);
        Assert.Equal("Prompt Agent", config.Description);
    }

    [Fact]
    public void Model_IsSetFromConstructor()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", "Tell jokes");
        Assert.Equal("gpt-4.1", config.Model);
    }

    [Fact]
    public void Instructions_IsSetFromConstructor()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", "Tell jokes");
        Assert.Equal("Tell jokes", config.Instructions);
    }

    [Fact]
    public void Instructions_CanBeNull()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", null);
        Assert.Null(config.Instructions);
    }

    [Fact]
    public void DefaultMetadata_ContainsDeployedByAndDeployedOn()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", null);
        Assert.Contains("DeployedBy", config.Metadata.Keys);
        Assert.Contains("DeployedOn", config.Metadata.Keys);
        Assert.Equal("Aspire Hosting Framework", config.Metadata["DeployedBy"]);
    }

    [Fact]
    public void Tools_IsInitiallyEmpty()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", null);
        Assert.Empty(config.Tools);
    }

    [Fact]
    public void ToAgentVersionCreationOptions_CreatesValidOptions()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", "Tell jokes")
        {
            Description = "Test Agent"
        };

        var options = config.ToAgentVersionCreationOptions();

        Assert.NotNull(options);
        Assert.IsType<AgentVersionCreationOptions>(options);
        Assert.Equal("Test Agent", options.Description);
    }

    [Fact]
    public void ToAgentVersionCreationOptions_IncludesMetadata()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", null);
        config.Metadata["TestKey"] = "TestValue";

        var options = config.ToAgentVersionCreationOptions();

        Assert.Contains("TestKey", options.Metadata.Keys);
        Assert.Equal("TestValue", options.Metadata["TestKey"]);
    }

    [Fact]
    public void ToAgentVersionCreationOptions_IncludesTools()
    {
        var config = new PromptAgentConfiguration("gpt-4.1", null);
        config.Tools.Add(new WebSearchTool());

        var options = config.ToAgentVersionCreationOptions();

        Assert.NotNull(options);
    }
}
