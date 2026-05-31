// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Pipelines APIs are experimental.
#pragma warning disable ASPIREAZURE001 // AzureEnvironmentResource is experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Azure.AI.Projects.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Foundry.Tests;

public class ToolboxTests
{
    [Fact]
    public void AddToolbox_CreatesProjectChildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var toolbox = project.AddToolbox("field-tools", t => t.Version = "v1");

        Assert.Equal("field-tools", toolbox.Resource.Name);
        Assert.Equal("v1", toolbox.Resource.Version);
        Assert.Same(project.Resource, toolbox.Resource.Parent);
    }

    [Fact]
    public void WithToolMethods_AddToolDefinitions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");

        var toolbox = project.AddToolbox("field-tools")
            .WithWebSearchTool()
            .WithMcpTool("inventory", "https://inventory.example.com/mcp")
            .WithAISearchTool("knowledge-base", search, "docs");

        Assert.Collection(
            toolbox.Resource.Tools,
            tool =>
            {
                var webSearch = Assert.IsType<FoundryToolboxWebSearchToolDefinition>(tool);
                Assert.Equal("web-search", webSearch.Name);
            },
            tool =>
            {
                var mcp = Assert.IsType<FoundryToolboxMcpToolDefinition>(tool);
                Assert.Equal("inventory", mcp.Name);
                Assert.Equal("https://inventory.example.com/mcp", mcp.EndpointExpression.ValueExpression);
            },
            tool =>
            {
                var aiSearch = Assert.IsType<FoundryToolboxAzureAISearchToolDefinition>(tool);
                Assert.Equal("knowledge-base", aiSearch.Name);
                Assert.Same(search.Resource, aiSearch.SearchResource);
                Assert.Equal("docs", aiSearch.IndexName);
                Assert.NotNull(aiSearch.Connection);
            });
    }

    [Fact]
    public async Task WithReference_InjectsToolboxConnectionProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var toolbox = project.AddToolbox("field-tools", t => t.Version = "v1");

        var pyapp = builder.AddPythonApp("app", "./app.py", "main:app")
            .WithReference(toolbox);

        builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pyapp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Contains(envVars, kvp =>
            kvp.Key == "FIELD_TOOLS_NAME"
            && kvp.Value == "field-tools");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "FIELD_TOOLS_PROJECTENDPOINT"
            && kvp.Value == "{my-project.outputs.endpoint}");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "FIELD_TOOLS_URI"
            && kvp.Value == "{my-project.outputs.endpoint}/toolboxes/field-tools/versions/v1/mcp?api-version=v1");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "FIELD_TOOLS_FOUNDRYFEATURES"
            && kvp.Value == "Toolboxes=V1Preview");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "FIELD_TOOLS_AUTHORIZATIONSCOPE"
            && kvp.Value == "https://ai.azure.com/.default");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "ConnectionStrings__field-tools"
            && kvp.Value == "{field-tools.connectionString}");
    }

    [Fact]
    public async Task AsHostedAgent_ResolvesToolboxConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var toolbox = project.AddToolbox("field-tools", t => t.Version = "v1");

        var agent = builder.AddPythonApp("agent", "./app.py", "main:app")
            .WithReference(toolbox)
            .AsHostedAgent(project);

        using var app = builder.Build();
        var hostedAgent = Assert.Single(builder.Resources.OfType<AzureHostedAgentResource>());
        project.Resource.Outputs["endpoint"] = "https://project.example.com";

        // TEMP DEBUG: capture env var callback keys + values from python app + hosted agent + project,
        // so that on failure the assertion message contains enough info to diagnose CI-only failures.
        var dump = new System.Text.StringBuilder();
        dump.AppendLine("---- TEMP DEBUG DUMP ----");
        foreach (var (label, res) in new[]
        {
            ("python-app (agent.Resource)", (IResource)agent.Resource),
            ("hosted-agent", hostedAgent),
            ("project (my-project)", project.Resource)
        })
        {
            dump.AppendLine($"== {label} env vars ==");
            if (!res.TryGetEnvironmentVariables(out var callbacks))
            {
                dump.AppendLine("  (no env var callbacks)");
                continue;
            }
            var ctx = new EnvironmentCallbackContext(builder.ExecutionContext, res, new Dictionary<string, object>());
            foreach (var cb in callbacks)
            {
                try
                {
                    await cb.Callback(ctx);
                }
                catch (Exception ex)
                {
                    dump.AppendLine($"  (callback threw: {ex.GetType().Name}: {ex.Message})");
                }
            }
            dump.AppendLine($"  count = {ctx.EnvironmentVariables.Count}");
            foreach (var (k, v) in ctx.EnvironmentVariables)
            {
                dump.AppendLine($"  {k} = [{v?.GetType().FullName}] {v}");
            }
        }

        // Dump pipeline steps and annotations on the python app to spot a leak via annotation sharing.
        dump.AppendLine("== python-app annotations ==");
        foreach (var ann in agent.Resource.Annotations)
        {
            dump.AppendLine($"  {ann.GetType().FullName}");
        }
        dump.AppendLine($"== project.Outputs keys ==");
        foreach (var key in project.Resource.Outputs.Keys)
        {
            dump.AppendLine($"  {key} = {project.Resource.Outputs[key]}");
        }
        dump.AppendLine("---- END DUMP ----");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var envVars = await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
                builder.ExecutionContext,
                hostedAgent,
                agent.Resource,
                NullLogger<ToolboxTests>.Instance,
                cts.Token);

            Assert.Equal("https://project.example.com/toolboxes/field-tools/versions/v1/mcp?api-version=v1", envVars["ConnectionStrings__field-tools"]);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Resolution failed: {ex.GetType().FullName}: {ex.Message}\n\n{dump}\n\nStack:\n{ex.StackTrace}", ex);
        }
    }

    [Fact]
    public async Task AddToolbox_RegistersPublishModeDeployStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var toolbox = project.AddToolbox("field-tools", t => t.Version = "v1");

        using var app = builder.Build();

        var annotation = Assert.Single(toolbox.Resource.Annotations.OfType<PipelineStepAnnotation>());

        var ctx = new PipelineStepFactoryContext
        {
            PipelineContext = CreatePipelineContext(app, DistributedApplicationOperation.Publish),
            Resource = toolbox.Resource
        };

        var steps = (await annotation.CreateStepsAsync(ctx)).ToList();

        // In publish mode only the deploy step is registered (no before-start hook).
        var step = Assert.Single(steps);
        Assert.Equal("deploy-field-tools", step.Name);
        Assert.Contains(WellKnownPipelineTags.DeployCompute, step.Tags);
        Assert.Contains(WellKnownPipelineSteps.Deploy, step.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.DeployPrereq, step.DependsOnSteps);
        Assert.Contains(AzureEnvironmentResource.ProvisionInfrastructureStepName, step.DependsOnSteps);
        Assert.Same(toolbox.Resource, step.Resource);
    }

    [Fact]
    public async Task AddToolbox_RegistersRunModeBeforeStartStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var toolbox = project.AddToolbox("field-tools", t => t.Version = "v1");

        using var app = builder.Build();

        var annotation = Assert.Single(toolbox.Resource.Annotations.OfType<PipelineStepAnnotation>());

        var ctx = new PipelineStepFactoryContext
        {
            PipelineContext = CreatePipelineContext(app, DistributedApplicationOperation.Run),
            Resource = toolbox.Resource
        };

        var steps = (await annotation.CreateStepsAsync(ctx)).ToList();

        Assert.Equal(2, steps.Count);

        var beforeStart = Assert.Single(steps, s => s.Name == "deploy-field-tools-before-start");
        Assert.Contains("before-start", beforeStart.RequiredBySteps);
        Assert.Contains("run-mode-azure-provision", beforeStart.DependsOnSteps);
        Assert.Same(toolbox.Resource, beforeStart.Resource);

        var deploy = Assert.Single(steps, s => s.Name == "deploy-field-tools");
        Assert.Contains(WellKnownPipelineTags.DeployCompute, deploy.Tags);
    }

    [Fact]
    public async Task WebSearchToolDefinition_ConvertsToProjectsAgentTool()
    {
        var tool = new FoundryToolboxWebSearchToolDefinition("web-search");

        var projectTool = await tool.ToProjectsAgentToolAsync(CancellationToken.None);

        Assert.NotNull(projectTool);
    }

    [Fact]
    public async Task AzureAISearchToolDefinition_ConvertsToAzureAISearchTool()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");

        var toolbox = project.AddToolbox("field-tools")
            .WithAISearchTool("knowledge-base", search, "docs");

        // Pre-seed the connection's bicep output so the tool conversion can resolve it without
        // running real provisioning.
        var def = Assert.IsType<FoundryToolboxAzureAISearchToolDefinition>(toolbox.Resource.Tools[0]);
        def.Connection.Outputs["id"] = "/subscriptions/sub/resourceGroups/rg/connections/search";

        var projectTool = await def.ToProjectsAgentToolAsync(CancellationToken.None);

        var aiSearch = Assert.IsType<AzureAISearchTool>(projectTool);
        var index = Assert.Single(aiSearch.Options.Indexes);
        Assert.Equal("/subscriptions/sub/resourceGroups/rg/connections/search", index.ProjectConnectionId);
        Assert.Equal("docs", index.IndexName);
    }

    [Fact]
    public async Task McpToolDefinition_ConvertsWithLiteralEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var toolbox = project.AddToolbox("field-tools")
            .WithMcpTool("inventory", "https://inventory.example.com/mcp");

        var def = Assert.IsType<FoundryToolboxMcpToolDefinition>(toolbox.Resource.Tools[0]);

        var projectTool = await def.ToProjectsAgentToolAsync(CancellationToken.None);

        Assert.NotNull(projectTool);
    }

    [Fact]
    public async Task McpToolDefinition_ThrowsWhenEndpointUnresolved()
    {
        // Construct an MCP tool definition directly with a reference expression that resolves to
        // empty (a parameter callback returning string.Empty). The public WithMcpTool overloads
        // both reject null/empty literal strings up-front, so we go through the internal ctor here.
        using var builder = TestDistributedApplicationBuilder.Create();
        var empty = builder.AddParameter("empty-endpoint", () => string.Empty);

        var def = new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"{empty.Resource}"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await def.ToProjectsAgentToolAsync(CancellationToken.None));
    }

    private static PipelineContext CreatePipelineContext(DistributedApplication app, DistributedApplicationOperation operation)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var execContext = new DistributedApplicationExecutionContext(operation);
        return new PipelineContext(model, execContext, app.Services, NullLogger.Instance, CancellationToken.None);
    }
}
