// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREFOUNDRY001 // Preview tool types

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

    [Fact]
    public void AddBingGroundingTool_CreatesToolResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");
        var connection = project.AddConnection(search);

        var tool = project.AddBingGroundingTool("bing-tool", connection);

        Assert.NotNull(tool);
        Assert.NotNull(tool.Resource);
        Assert.Equal("bing-tool", tool.Resource.Name);
        Assert.IsType<BingGroundingToolResource>(tool.Resource);
        Assert.NotNull(tool.Resource.Connection);
    }

    [Fact]
    public void WithBingGroundingTool_AddsToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
        var search = builder.AddAzureSearch("search");
        var connection = project.AddConnection(search);
        var bingTool = project.AddBingGroundingTool("bing-tool", connection);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithTool(bingTool);

        Assert.Single(agent.Resource.Tools);
        Assert.Same(bingTool.Resource, agent.Resource.Tools[0]);
    }

    [Fact]
    public void WithSharePoint_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithSharePoint("conn-1", "conn-2");

        Assert.Single(agent.Resource.Tools);
        var spTool = Assert.IsType<SharePointToolDefinition>(agent.Resource.Tools[0]);
        Assert.Equal(["conn-1", "conn-2"], spTool.ProjectConnectionIds);
    }

    [Fact]
    public void WithFabric_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithFabric("fabric-conn-1");

        Assert.Single(agent.Resource.Tools);
        var fabricTool = Assert.IsType<FabricToolDefinition>(agent.Resource.Tools[0]);
        Assert.Equal(["fabric-conn-1"], fabricTool.ProjectConnectionIds);
    }

    [Fact]
    public void WithAzureFunction_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithAzureFunction(
                "myFunc",
                "Does something useful",
                BinaryData.FromString("""{"type":"object","properties":{}}"""),
                "https://queue.core.windows.net",
                "input-queue",
                "https://queue.core.windows.net",
                "output-queue");

        Assert.Single(agent.Resource.Tools);
        var funcTool = Assert.IsType<AzureFunctionToolDefinition>(agent.Resource.Tools[0]);
        Assert.Equal("myFunc", funcTool.FunctionName);
    }

    [Fact]
    public void MultipleToolTypes_AllAdded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
        var search = builder.AddAzureSearch("search");
        var searchTool = project.AddAzureAISearchTool("search-tool", search);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithCodeInterpreter()
            .WithWebSearch()
            .WithSharePoint("sp-conn")
            .WithFabric("fab-conn")
            .WithTool(searchTool);

        Assert.Equal(5, agent.Resource.Tools.Count);
        Assert.IsType<CodeInterpreterToolDefinition>(agent.Resource.Tools[0]);
        Assert.IsType<WebSearchToolDefinition>(agent.Resource.Tools[1]);
        Assert.IsType<SharePointToolDefinition>(agent.Resource.Tools[2]);
        Assert.IsType<FabricToolDefinition>(agent.Resource.Tools[3]);
        Assert.Same(searchTool.Resource, agent.Resource.Tools[4]);
    }

    [Fact]
    public void WithFunction_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithFunction(
                "get_weather",
                BinaryData.FromString("""{"type":"object","properties":{"location":{"type":"string"}}}"""),
                description: "Gets the current weather");

        Assert.Single(agent.Resource.Tools);
        var funcTool = Assert.IsType<FunctionToolDefinition>(agent.Resource.Tools[0]);
        Assert.Equal("get_weather", funcTool.FunctionName);
        Assert.Equal("Gets the current weather", funcTool.Description);
    }

    [Fact]
    public void WithImageGeneration_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithImageGeneration();

        Assert.Single(agent.Resource.Tools);
        Assert.IsType<ImageGenerationToolDefinition>(agent.Resource.Tools[0]);
    }

    [Fact]
    public void WithComputerUse_AddsToolToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent("my-agent", model)
            .WithComputerUse(1920, 1080);

        Assert.Single(agent.Resource.Tools);
        var computerTool = Assert.IsType<ComputerToolDefinition>(agent.Resource.Tools[0]);
        Assert.Equal(1920, computerTool.DisplayWidth);
        Assert.Equal(1080, computerTool.DisplayHeight);
        Assert.Equal("browser", computerTool.Environment);
    }
}
