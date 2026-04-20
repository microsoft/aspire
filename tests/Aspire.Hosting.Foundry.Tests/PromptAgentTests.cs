// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Foundry.Tests;

public class PromptAgentTests
{
    [Fact]
    public void AddPromptAgent_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model, instructions: "You tell jokes.");

        Assert.NotNull(agent);
        Assert.NotNull(agent.Resource);
        Assert.Equal("my-agent", agent.Resource.Name);
        Assert.IsType<AzurePromptAgentResource>(agent.Resource);
    }

    [Fact]
    public void AddPromptAgent_SetsModelAndInstructions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model, instructions: "You tell jokes.");

        Assert.Equal("gpt41", agent.Resource.Model);
        Assert.Equal("You tell jokes.", agent.Resource.Instructions);
    }

    [Fact]
    public void AddPromptAgent_SetsProjectReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model);

        Assert.Same(project.Resource, agent.Resource.Project);
    }

    [Fact]
    public void AddPromptAgent_WithNullName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        Assert.Throws<ArgumentException>(() => project.AddPromptAgent("", model));
    }

    [Fact]
    public void AddPromptAgent_WithNullModel_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        Assert.Throws<ArgumentNullException>(() => project.AddPromptAgent("my-agent", null!));
    }

    [Fact]
    public void AddPromptAgent_InstructionsAreOptional()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model);

        Assert.Null(agent.Resource.Instructions);
    }

    [Fact]
    public void WithCodeInterpreter_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithCodeInterpreter();

        Assert.Single(agent.Resource.Tools);
        Assert.IsType<CodeInterpreterToolDefinition>(agent.Resource.Tools[0]);
    }

    [Fact]
    public void WithFileSearch_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithFileSearch("store-1", "store-2");

        Assert.Single(agent.Resource.Tools);
        var fsTool = Assert.IsType<FileSearchToolDefinition>(agent.Resource.Tools[0]);
        Assert.Equal(["store-1", "store-2"], fsTool.VectorStoreIds);
    }

    [Fact]
    public void WithWebSearch_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithWebSearch();

        Assert.Single(agent.Resource.Tools);
        Assert.IsType<WebSearchToolDefinition>(agent.Resource.Tools[0]);
    }

    [Fact]
    public void MultipleTools_AllAdded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithCodeInterpreter()
            .WithFileSearch()
            .WithWebSearch();

        Assert.Equal(3, agent.Resource.Tools.Count);
        Assert.IsType<CodeInterpreterToolDefinition>(agent.Resource.Tools[0]);
        Assert.IsType<FileSearchToolDefinition>(agent.Resource.Tools[1]);
        Assert.IsType<WebSearchToolDefinition>(agent.Resource.Tools[2]);
    }

    [Fact]
    public void AddAzureAISearchTool_CreatesToolResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");

        var tool = project.AddAzureAISearchTool("search-tool", search);

        Assert.NotNull(tool);
        Assert.NotNull(tool.Resource);
        Assert.Equal("search-tool", tool.Resource.Name);
        Assert.IsType<AzureAISearchToolResource>(tool.Resource);
        Assert.Same(search.Resource, tool.Resource.SearchResource);
    }

    [Fact]
    public void AddAzureAISearchTool_SetsConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");

        var tool = project.AddAzureAISearchTool("search-tool", search);

        Assert.NotNull(tool.Resource.Connection);
    }

    [Fact]
    public void WithTool_AddsResourceToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
        var search = builder.AddAzureSearch("search");
        var searchTool = project.AddAzureAISearchTool("search-tool", search);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithTool(searchTool);

        Assert.Single(agent.Resource.Tools);
        Assert.Same(searchTool.Resource, agent.Resource.Tools[0]);
    }

    [Fact]
    public async Task AddPromptAgent_WithReference_ShouldBindConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("test-account")
            .AddProject("test-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
        var agent = project.AddPromptAgent("my-agent", model, instructions: "You tell jokes.");

        var pyapp = builder.AddPythonApp("app", "./app.py", "main:app")
            .WithReference(agent);

        builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pyapp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Contains(envVars, kvp => kvp.Key is "ConnectionStrings__my-agent");
    }

    [Fact]
    public void PromptAgentResource_ImplementsExpectedInterfaces()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
        var agent = project.AddPromptAgent("my-agent", model);

        Assert.IsAssignableFrom<IResourceWithConnectionString>(agent.Resource);
        Assert.IsAssignableFrom<IResourceWithEnvironment>(agent.Resource);
        Assert.IsAssignableFrom<IComputeResource>(agent.Resource);
    }
}
