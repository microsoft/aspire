// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Pipelines APIs are experimental.
#pragma warning disable ASPIREAZURE001 // AzureEnvironmentResource is experimental.

using System.ClientModel.Primitives;
using System.Net;
using System.Text;
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

        var toolbox = project.AddToolbox("field-tools", t => t.Version = "7");

        Assert.Equal("field-tools", toolbox.Resource.Name);
        Assert.Equal("7", toolbox.Resource.Version);
        Assert.Same(project.Resource, toolbox.Resource.Parent);
        Assert.IsNotAssignableFrom<IResourceWithParent>(toolbox.Resource);
        var parentRelationship = Assert.Single(
            toolbox.Resource.Annotations.OfType<ResourceRelationshipAnnotation>());
        Assert.Equal("Parent", parentRelationship.Type);
        Assert.Same(project.Resource, parentRelationship.Resource);

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
        var consumerRole = Assert.Single(
            toolbox.Resource.Annotations.OfType<ReferenceRoleAssignmentAnnotation>());
        Assert.Same(project.Resource, consumerRole.Target);
        var role = Assert.Single(consumerRole.Roles);
        Assert.Equal(FoundryResource.FoundryUserRoleDefinitionId, role.Id, ignoreCase: true);
#pragma warning restore ASPIREAZURE003
    }

    [Fact]
    public void RunAsExisting_UsesExistingToolboxOnlyInRunMode()
    {
        using var runBuilder = TestDistributedApplicationBuilder.Create();
        var runToolbox = runBuilder.AddFoundry("run-account")
            .AddProject("run-project")
            .AddToolbox("run-tools")
            .RunAsExisting();

        using var publishBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publishToolbox = publishBuilder.AddFoundry("publish-account")
            .AddProject("publish-project")
            .AddToolbox("publish-tools")
            .RunAsExisting();

        Assert.True(runToolbox.Resource.IsExisting);
        Assert.False(publishToolbox.Resource.IsExisting);
    }

    [Fact]
    public void PublishAsExisting_UsesExistingToolboxOnlyInPublishMode()
    {
        using var runBuilder = TestDistributedApplicationBuilder.Create();
        var runToolbox = runBuilder.AddFoundry("run-account")
            .AddProject("run-project")
            .AddToolbox("run-tools")
            .PublishAsExisting();

        using var publishBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publishToolbox = publishBuilder.AddFoundry("publish-account")
            .AddProject("publish-project")
            .AddToolbox("publish-tools")
            .PublishAsExisting();

        Assert.False(runToolbox.Resource.IsExisting);
        Assert.True(publishToolbox.Resource.IsExisting);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public void AsExisting_UsesExistingToolboxInBothModes(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var toolbox = builder.AddFoundry("account")
            .AddProject("project")
            .AddToolbox("field-tools")
            .AsExisting();

        Assert.True(toolbox.Resource.IsExisting);
        Assert.Empty(toolbox.Resource.Tools);
    }

    [Fact]
    public void AsExisting_AfterAISearchTool_RemovesToolAndConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("project");
        var search = builder.AddAzureSearch("search");
        var toolbox = project.AddToolbox("field-tools");
        var resourceCountWithoutConnection = builder.Resources.Count;
        toolbox.WithAISearchTool("knowledge-base", search, "docs");
        Assert.Equal(resourceCountWithoutConnection + 1, builder.Resources.Count);

        toolbox.AsExisting();

        Assert.Empty(toolbox.Resource.Tools);
        Assert.Equal(resourceCountWithoutConnection, builder.Resources.Count);
    }

    [Fact]
    public void AsExisting_BeforeAISearchTool_DoesNotAddToolOrConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("project");
        var search = builder.AddAzureSearch("search");
        var toolbox = project.AddToolbox("field-tools")
            .AsExisting();
        var resourceCount = builder.Resources.Count;

        toolbox.WithAISearchTool("knowledge-base", search, "docs");

        Assert.Empty(toolbox.Resource.Tools);
        Assert.Equal(resourceCount, builder.Resources.Count);
    }

    [Fact]
    public void WithToolMethods_AddToolDefinitions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");

        var toolbox = project.AddToolbox("field-tools")
            .WithDescription("Tools for field technicians.")
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
        var toolbox = project.AddToolbox("field-tools", t => t.Version = "7");

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
            && kvp.Value == "{my-project.outputs.endpoint}/toolboxes/field-tools/versions/7/mcp?api-version=v1");
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
        var toolbox = project.AddToolbox("field-tools", t => t.Version = "7");

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

        Assert.Equal("https://project.example.com/toolboxes/field-tools/versions/7/mcp?api-version=v1", envVars["ConnectionStrings__field-tools"]);
    }

    [Fact]
    public async Task AddToolbox_RegistersPublishModeDeployStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var toolbox = project.AddToolbox("field-tools");

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
        var toolbox = project.AddToolbox("field-tools");

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
        Assert.Contains(AzureEnvironmentResource.PrepareResourcesStepName, beforeStart.DependsOnSteps);
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
        var json = ModelReaderWriter.Write(
            projectTool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default);
        Assert.Equal("""{"type":"web_search","name":"web-search"}""", json.ToString());
    }

    [Fact]
    public async Task WebSearchToolDefinition_IncludesDescriptionWhenConfigured()
    {
        var tool = new FoundryToolboxWebSearchToolDefinition(
            "web-search",
            "Search the public web.");

        var projectTool = await tool.ToProjectsAgentToolAsync(CancellationToken.None);

        var json = ModelReaderWriter.Write(
            projectTool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default);
        Assert.Equal(
            """{"type":"web_search","name":"web-search","description":"Search the public web."}""",
            json.ToString());
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
        var json = ModelReaderWriter.Write(
            projectTool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default);
        Assert.Contains("\"name\":\"knowledge-base\"", json.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessProbe_RetriesAndFollowsToolsListPagination()
    {
        var initialize = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26"}}""",
                Encoding.UTF8,
                "application/json")
        };
        initialize.Headers.Add("Mcp-Session-Id", "session-1");
        using var handler = new SequenceHttpMessageHandler(
            initialize,
            new HttpResponseMessage(HttpStatusCode.Accepted),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            CreateJsonResponse("""{"jsonrpc":"2.0","id":3,"result":{"tools":[{"name":"other"}],"nextCursor":"page-2"}}"""),
            CreateJsonResponse("""{"jsonrpc":"2.0","id":4,"result":{"tools":[{"name":"knowledge-base"}]}}"""));
        using var client = new HttpClient(handler);

        var tools = await new FoundryToolboxReadinessProbe(
            client,
            timeout: TimeSpan.FromSeconds(1),
            retryDelay: TimeSpan.Zero)
            .WaitForToolsAsync(
                new Uri("https://project.example.com/toolboxes/field-tools/mcp?api-version=v1"),
                "token",
                ["knowledge-base"],
                CancellationToken.None);

        Assert.Equal(2, tools.Count);
        Assert.Contains("knowledge-base", tools);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Contains("\"method\":\"initialize\"", request.Content, StringComparison.Ordinal);
                Assert.Null(request.SessionId);
                Assert.Null(request.ProtocolVersion);
            },
            request =>
            {
                Assert.Contains("\"method\":\"notifications/initialized\"", request.Content, StringComparison.Ordinal);
                Assert.Equal("session-1", request.SessionId);
                Assert.Equal("2025-03-26", request.ProtocolVersion);
            },
            request => Assert.Equal("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""", request.Content),
            request => Assert.Equal("""{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}""", request.Content),
            request => Assert.Equal("""{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{"cursor":"page-2"}}""", request.Content));
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

        var json = ModelReaderWriter.Write(
            projectTool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default);
        Assert.Equal(
            """{"type":"mcp","server_label":"inventory","server_url":"https://inventory.example.com/mcp"}""",
            json.ToString());
    }

    [Fact]
    public async Task McpToolDefinition_IncludesServerMetadataAndGlobalApproval()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var toolbox = project.AddToolbox("field-tools")
            .WithMcpTool(
                "inventory",
                "https://inventory.example.com/mcp",
                new FoundryToolboxMcpToolOptions
                {
                    ServerLabel = "inventory-server",
                    ServerDescription = "Inventory MCP server.",
                    ApprovalPolicy = new()
                    {
                        Global = FoundryToolboxMcpGlobalApprovalMode.Always
                    }
                });
        var definition = Assert.IsType<FoundryToolboxMcpToolDefinition>(
            Assert.Single(toolbox.Resource.Tools));

        var projectTool = await definition.ToProjectsAgentToolAsync(CancellationToken.None);

        var json = ModelReaderWriter.Write(
            projectTool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default);
        Assert.Equal(
            """{"type":"mcp","server_label":"inventory-server","server_url":"https://inventory.example.com/mcp","server_description":"Inventory MCP server.","require_approval":"always"}""",
            json.ToString());
    }

    [Fact]
    public async Task McpToolDefinition_IncludesCanonicalCustomApproval()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var toolbox = project.AddToolbox("field-tools")
            .WithMcpTool(
                "inventory",
                "https://inventory.example.com/mcp",
                new FoundryToolboxMcpToolOptions
                {
                    ApprovalPolicy = new()
                    {
                        Always = new()
                        {
                            ToolNames = ["write", "delete", "write"],
                            ReadOnly = false
                        },
                        Never = new()
                        {
                            ToolNames = ["read"],
                            ReadOnly = true
                        }
                    }
                });
        var definition = Assert.IsType<FoundryToolboxMcpToolDefinition>(
            Assert.Single(toolbox.Resource.Tools));

        var projectTool = await definition.ToProjectsAgentToolAsync(CancellationToken.None);

        var json = ModelReaderWriter.Write(
            projectTool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default);
        Assert.Equal(
            """{"type":"mcp","server_label":"inventory","server_url":"https://inventory.example.com/mcp","require_approval":{"always":{"tool_names":["delete","write"],"read_only":false},"never":{"tool_names":["read"],"read_only":true}}}""",
            json.ToString());
    }

    [Fact]
    public void WithMcpTool_RejectsMixedGlobalAndCustomApproval()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var exception = Assert.Throws<ArgumentException>(
            () => project.AddToolbox("field-tools")
                .WithMcpTool(
                    "inventory",
                    "https://inventory.example.com/mcp",
                    new FoundryToolboxMcpToolOptions
                    {
                        ApprovalPolicy = new()
                        {
                            Global = FoundryToolboxMcpGlobalApprovalMode.Always,
                            Never = new()
                            {
                                ToolNames = ["read"]
                            }
                        }
                    }));

        Assert.Contains(
            "cannot be combined with custom filters",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithMcpTool_RejectsEmptyCustomApprovalFilter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var exception = Assert.Throws<ArgumentException>(
            () => project.AddToolbox("field-tools")
                .WithMcpTool(
                    "inventory",
                    "https://inventory.example.com/mcp",
                    new FoundryToolboxMcpToolOptions
                    {
                        ApprovalPolicy = new()
                        {
                            Always = new()
                        }
                    }));

        Assert.Contains(
            "must specify at least one tool name or a read-only value",
            exception.Message,
            StringComparison.Ordinal);
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
    public async Task McpToolDefinition_ThrowsWhenEndpointIsNotHttps()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var def = new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"http://inventory.example.com/mcp"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await def.ToProjectsAgentToolAsync(CancellationToken.None));
        Assert.Contains("Foundry-reachable absolute HTTPS endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpToolDefinition_ThrowsWhenEndpointIsLoopback()
    {
        var def = new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"https://localhost:7443/mcp"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await def.ToProjectsAgentToolAsync(CancellationToken.None));

        Assert.Contains("Foundry-reachable", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://inventory.localhost:7443/mcp")]
    [InlineData("https://user:password@inventory.example.com/mcp")]
    public async Task McpToolDefinition_ThrowsWhenEndpointIsNotPubliclyReachable(string endpoint)
    {
        var def = new FoundryToolboxMcpToolDefinition(
            "inventory",
            ReferenceExpression.Create($"{endpoint}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await def.ToProjectsAgentToolAsync(CancellationToken.None));

        Assert.Contains("Foundry-reachable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithMcpTool_ThrowsImmediatelyWhenLiteralEndpointIsNotHttps()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var exception = Assert.Throws<ArgumentException>(
            () => project.AddToolbox("field-tools")
                .WithMcpTool("inventory", "http://inventory.example.com/mcp"));

        Assert.Equal("endpoint", exception.ParamName);
    }

    [Fact]
    public void WithMcpTool_ThrowsImmediatelyWhenLiteralEndpointIsLoopback()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var exception = Assert.Throws<ArgumentException>(
            () => project.AddToolbox("field-tools")
                .WithMcpTool("inventory", "https://localhost:7443/mcp"));

        Assert.Equal("endpoint", exception.ParamName);
    }

    [Theory]
    [InlineData("https://inventory.localhost:7443/mcp")]
    [InlineData("https://user:password@inventory.example.com/mcp")]
    public void WithMcpTool_ThrowsImmediatelyWhenLiteralEndpointIsNotPubliclyReachable(string endpoint)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var exception = Assert.Throws<ArgumentException>(
            () => project.AddToolbox("field-tools")
                .WithMcpTool("inventory", endpoint));

        Assert.Equal("endpoint", exception.ParamName);
    }

    [Fact]
    public void WithAISearchTool_UsesDeterministicConnectionName()
    {
        using var firstBuilder = TestDistributedApplicationBuilder.Create();
        var firstProject = firstBuilder.AddFoundry("account")
            .AddProject("my-project");
        var firstSearch = firstBuilder.AddAzureSearch("search");
        var firstToolbox = firstProject.AddToolbox("field-tools")
            .WithAISearchTool("knowledge-base", firstSearch, "docs");

        using var secondBuilder = TestDistributedApplicationBuilder.Create();
        var secondProject = secondBuilder.AddFoundry("account")
            .AddProject("my-project");
        var secondSearch = secondBuilder.AddAzureSearch("search");
        var secondToolbox = secondProject.AddToolbox("field-tools")
            .WithAISearchTool("knowledge-base", secondSearch, "docs");

        var firstDefinition = Assert.IsType<FoundryToolboxAzureAISearchToolDefinition>(
            Assert.Single(firstToolbox.Resource.Tools));
        var secondDefinition = Assert.IsType<FoundryToolboxAzureAISearchToolDefinition>(
            Assert.Single(secondToolbox.Resource.Tools));

        Assert.Equal(firstDefinition.Connection.Name, secondDefinition.Connection.Name);
    }

    [Fact]
    public void WithAISearchTool_EmitsSearchRoleAssignmentsForProjectIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");
        var toolbox = project.AddToolbox("field-tools")
            .WithAISearchTool("knowledge-base", search, "docs");
        var definition = Assert.IsType<FoundryToolboxAzureAISearchToolDefinition>(
            Assert.Single(toolbox.Resource.Tools));

        var bicep = definition.Connection.GetBicepTemplateString();

        Assert.Contains("8ebe5a00-799e-43f5-93ac-243d3dce84a7", bicep, StringComparison.Ordinal);
        Assert.Contains("7ca78c08-252a-4471-8644-bb5ff32d4ba0", bicep, StringComparison.Ordinal);
        Assert.Contains("principalId", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAISearchTool_RejectsEmptyIndexName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var search = builder.AddAzureSearch("search");

        var exception = Assert.Throws<ArgumentException>(
            () => project.AddToolbox("field-tools")
                .WithAISearchTool("knowledge-base", search, string.Empty));

        Assert.Equal("indexName", exception.ParamName);
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
        var replacement = new ContainerResource(mcp.Resource.Name);
        model.Resources.Remove(mcp.Resource);
        model.Resources.Add(replacement);

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
            Resource = replacement,
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

    [Fact]
    public async Task AsExisting_PublishConfigurationAnnotation_AddsNoToolDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");
        var mcp = builder.AddContainer("mcp", "ghcr.io/example/mcp")
            .WithHttpEndpoint(targetPort: 8080, name: "http");
        var toolbox = project.AddToolbox("field-tools")
            .WithMcpTool("inventory", mcp.GetEndpoint("http"))
            .AsExisting();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var toolboxStepAnnotation = Assert.Single(toolbox.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var toolboxSteps = (await toolboxStepAnnotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = CreatePipelineContext(app, DistributedApplicationOperation.Publish),
            Resource = toolbox.Resource
        })).ToList();
        var toolboxDeploy = Assert.Single(toolboxSteps, step => step.Name == "deploy-field-tools");
        var mcpDeploy = new PipelineStep
        {
            Name = "deploy-mcp",
            Action = _ => Task.CompletedTask,
            Resource = mcp.Resource,
            Tags = { WellKnownPipelineTags.DeployCompute },
        };
        var dependenciesBefore = toolboxDeploy.DependsOnSteps.ToArray();

        var configurationAnnotation = Assert.Single(
            toolbox.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        await configurationAnnotation.Callback(new PipelineConfigurationContext
        {
            Services = app.Services,
            Model = model,
            Steps = [toolboxDeploy, mcpDeploy]
        });

        Assert.Equal(dependenciesBefore, toolboxDeploy.DependsOnSteps);
    }

    [Fact]
    public async Task WaitForMcpResourceAsync_ThrowsWhenDependencyFailsToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var mcp = builder.AddContainer("mcp", "ghcr.io/example/mcp");
        using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(mcp.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.FailedToStart
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => FoundryToolboxResource.WaitForMcpResourceAsync(
                notifications,
                mcp.Resource,
                CancellationToken.None));

        Assert.Contains("terminal state 'FailedToStart'", exception.Message, StringComparison.Ordinal);
    }

    private static PipelineContext CreatePipelineContext(DistributedApplication app, DistributedApplicationOperation operation)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var execContext = new DistributedApplicationExecutionContext(operation);
        return new PipelineContext(model, execContext, app.Services, NullLogger.Instance, CancellationToken.None);
    }

    private static HttpResponseMessage CreateJsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
