// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AAIP001 // Toolbox APIs are experimental.

using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Resources;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Azure;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for deploying Aspire applications with Foundry Hosted Agents.
/// </summary>
public sealed class FoundryHostedAgentDeploymentTests(ITestOutputHelper output)
{
    // Timeout set to 45 minutes to allow for Azure AI Foundry provisioning and model deployment.
    // Foundry deployments can take longer than standard ACA due to AI resource provisioning.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    // This scenario deploys twice; its two phase limits total 50 minutes before setup and inspection.
    private static readonly TimeSpan s_toolboxTestTimeout = TimeSpan.FromMinutes(70);

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/16330")]
    public async Task DeployFoundryHostedAgentToAzure()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployFoundryHostedAgentToAzureCore(cancellationToken);
    }

    [Fact]
    public async Task DeployFoundryToolboxToAzure()
    {
        using var cts = new CancellationTokenSource(s_toolboxTestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Azure subscription not configured. Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION.");
        }

        if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
        {
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                Assert.Fail("Azure authentication not available in CI. Check OIDC configuration.");
            }
            else
            {
                Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
            }
        }

        var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("foundry-toolbox");
        const string projectName = "FoundryToolbox";

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Foundry");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            var appHostFilePath = Path.Combine(
                workspace.WorkspaceRoot.FullName,
                projectName,
                $"{projectName}.AppHost",
                "AppHost.cs");
            var appHostContent = File.ReadAllText(appHostFilePath);
            appHostContent = "using Aspire.Hosting.Foundry;\n" + appHostContent;
            appHostContent = appHostContent.Replace(
                "builder.Build().Run();",
                """
                var foundry = builder.AddFoundry("aif-myfoundry");
                var foundryProject = foundry.AddProject("proj-myproject");
                var search = builder.AddAzureSearch("search");

                foundryProject.AddToolbox("field-tools")
                    .WithDescription("Tools for field technicians.")
                    .WithWebSearchTool("web-search", "Search the public web.")
                    .WithMcpTool(
                        "microsoft-learn",
                        "https://learn.microsoft.com/api/mcp",
                        new FoundryToolboxMcpToolOptions
                        {
                            ServerDescription = "Search Microsoft Learn.",
                            ApprovalPolicy = new()
                            {
                                Global = FoundryToolboxMcpGlobalApprovalMode.Always
                            }
                        })
                    .WithAISearchTool(
                        "knowledge-base",
                        search,
                        "docs",
                        "Search the internal knowledge base.");

                builder.Build().Run();
                """);
            File.WriteAllText(appHostFilePath, appHostContent);

            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=swedencentral && export AZURE__RESOURCEGROUP={resourceGroupName}" +
                $" && export AZURE__SUBSCRIPTIONID={subscriptionId}" +
                " && export AZURE__TENANTID=$(az account show --query tenantId -o tsv)");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The first deployment creates a Toolbox version; the second must reconcile to the same
            // immutable version rather than producing another one.
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(35), counter: counter);
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            var credential = AzureAuthenticationHelpers.GetAzureCredential();
            var resources = await GetToolboxTestResourcesAsync(
                subscriptionId,
                resourceGroupName,
                credential,
                cancellationToken);
            await EnsureSearchIndexExistsAsync(resources, credential, cancellationToken);
            var firstDeployment = await InspectToolboxAsync(
                resources.ProjectEndpoint,
                credential,
                cancellationToken);
            AssertToolboxDefinition(firstDeployment);
            Assert.Equal(1, firstDeployment.VersionCount);

            var discoveredTools = await ListToolboxToolsAsync(
                resources.ProjectEndpoint,
                credential,
                cancellationToken);
            Assert.Contains("knowledge-base", discoveredTools);

            // The first pipeline banner remains visible, so clear it before waiting for the redeploy.
            await auto.TypeAsync("clear");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(15), counter: counter);
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            var secondDeployment = await InspectToolboxAsync(
                resources.ProjectEndpoint,
                credential,
                cancellationToken);
            Assert.Equal(firstDeployment.DefaultVersion, secondDeployment.DefaultVersion);
            Assert.Equal(firstDeployment.ConfigurationHash, secondDeployment.ConfigurationHash);
            Assert.Equal(firstDeployment.VersionCount, secondDeployment.VersionCount);

            await auto.TypeAsync(
                $"az group show -n \"{resourceGroupName}\" --query name -o tsv");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(resourceGroupName, timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployFoundryToolboxToAzure),
                resourceGroupName,
                new Dictionary<string, string>(),
                DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployFoundryToolboxToAzure),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);
            throw;
        }
        finally
        {
            TriggerCleanupResourceGroup(resourceGroupName, output);
            DeploymentReporter.ReportCleanupStatus(
                resourceGroupName,
                success: true,
                "Cleanup triggered (fire-and-forget)");
        }
    }

    private static async Task<ToolboxTestResources> GetToolboxTestResourcesAsync(
        string subscriptionId,
        string resourceGroupName,
        TokenCredential credential,
        CancellationToken cancellationToken)
    {
        var managementToken = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            cancellationToken);
        using var client = new HttpClient();
        var resourcesUri = new Uri(
            $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/resources?api-version=2021-04-01");
        using var resourcesRequest = new HttpRequestMessage(HttpMethod.Get, resourcesUri);
        resourcesRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", managementToken.Token);
        using var resourcesResponse = await client.SendAsync(resourcesRequest, cancellationToken);
        resourcesResponse.EnsureSuccessStatusCode();
        using var resourcesDocument = JsonDocument.Parse(
            await resourcesResponse.Content.ReadAsStringAsync(cancellationToken));

        var resources = resourcesDocument.RootElement.GetProperty("value").EnumerateArray().ToArray();
        var project = resources.Single(resource =>
            string.Equals(
                resource.GetProperty("type").GetString(),
                "Microsoft.CognitiveServices/accounts/projects",
                StringComparison.OrdinalIgnoreCase));
        var search = resources.Single(resource =>
            string.Equals(
                resource.GetProperty("type").GetString(),
                "Microsoft.Search/searchServices",
                StringComparison.OrdinalIgnoreCase));

        var projectId = project.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("The deployed Foundry project did not have a resource ID.");
        using var projectRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://management.azure.com{projectId}?api-version=2025-06-01");
        projectRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", managementToken.Token);
        using var projectResponse = await client.SendAsync(projectRequest, cancellationToken);
        projectResponse.EnsureSuccessStatusCode();
        using var projectDocument = JsonDocument.Parse(
            await projectResponse.Content.ReadAsStringAsync(cancellationToken));
        var projectEndpoint = projectDocument.RootElement
            .GetProperty("properties")
            .GetProperty("endpoints")
            .GetProperty("AI Foundry API")
            .GetString();

        return new(
            new Uri(projectEndpoint
                ?? throw new InvalidOperationException("The deployed Foundry project did not expose an endpoint.")),
            search.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("The deployed Search service did not have a name."),
            search.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("The deployed Search service did not have a resource ID."));
    }

    private static async Task EnsureSearchIndexExistsAsync(
        ToolboxTestResources resources,
        TokenCredential credential,
        CancellationToken cancellationToken)
    {
        var managementToken = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            cancellationToken);
        var principalId = GetPrincipalId(managementToken.Token);
        using var client = new HttpClient();
        using var roleAssignmentRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"https://management.azure.com{resources.SearchResourceId}/providers/Microsoft.Authorization/roleAssignments/{Guid.NewGuid():D}?api-version=2022-04-01");
        roleAssignmentRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", managementToken.Token);
        roleAssignmentRequest.Content = JsonContent.Create(new
        {
            properties = new
            {
                roleDefinitionId =
                    $"/subscriptions/{AzureAuthenticationHelpers.GetSubscriptionId()}/providers/Microsoft.Authorization/roleDefinitions/7ca78c08-252a-4471-8644-bb5ff32d4ba0",
                principalId
            }
        });
        using var roleAssignmentResponse = await client.SendAsync(
            roleAssignmentRequest,
            cancellationToken);
        roleAssignmentResponse.EnsureSuccessStatusCode();

        var indexClient = new SearchIndexClient(
            new Uri($"https://{resources.SearchServiceName}.search.windows.net"),
            credential);
        var index = new SearchIndex("docs")
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String)
                {
                    IsKey = true,
                    IsFilterable = true
                },
                new SearchableField("content")
            }
        };
        using var rbacPropagationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        rbacPropagationCancellation.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            while (true)
            {
                try
                {
                    await indexClient.CreateOrUpdateIndexAsync(
                        index,
                        cancellationToken: rbacPropagationCancellation.Token);
                    break;
                }
                catch (RequestFailedException ex) when (ex.Status == 403)
                {
                    // Azure RBAC propagation can take up to ten minutes after the test principal
                    // receives Search Service Contributor on the newly provisioned service.
                    // See https://learn.microsoft.com/azure/role-based-access-control/troubleshooting#role-assignment-changes-are-not-being-detected.
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        rbacPropagationCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Search Service Contributor did not propagate within ten minutes.");
        }
    }

    private static string GetPrincipalId(string accessToken)
    {
        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            throw new InvalidOperationException("The Azure access token was not a JWT.");
        }

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return document.RootElement.GetProperty("oid").GetString()
            ?? throw new InvalidOperationException("The Azure access token did not contain an object ID.");
    }

    private static async Task<ToolboxDeploymentSnapshot> InspectToolboxAsync(
        Uri projectEndpoint,
        TokenCredential credential,
        CancellationToken cancellationToken)
    {
        var options = new AIProjectClientOptions();
        options.AddPolicy(new ToolboxFeaturesPolicy(), PipelinePosition.PerCall);
        var projectClient = new AIProjectClient(projectEndpoint, credential, options);
        var toolboxes = projectClient.AgentAdministrationClient.GetAgentToolboxes();
        var toolbox = (await toolboxes.GetToolboxAsync("field-tools", cancellationToken)).Value;
        var versions = new List<ToolboxVersion>();
        await foreach (var version in toolboxes.GetToolboxVersionsAsync(
            "field-tools",
            cancellationToken: cancellationToken))
        {
            versions.Add(version);
        }

        var defaultVersion = (await toolboxes.GetToolboxVersionAsync(
            "field-tools",
            toolbox.DefaultVersion,
            cancellationToken)).Value;
        var configurationHash = defaultVersion.Metadata["aspire-configuration-hash"];
        var serializedTools = defaultVersion.Tools
            .Select(SerializeTool)
            .ToArray();

        return new(
            toolbox.DefaultVersion,
            configurationHash,
            versions.Count,
            defaultVersion.Description,
            new Dictionary<string, string>(defaultVersion.Metadata, StringComparer.Ordinal),
            serializedTools);
    }

    private static JsonElement SerializeTool(ProjectsAgentTool tool)
    {
        using var document = JsonDocument.Parse(ModelReaderWriter.Write(
            tool,
            ModelReaderWriterOptions.Json,
            AzureAIProjectsAgentsContext.Default));

        return document.RootElement.Clone();
    }

    private static void AssertToolboxDefinition(ToolboxDeploymentSnapshot snapshot)
    {
        Assert.Equal("Tools for field technicians.", snapshot.Description);
        Assert.Equal("Aspire.Hosting.Foundry", snapshot.Metadata["aspire-managed-by"]);
        Assert.Equal("1", snapshot.Metadata["aspire-schema-version"]);
        Assert.Equal(3, snapshot.Tools.Count);

        var webSearch = snapshot.Tools.Single(tool =>
            tool.GetProperty("type").GetString() == "web_search");
        Assert.Equal("web-search", webSearch.GetProperty("name").GetString());
        Assert.Equal("Search the public web.", webSearch.GetProperty("description").GetString());

        var mcp = snapshot.Tools.Single(tool =>
            tool.GetProperty("type").GetString() == "mcp");
        Assert.Equal("microsoft-learn", mcp.GetProperty("server_label").GetString());
        Assert.Equal(
            "https://learn.microsoft.com/api/mcp",
            mcp.GetProperty("server_url").GetString());
        Assert.Equal("Search Microsoft Learn.", mcp.GetProperty("server_description").GetString());
        Assert.Equal("always", mcp.GetProperty("require_approval").GetString());

        var search = snapshot.Tools.Single(tool =>
            tool.GetProperty("type").GetString() == "azure_ai_search");
        Assert.Equal(
            "docs",
            search.GetProperty("azure_ai_search")
                .GetProperty("indexes")[0]
                .GetProperty("index_name")
                .GetString());
    }

    private static async Task<IReadOnlyList<string>> ListToolboxToolsAsync(
        Uri projectEndpoint,
        TokenCredential credential,
        CancellationToken cancellationToken)
    {
        var accessToken = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]),
            cancellationToken);
        var endpoint = new Uri(
            projectEndpoint,
            $"{projectEndpoint.AbsolutePath.TrimEnd('/')}/toolboxes/field-tools/mcp?api-version=v1");
        using var client = new HttpClient();

        var initialize = await SendMcpRequestAsync(
            client,
            endpoint,
            accessToken.Token,
            sessionId: null,
            protocolVersion: null,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"Aspire.Deployment.EndToEnd.Tests","version":"1.0"}}}
            """,
            cancellationToken);
        var negotiatedProtocol = initialize.Result
            .GetProperty("protocolVersion")
            .GetString();
        Assert.False(string.IsNullOrEmpty(negotiatedProtocol));

        await SendMcpRequestAsync(
            client,
            endpoint,
            accessToken.Token,
            initialize.SessionId,
            negotiatedProtocol,
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""",
            cancellationToken);
        using var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryCancellation.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var requestId = 2;
            while (true)
            {
                var toolNames = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                var retryDiscovery = false;
                do
                {
                    McpResponse tools;
                    try
                    {
                        tools = await SendMcpRequestAsync(
                            client,
                            endpoint,
                            accessToken.Token,
                            initialize.SessionId,
                            negotiatedProtocol,
                            CreateToolsListPayload(requestId++, cursor),
                            discoveryCancellation.Token);
                    }
                    catch (HttpRequestException ex)
                        when (ex.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                    {
                        // Foundry documents HTTP 500 from tools/list as transient while tool
                        // discovery converges immediately after Toolbox provisioning.
                        // See https://learn.microsoft.com/azure/foundry/agents/how-to/tools/toolbox#troubleshooting.
                        retryDiscovery = true;
                        break;
                    }

                    foreach (var tool in tools.Result.GetProperty("tools").EnumerateArray())
                    {
                        toolNames.Add(tool.GetProperty("name").GetString()
                            ?? throw new InvalidOperationException("A discovered MCP tool did not have a name."));
                    }

                    cursor = tools.Result.TryGetProperty("nextCursor", out var nextCursor)
                        ? nextCursor.GetString()
                        : null;
                }
                while (!string.IsNullOrEmpty(cursor));

                if (!retryDiscovery && toolNames.Contains("knowledge-base"))
                {
                    return toolNames.ToArray();
                }

                // Toolbox tool discovery is eventually consistent immediately after provisioning.
                await Task.Delay(TimeSpan.FromSeconds(5), discoveryCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Foundry Toolbox did not discover the 'knowledge-base' Azure AI Search tool within two minutes.");
        }
    }

    private static string CreateToolsListPayload(int requestId, string? cursor)
    {
        // MCP tools/list pagination sends an opaque cursor in the next request:
        //   {"jsonrpc":"2.0","id":3,"method":"tools/list","params":{"cursor":"opaque"}}
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", requestId);
            writer.WriteString("method", "tools/list");
            writer.WriteStartObject("params");
            if (cursor is not null)
            {
                writer.WriteString("cursor", cursor);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<McpResponse> SendMcpRequestAsync(
        HttpClient client,
        Uri endpoint,
        string accessToken,
        string? sessionId,
        string? protocolVersion,
        string payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }
        if (!string.IsNullOrEmpty(protocolVersion))
        {
            request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        }
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var requestDocument = JsonDocument.Parse(payload);
        var expectedId = requestDocument.RootElement.TryGetProperty("id", out var requestId)
            ? requestId.GetInt32()
            : (int?)null;

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseSessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.Single()
            : sessionId;

        if (string.IsNullOrWhiteSpace(responsePayload) || expectedId is null)
        {
            return new(default, responseSessionId);
        }

        // Streamable HTTP may return either one JSON document or SSE frames such as:
        //   event: message
        //   data: {"jsonrpc":"2.0","id":1,"result":{...}}
        var responseMessages = responsePayload.TrimStart().StartsWith('{')
            ? [responsePayload]
            : responsePayload.Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].Trim());
        JsonElement? matchingResponse = null;
        foreach (var responseMessage in responseMessages)
        {
            using var candidate = JsonDocument.Parse(responseMessage);
            if (candidate.RootElement.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == expectedId)
            {
                matchingResponse = candidate.RootElement.Clone();
                break;
            }
        }

        if (matchingResponse is null)
        {
            throw new InvalidOperationException(
                $"The MCP response did not contain JSON-RPC response ID {expectedId}.");
        }

        if (matchingResponse.Value.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"MCP request failed: {error.GetRawText()}");
        }

        var result = matchingResponse.Value.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : default;
        return new(result, responseSessionId);
    }

    private sealed record ToolboxTestResources(
        Uri ProjectEndpoint,
        string SearchServiceName,
        string SearchResourceId);

    private sealed record ToolboxDeploymentSnapshot(
        string DefaultVersion,
        string ConfigurationHash,
        int VersionCount,
        string Description,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<JsonElement> Tools);

    private sealed record McpResponse(JsonElement Result, string? SessionId);

    private sealed class ToolboxFeaturesPolicy : PipelinePolicy
    {
        public override void Process(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            message.Request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            message.Request.Headers.Add("Foundry-Features", "Toolboxes=V1Preview");
            return ProcessNextAsync(message, pipeline, currentIndex);
        }
    }

    private async Task DeployFoundryHostedAgentToAzureCore(CancellationToken cancellationToken)
    {
        // Validate prerequisites
        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Azure subscription not configured. Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION.");
        }

        if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
        {
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                Assert.Fail("Azure authentication not available in CI. Check OIDC configuration.");
            }
            else
            {
                Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
            }
        }

        var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var deploymentUrls = new Dictionary<string, string>();
        // Generate a unique resource group name with pattern: e2e-[testcasename]-[runid]-[attempt]
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("foundry-agent");
        var projectName = "FoundryAgent";

        output.WriteLine($"Test: {nameof(DeployFoundryHostedAgentToAzure)}");
        output.WriteLine($"Project Name: {projectName}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // Step 1: Prepare environment
            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            // Step 2: Set up CLI environment
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            // Step 3: Create starter project using aspire new (for basic AppHost scaffold)
            output.WriteLine("Step 3: Creating starter project...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            // Step 4: Navigate to project directory
            output.WriteLine("Step 4: Navigating to project directory...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 5: Add Aspire.Hosting.Foundry package to the AppHost
            output.WriteLine("Step 5: Adding Foundry hosting package...");
            await auto.TypeAsync("aspire add Aspire.Hosting.Foundry");
            await auto.EnterAsync();

            // aspire add may show a version selection prompt
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Step 6: Create a dedicated .NET hosted agent project
            // WithComputeEnvironment requires a proper agent application, not a standard apiservice.
            output.WriteLine("Step 6: Creating .NET hosted agent project...");
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var hostedAgentDir = Path.Combine(projectDir, "DotNetHostedAgent");
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");

            Directory.CreateDirectory(hostedAgentDir);

            // Write minimal hosted agent .csproj.
            // The package set and explicit versions below must stay in sync with
            // playground/FoundryAgents/DotNetHostedAgent/DotNetHostedAgent.csproj.
            // This project is materialized at test runtime outside the repo, so it does not
            // participate in central package management; the playground's `VersionOverride`
            // values become explicit `Version` values here. Update both files together.
            File.WriteAllText(Path.Combine(hostedAgentDir, "DotNetHostedAgent.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <!-- Suppress experimental API warnings from Agent Framework Foundry packages -->
                    <NoWarn>$(NoWarn);OPENAI001;MAIF001;MAAI001</NoWarn>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Azure.AI.Projects" Version="2.1.0-beta.3" />
                    <PackageReference Include="Azure.Identity" Version="1.21.0" />
                    <PackageReference Include="Microsoft.Agents.AI.Foundry.Hosting" Version="1.12.0-preview.260629.1" />
                    <PackageReference Include="Microsoft.Extensions.AI" Version="10.7.0" />
                    <PackageReference Include="ModelContextProtocol" Version="1.1.0" />
                    <PackageReference Include="Azure.Core" Version="1.59.0" />
                  </ItemGroup>
                </Project>
                """);

            // Write minimal hosted agent Program.cs.
            // Mirrors playground/FoundryAgents/DotNetHostedAgent/Program.cs: reads the
            // Foundry project + chat connection strings, builds an AIProjectClient-backed
            // AIAgent, and hosts it as a Foundry Responses endpoint on DEFAULT_AD_PORT.
            File.WriteAllText(Path.Combine(hostedAgentDir, "Program.cs"), """
                using System.ComponentModel;
                using System.Data.Common;
                using Azure.AI.Projects;
                using Azure.Identity;
                using Microsoft.Agents.AI;
                using Microsoft.Agents.AI.Foundry.Hosting;
                using Microsoft.Extensions.AI;

                string projectConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__projmyproject")
                    ?? throw new InvalidOperationException("ConnectionStrings__projmyproject is not set.");

                string chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__chat")
                    ?? throw new InvalidOperationException("ConnectionStrings__chat is not set.");

                DbConnectionStringBuilder projectConnectionBuilder = new() { ConnectionString = projectConnectionString };
                DbConnectionStringBuilder chatConnectionBuilder = new() { ConnectionString = chatConnectionString };

                string projectEndpoint = GetRequiredConnectionValue(projectConnectionBuilder, "Endpoint");
                string deploymentName = GetRequiredConnectionValue(chatConnectionBuilder, "Deployment");

                if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out Uri? projectUri) || projectUri is null)
                {
                    throw new InvalidOperationException("ConnectionStrings__projmyproject contains an invalid Endpoint value.");
                }

                [Description("Get a weather forecast")]
                string GetWeatherForecast() => "Sunny, 25°C";

                DefaultAzureCredential credential = new();

                AIAgent agent = new AIProjectClient(projectUri, credential)
                    .AsAIAgent(
                        model: deploymentName,
                        name: "WeatherAgent",
                        instructions: "You are the Weather Intelligence Agent.",
                        tools: [AIFunctionFactory.Create(GetWeatherForecast)]);

                // Bind to the port allocated by Aspire via the DEFAULT_AD_PORT environment variable.
                string port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

                var builder = WebApplication.CreateBuilder(args);
                builder.WebHost.UseUrls($"http://+:{port}");
                builder.Services.AddFoundryResponses(agent);

                var app = builder.Build();

                app.MapFoundryResponses();
                app.MapGet("/liveness", () => Results.Ok("Healthy"));
                app.MapGet("/readiness", () => Results.Ok("Ready"));

                app.Run();

                static string GetRequiredConnectionValue(DbConnectionStringBuilder connectionBuilder, string key)
                {
                    if (!connectionBuilder.TryGetValue(key, out object? rawValue) || rawValue is null)
                    {
                        throw new InvalidOperationException($"Connection string is missing '{key}'.");
                    }

                    string? value = rawValue.ToString();

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new InvalidOperationException($"Connection string has an empty '{key}' value.");
                    }

                    return value;
                }
                """);

            // Write Dockerfile for the hosted agent
            File.WriteAllText(Path.Combine(hostedAgentDir, ".dockerignore"), """
                bin/
                obj/
                """);

            output.WriteLine($"Created hosted agent project at: {hostedAgentDir}");

            // Step 7: Add hosted agent to the solution and add project reference from AppHost
            output.WriteLine("Step 7: Adding hosted agent to solution...");
            await auto.TypeAsync($"dotnet sln add DotNetHostedAgent/DotNetHostedAgent.csproj");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync($"dotnet add {projectName}.AppHost/{projectName}.AppHost.csproj reference DotNetHostedAgent/DotNetHostedAgent.csproj");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Step 8: Modify AppHost.cs to wire up Foundry + hosted agent
            // Replace the standard starter template AppHost with Foundry-based configuration.
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");
            output.WriteLine($"Modifying AppHost.cs at: {appHostFilePath}");

            var appHostContent = File.ReadAllText(appHostFilePath);

            // Add the Foundry using directive
            appHostContent = "using Aspire.Hosting.Foundry;\n" + appHostContent;

            // Insert Foundry resources before builder.Build().Run();
            appHostContent = appHostContent.Replace(
                "builder.Build().Run();",
                """
                var foundry = builder.AddFoundry("aif-myfoundry");
                var foundryProject = foundry.AddProject("proj-myproject");
                var chat = foundryProject.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

                builder.AddProject<Projects.DotNetHostedAgent>("dotnet-hosted-agent")
                    .WithReference(chat).WaitFor(chat)
                    .AsHostedAgent(foundryProject, HostedAgentProtocol.Responses, "2.0.0");

                builder.Build().Run();
                """);

            File.WriteAllText(appHostFilePath, appHostContent);
            output.WriteLine($"Modified AppHost.cs at: {appHostFilePath}");

            // Step 9: Navigate to AppHost project directory
            output.WriteLine("Step 9: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 10: Set environment variables for deployment
            // - Unset ASPIRE_PLAYGROUND to avoid conflicts
            // - Set Azure location
            // - Set AZURE__RESOURCEGROUP to use our unique resource group name
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Step 11: Deploy to Azure using aspire deploy
            output.WriteLine("Step 11: Starting Foundry Hosted Agent deployment...");
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            // Wait for pipeline to complete successfully
            // Foundry deployments may take longer due to AI resource provisioning
            await auto.WaitUntilTextAsync(ConsoleActivityLoggerStrings.PipelineSucceeded, timeout: TimeSpan.FromMinutes(35));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 12: Verify deployed resources exist in the resource group
            output.WriteLine("Step 12: Verifying deployed resources...");
            await auto.TypeAsync(
                $"RG_NAME=\"{resourceGroupName}\" && " +
                "echo \"Resource group: $RG_NAME\" && " +
                "if ! az group show -n \"$RG_NAME\" &>/dev/null; then echo \"❌ Resource group not found\"; exit 1; fi && " +
                "resources=$(az resource list -g \"$RG_NAME\" -o table 2>/dev/null) && " +
                "echo \"$resources\" && " +
                "if [ -z \"$resources\" ]; then echo \"❌ No resources found in resource group\"; exit 1; fi && " +
                "echo \"✅ Resources found in resource group\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Step 13: Exit terminal
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Deployment completed in {duration}");

            // Report success
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployFoundryHostedAgentToAzure),
                resourceGroupName,
                deploymentUrls,
                duration);

            output.WriteLine("✅ Test passed!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployFoundryHostedAgentToAzure),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Clean up the resource group we created
            output.WriteLine($"Triggering cleanup of resource group: {resourceGroupName}");
            TriggerCleanupResourceGroup(resourceGroupName, output);
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Cleanup triggered (fire-and-forget)");
        }
    }

    /// <summary>
    /// Triggers cleanup of a specific resource group.
    /// This is fire-and-forget - the hourly cleanup workflow handles any missed resources.
    /// </summary>
    private static void TriggerCleanupResourceGroup(string resourceGroupName, ITestOutputHelper output)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            output.WriteLine($"Cleanup triggered for resource group: {resourceGroupName}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to trigger cleanup: {ex.Message}");
        }
    }
}
