// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AAIP001 // Toolbox APIs are experimental.

using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Utils;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;

namespace Aspire.Hosting.Foundry.Tests;

public class AzureFoundryToolboxAdministrationTests
{
    [Fact]
    public async Task CreateVersionAsync_PreservesSearchToolInHttpRequestAndReturnedConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account").AddProject("my-project");
        var search = builder.AddAzureSearch("search");
        var toolbox = project.AddToolbox("field-tools")
            .WithDescription("Tools for field technicians.")
            .WithAISearchTool("knowledge-base", search, "docs", "Search the internal knowledge base.");
        var searchDefinition = Assert.IsType<FoundryToolboxAzureAISearchToolDefinition>(
            Assert.Single(toolbox.Resource.Tools));
        const string connectionId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/account/projects/my-project/connections/search";
        searchDefinition.Connection.Outputs["id"] = connectionId;
        var tool = await searchDefinition.ResolveAsync(CancellationToken.None);
        var definition = FoundryToolboxDeploymentDefinition.Create(
            "field-tools", "Tools for field technicians.", [tool], new Dictionary<string, string>());
        var responseJson = $$"""
            {
              "object": "toolbox.version",
              "id": "field-tools:1",
              "name": "field-tools",
              "version": "1",
              "created_at": 0,
              "description": "Tools for field technicians.",
              "metadata": {{JsonSerializer.Serialize(definition.CreateDeploymentMetadata())}},
              "tools": [
                {
                  "type": "azure_ai_search",
                  "name": "knowledge-base",
                  "description": "Search the internal knowledge base.",
                  "azure_ai_search": {
                    "indexes": [{"project_connection_id": "{{connectionId}}", "index_name": "docs"}]
                  }
                }
              ]
            }
            """;
        using var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        using var httpClient = new HttpClient(handler);
        var options = new AIProjectClientOptions { Transport = new HttpClientPipelineTransport(httpClient) };
        options.AddPolicy(new FoundryToolboxFeaturesPolicy(), PipelinePosition.PerCall);
        var projectClient = new AIProjectClient(
            new Uri("https://example.invalid/api/projects/my-project"), new TestTokenCredential(), options);
        var toolboxes = projectClient.AgentAdministrationClient.GetAgentToolboxes();
        var administration = new AzureFoundryToolboxAdministration(toolboxes, _ => Assert.Fail("Unexpected retry."));

        var version = await administration.CreateVersionAsync(definition, CancellationToken.None);
        var returned = (await toolboxes.GetVersionAsync("field-tools", version, CancellationToken.None)).Value;

        Assert.Equal("1", version);
        Assert.Equal(version, returned.Version);
        Assert.Equal(definition.Description, returned.Description);
        Assert.Equal(definition.ConfigurationHash, returned.Metadata["aspire-configuration-hash"]);
        var returnedTool = Assert.IsType<AzureAISearchToolboxTool>(Assert.Single(returned.Tools));
        Assert.Equal("knowledge-base", returnedTool.Name);
        Assert.Equal("Search the internal knowledge base.", returnedTool.Description);
        var index = Assert.Single(returnedTool.AzureAiSearch.Indexes);
        Assert.Equal(connectionId, index.ProjectConnectionId);
        Assert.Equal("docs", index.IndexName);
        Assert.Equal(2, handler.Requests.Count);
        using var request = JsonDocument.Parse(handler.Requests[0].Content);
        using var returnedConfiguration = JsonDocument.Parse(ModelReaderWriter.Write(
            returned, ModelReaderWriterOptions.Json, AzureAIProjectsAgentsContext.Default));
        await Verify(JsonSerializer.Serialize(new
        {
            Request = request.RootElement,
            Returned = returnedConfiguration.RootElement
        }, new JsonSerializerOptions { WriteIndented = true }), "json")
            .AddScrubber(text => text.Replace(definition.ConfigurationHash, "{configurationHash}"));
    }
}
