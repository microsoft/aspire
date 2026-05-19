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

        var agent = project.AddPromptAgent(model, "my-agent", instructions: "You tell jokes.");

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

        var agent = project.AddPromptAgent(model, "my-agent", instructions: "You tell jokes.");

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

        var agent = project.AddPromptAgent(model, "my-agent");

        Assert.Same(project.Resource, agent.Resource.Project);
    }

    [Fact]
    public void AddPromptAgent_WithNullName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        Assert.Throws<ArgumentException>(() => project.AddPromptAgent(model, ""));
    }

    [Fact]
    public void AddPromptAgent_WithNullModel_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        Assert.Throws<ArgumentNullException>(() => project.AddPromptAgent(null!, "my-agent"));
    }

    [Fact]
    public void AddPromptAgent_InstructionsAreOptional()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var agent = project.AddPromptAgent(model, "my-agent");

        Assert.Null(agent.Resource.Instructions);
    }

    [Fact]
    public void AddPromptAgent_WithTools_AddToolsToAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var codeInterp = project.AddCodeInterpreterTool("ci");
        var webSearch = project.AddWebSearchTool("ws");

        var agent = project.AddPromptAgent(model, "my-agent",
            instructions: "You tell jokes.")
            .WithTool(codeInterp)
            .WithTool(webSearch);

        Assert.Equal(2, agent.Resource.Tools.Count);
        Assert.IsType<CodeInterpreterToolResource>(agent.Resource.Tools[0]);
        Assert.IsType<WebSearchToolResource>(agent.Resource.Tools[1]);
    }

    [Fact]
    public void AddCodeInterpreterTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddCodeInterpreterTool("code-interp");

        Assert.NotNull(tool);
        Assert.Equal("code-interp", tool.Resource.Name);
        Assert.IsType<CodeInterpreterToolResource>(tool.Resource);
        Assert.Same(project.Resource, tool.Resource.Project);
    }

    [Fact]
    public void AddFileSearchTool_CreatesResourceWithVectorStoreIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddFileSearchTool("fs", "store-1", "store-2");

        Assert.NotNull(tool);
        Assert.IsType<FileSearchToolResource>(tool.Resource);
        Assert.Equal(["store-1", "store-2"], tool.Resource.VectorStoreIds);
    }

    [Fact]
    public void AddWebSearchTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddWebSearchTool("ws");

        Assert.NotNull(tool);
        Assert.IsType<WebSearchToolResource>(tool.Resource);
    }

    [Fact]
    public void AddAISearchTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddAISearchTool("search");

        Assert.NotNull(tool);
        Assert.IsType<AzureAISearchToolResource>(tool.Resource);
        Assert.Same(project.Resource, tool.Resource.Project);
        Assert.Null(tool.Resource.SearchResource);
    }

    [Fact]
    public void AddAISearchTool_WithReference_LinksSearchResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");

        var tool = project.AddAISearchTool("search-tool").WithReference(search);

        Assert.NotNull(tool.Resource.SearchResource);
        Assert.Same(search.Resource, tool.Resource.SearchResource);
        Assert.NotNull(tool.Resource.Connection);
    }

    [Fact]
    public void AddAISearchTool_WithReference_Twice_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");
        var search2 = builder.AddAzureSearch("search2");

        var tool = project.AddAISearchTool("search-tool").WithReference(search);

        Assert.Throws<InvalidOperationException>(() => tool.WithReference(search2));
    }

    [Fact]
    public void AddBingGroundingTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddBingGroundingTool("bing");

        Assert.NotNull(tool);
        Assert.IsType<BingGroundingToolResource>(tool.Resource);
        Assert.Null(tool.Resource.Connection);
    }

    [Fact]
    public void AddBingGroundingTool_WithReference_LinksConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var bingResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing-test";
        var connection = project.AddBingGroundingConnection("bing-conn", bingResourceId);

        var tool = project.AddBingGroundingTool("bing").WithReference(connection);

        Assert.NotNull(tool.Resource.Connection);
        Assert.Same(connection.Resource, tool.Resource.Connection);
    }

    [Fact]
    public void AddBingGroundingTool_WithReference_Twice_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var bingResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing-test";
        var conn1 = project.AddBingGroundingConnection("bing-conn-1", bingResourceId);
        var conn2 = project.AddBingGroundingConnection("bing-conn-2", bingResourceId);

        var tool = project.AddBingGroundingTool("bing").WithReference(conn1);

        Assert.Throws<InvalidOperationException>(() => tool.WithReference(conn2));
    }

    [Fact]
    public void AddBingGroundingTool_WithResourceId_CreatesConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var bingResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing-test";
        var tool = project.AddBingGroundingTool("bing").WithReference(bingResourceId);

        Assert.NotNull(tool.Resource.Connection);
    }

    [Fact]
    public void AddBingGroundingTool_WithResourceId_Twice_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var bingResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing-test";
        var tool = project.AddBingGroundingTool("bing").WithReference(bingResourceId);

        Assert.Throws<InvalidOperationException>(() => tool.WithReference(bingResourceId));
    }

    [Fact]
    public void AddBingGroundingConnection_CreatesConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var bingResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Bing/accounts/bing-test";
        var connection = project.AddBingGroundingConnection("bing-conn", bingResourceId);

        Assert.NotNull(connection);
        Assert.IsType<BingGroundingConnectionResource>(connection.Resource);
    }

    [Fact]
    public void AddBingGroundingTool_WithParameterReference_SetsConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var bingResourceId = builder.AddParameter("bingResourceId");
        var tool = project.AddBingGroundingTool("bing").WithReference(bingResourceId);

        Assert.NotNull(tool.Resource.Connection);
    }

    [Fact]
    public void AddBingGroundingConnection_WithParameter_CreatesConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var bingResourceId = builder.AddParameter("bingResourceId");
        var connection = project.AddBingGroundingConnection("bing-conn", bingResourceId);

        Assert.NotNull(connection);
        Assert.IsType<BingGroundingConnectionResource>(connection.Resource);
    }

    [Fact]
    public void AddSharePointTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddSharePointTool("sp", "conn-1", "conn-2");

        Assert.NotNull(tool);
        Assert.IsType<SharePointToolResource>(tool.Resource);
        Assert.Equal(["conn-1", "conn-2"], tool.Resource.ProjectConnectionIds);
    }

    [Fact]
    public void AddFabricTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddFabricTool("fab", "fab-conn-1");

        Assert.NotNull(tool);
        Assert.IsType<FabricToolResource>(tool.Resource);
        Assert.Equal(["fab-conn-1"], tool.Resource.ProjectConnectionIds);
    }

    [Fact]
    public void AddAzureFunctionTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddAzureFunctionTool(
            "func-tool",
            "myFunc",
            "Does something useful",
            BinaryData.FromString("""{"type":"object","properties":{}}"""),
            "https://queue.core.windows.net",
            "input-queue",
            "https://queue.core.windows.net",
            "output-queue");

        Assert.NotNull(tool);
        Assert.IsType<AzureFunctionToolResource>(tool.Resource);
        Assert.Equal("myFunc", tool.Resource.FunctionName);
    }

    [Fact]
    public void AddFunctionTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddFunctionTool(
            "func",
            "get_weather",
            BinaryData.FromString("""{"type":"object","properties":{"location":{"type":"string"}}}"""),
            description: "Gets the current weather");

        Assert.NotNull(tool);
        Assert.IsType<FunctionToolResource>(tool.Resource);
        Assert.Equal("get_weather", tool.Resource.FunctionName);
        Assert.Equal("Gets the current weather", tool.Resource.Description);
    }

    [Fact]
    public void AddPromptAgent_WithMixedToolTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
        var search = builder.AddAzureSearch("search");

        var codeInterp = project.AddCodeInterpreterTool("ci");
        var webSearch = project.AddWebSearchTool("ws");
        var aiSearch = project.AddAISearchTool("search-tool").WithReference(search);
        var sharePoint = project.AddSharePointTool("sp", "sp-conn");

        var agent = project.AddPromptAgent(model, "my-agent",
            instructions: "You tell jokes.")
            .WithTool(codeInterp)
            .WithTool(webSearch)
            .WithTool(aiSearch)
            .WithTool(sharePoint);

        Assert.Equal(4, agent.Resource.Tools.Count);
        Assert.IsType<CodeInterpreterToolResource>(agent.Resource.Tools[0]);
        Assert.IsType<WebSearchToolResource>(agent.Resource.Tools[1]);
        Assert.IsType<AzureAISearchToolResource>(agent.Resource.Tools[2]);
        Assert.IsType<SharePointToolResource>(agent.Resource.Tools[3]);
    }

    [Fact]
    public void AddPromptAgent_ToolReusedAcrossAgents()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var codeInterp = project.AddCodeInterpreterTool("ci");

        var agent1 = project.AddPromptAgent(model, "agent-1").WithTool(codeInterp);
        var agent2 = project.AddPromptAgent(model, "agent-2").WithTool(codeInterp);

        Assert.Single(agent1.Resource.Tools);
        Assert.Single(agent2.Resource.Tools);
        Assert.Same(codeInterp.Resource, agent1.Resource.Tools[0]);
        Assert.Same(codeInterp.Resource, agent2.Resource.Tools[0]);
    }

    [Fact]
    public void AddPromptAgent_CrossProjectTool_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("account");
        var project1 = foundry.AddProject("proj-1");
        var project2 = foundry.AddProject("proj-2");
        var model = project1.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

        var toolFromProject2 = project2.AddCodeInterpreterTool("ci");

        Assert.Throws<InvalidOperationException>(() =>
            project1.AddPromptAgent(model, "agent").WithTool(toolFromProject2));
    }

    [Fact]
    public async Task AddPromptAgent_WithReference_ShouldBindConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("test-account")
            .AddProject("test-project");
        var model = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
        var agent = project.AddPromptAgent(model, "my-agent", instructions: "You tell jokes.");

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
        var agent = project.AddPromptAgent(model, "my-agent");

        Assert.IsAssignableFrom<IResourceWithConnectionString>(agent.Resource);
        Assert.IsAssignableFrom<IResourceWithEnvironment>(agent.Resource);
        Assert.IsNotAssignableFrom<IComputeResource>(agent.Resource);
    }

    [Fact]
    public void AddImageGenerationTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddImageGenerationTool("img-gen");

        Assert.NotNull(tool);
        Assert.IsType<ImageGenerationToolResource>(tool.Resource);
    }

    [Fact]
    public void AddComputerUseTool_CreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var tool = project.AddComputerUseTool("computer", displayWidth: 1920, displayHeight: 1080);

        Assert.NotNull(tool);
        var computerTool = Assert.IsType<ComputerToolResource>(tool.Resource);
        Assert.Equal(1920, computerTool.DisplayWidth);
        Assert.Equal(1080, computerTool.DisplayHeight);
        Assert.Equal("browser", computerTool.Environment);
    }

    [Fact]
    public void ToolResources_HaveProjectReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var ci = project.AddCodeInterpreterTool("ci");
        var fs = project.AddFileSearchTool("fs");
        var ws = project.AddWebSearchTool("ws");

        Assert.Same(project.Resource, ci.Resource.Project);
        Assert.Same(project.Resource, fs.Resource.Project);
        Assert.Same(project.Resource, ws.Resource.Project);
    }
}
