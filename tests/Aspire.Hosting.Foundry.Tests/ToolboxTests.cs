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

        // Seed the Bicep outputs that the AzureCognitiveServicesProjectResource exposes via
        // GetConnectionProperties(): the resolution path walks every env var callback on the
        // hosted agent's target resource, so any project output reachable through a `WithReference`
        // chain must be resolvable for the test to focus on the toolbox connection string assertion.
        project.Resource.Outputs["endpoint"] = "https://project.example.com";
        project.Resource.Outputs["APPLICATION_INSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=test;IngestionEndpoint=https://test.example.com/";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var envVars = await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
            builder.ExecutionContext,
            hostedAgent,
            agent.Resource,
            NullLogger<ToolboxTests>.Instance,
            cts.Token);

        Assert.Equal("https://project.example.com/toolboxes/field-tools/versions/v1/mcp?api-version=v1", envVars["ConnectionStrings__field-tools"]);
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

    [Fact]
    public void WithMcpTool_ConfigureCallback_SetsAuthorizationTokenAndHeaders()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var token = builder.AddParameter("mcp-token", () => "secret-value");
        var traceparent = builder.AddParameter("traceparent", () => "00-aaaa-bbbb-01");

        var toolbox = project.AddToolbox("field-tools")
            .WithMcpTool("inventory", "https://inventory.example.com/mcp", t =>
            {
                t.AuthorizationTokenExpression = ReferenceExpression.Create($"{token.Resource}");
                t.Headers["X-Trace-Parent"] = ReferenceExpression.Create($"{traceparent.Resource}");
                // Header name matching is case-insensitive: writing through one casing and then
                // reading through another must resolve to the same entry.
                t.Headers["x-trace-parent"] = ReferenceExpression.Create($"{traceparent.Resource}-2");
            });

        var def = Assert.IsType<FoundryToolboxMcpToolDefinition>(toolbox.Resource.Tools[0]);

        Assert.NotNull(def.AuthorizationTokenExpression);
        Assert.Single(def.Headers);
        Assert.True(def.Headers.ContainsKey("X-TRACE-PARENT"));
    }

    [Fact]
    public async Task McpToolDefinition_AuthorizationToken_EmptyValue_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var empty = builder.AddParameter("empty-token", () => string.Empty);

        var def = new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"https://inventory.example.com/mcp"))
        {
            AuthorizationTokenExpression = ReferenceExpression.Create($"{empty.Resource}")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await def.ToProjectsAgentToolAsync(CancellationToken.None));
        Assert.Contains("authorization token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task McpToolDefinition_Headers_EmptyValueOmitted_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var present = builder.AddParameter("present", () => "v1");
        var empty = builder.AddParameter("empty-header", () => string.Empty);

        var def = new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"https://inventory.example.com/mcp"));
        def.Headers["X-Present"] = ReferenceExpression.Create($"{present.Resource}");
        // Empty header values are silently dropped (vs. throwing) so optional headers backed by
        // parameters can be safely omitted in environments where the upstream value isn't set.
        def.Headers["X-Optional"] = ReferenceExpression.Create($"{empty.Resource}");

        var projectTool = await def.ToProjectsAgentToolAsync(CancellationToken.None);

        Assert.NotNull(projectTool);
    }

    [Fact]
    public async Task AddToolbox_McpTool_PublishConfigurationAnnotation_WiresDependencyOnReferencedCompute()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var mcp = builder.AddContainer("mcp", "ghcr.io/example/mcp")
            .WithHttpEndpoint(targetPort: 8080, name: "http");

        var toolbox = project.AddToolbox("field-tools")
            .WithMcpTool("inventory", mcp.GetEndpoint("http"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Materialize the toolbox's own deploy-compute step via its PipelineStepAnnotation, then
        // fabricate a stand-in deploy-compute step for the referenced container - in a real publish
        // run this would come from the AzureContainerApp pipeline. The PipelineConfigurationAnnotation
        // we're testing wires DependsOnSteps across these two via tag-based lookup, independent of who
        // produced them.
        var toolboxStepAnnotation = Assert.Single(toolbox.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var toolboxSteps = (await toolboxStepAnnotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = CreatePipelineContext(app, DistributedApplicationOperation.Publish),
            Resource = toolbox.Resource
        })).ToList();
        var toolboxDeploy = Assert.Single(toolboxSteps, s => s.Name == "deploy-field-tools");

        var containerDeploy = new PipelineStep
        {
            Name = "deploy-mcp",
            Action = _ => Task.CompletedTask,
            Resource = mcp.Resource,
            Tags = { WellKnownPipelineTags.DeployCompute },
        };

        var configCtx = new PipelineConfigurationContext
        {
            Services = app.Services,
            Model = model,
            Steps = new[] { toolboxDeploy, containerDeploy }
        };

        var configAnnotation = Assert.Single(toolbox.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        await configAnnotation.Callback(configCtx);

        Assert.Contains("deploy-mcp", toolboxDeploy.DependsOnSteps);
    }

    [Fact]
    public async Task AddToolbox_McpTool_PublishConfigurationAnnotation_LiteralEndpoint_AddsNoDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var toolbox = project.AddToolbox("field-tools")
            .WithMcpTool("inventory", "https://inventory.example.com/mcp");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var toolboxStepAnnotation = Assert.Single(toolbox.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var toolboxSteps = (await toolboxStepAnnotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = CreatePipelineContext(app, DistributedApplicationOperation.Publish),
            Resource = toolbox.Resource
        })).ToList();
        var toolboxDeploy = Assert.Single(toolboxSteps, s => s.Name == "deploy-field-tools");

        var dependsOnBefore = toolboxDeploy.DependsOnSteps.ToArray();

        var configCtx = new PipelineConfigurationContext
        {
            Services = app.Services,
            Model = model,
            Steps = new[] { toolboxDeploy }
        };

        var configAnnotation = Assert.Single(toolbox.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        await configAnnotation.Callback(configCtx);

        // A literal-URI MCP tool has no resource references to walk, so the configuration pass
        // should leave the existing dependency list untouched.
        Assert.Equal(dependsOnBefore, toolboxDeploy.DependsOnSteps);
    }

    private static PipelineContext CreatePipelineContext(DistributedApplication app, DistributedApplicationOperation operation)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var execContext = new DistributedApplicationExecutionContext(operation);
        return new PipelineContext(model, execContext, app.Services, NullLogger.Instance, CancellationToken.None);
    }
}
