// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using Aspire.Components.TestUtilities;
using Aspire.OpenAI;
using Azure.AI.Extensions.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using OpenAI.Chat;
using Xunit;

namespace Aspire.Azure.AI.Extensions.OpenAI.Tests;

[Collection("OpenAI telemetry")]
public class AspireConfigurableOpenAIExtensionsTests
{
    [Theory]
    [InlineData("Endpoint=https://api.openai.com/v1;Key=test;Model=chat", "https://api.openai.com/v1", "Aspire:OpenAI")]
    [InlineData("Endpoint=http://localhost:5272/v1;Key=local;Model=chat", "http://localhost:5272/v1", "Aspire:OpenAI")]
    [InlineData("Key=test;Model=chat", "https://api.openai.com/v1", "Aspire:OpenAI")]
    [InlineData("Endpoint=https://test.openai.azure.com;Deployment=chat", "https://test.openai.azure.com/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("Endpoint=https://test.openai.azure.com;Key=test;Deployment=chat", "https://test.openai.azure.com/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("https://test.openai.azure.com", "https://test.openai.azure.com/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("Endpoint=https://test.services.ai.azure.com;EndpointAIInference=https://test.services.ai.azure.com/models;Deployment=chat", "https://test.services.ai.azure.com/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("Endpoint=https://test.openai.azure.us;Deployment=chat", "https://test.openai.azure.us/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("Endpoint=https://test.openai.azure.cn;Deployment=chat", "https://test.openai.azure.cn/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("Endpoint=https://test.openai.azure.de;Deployment=chat", "https://test.openai.azure.de/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("Endpoint=https://custom.example.com;IsAzure=true;Deployment=chat", "https://custom.example.com/openai/v1/", "Aspire:Azure:AI:OpenAI")]
    [InlineData("Endpoint=https://test.openai.azure.com;IsAzure=false;Key=test;Model=chat", "https://test.openai.azure.com", "Aspire:OpenAI")]
    [InlineData("Endpoint=https://test.azure.com.example.org/v1;Key=test;Model=chat", "https://test.azure.com.example.org/v1", "Aspire:OpenAI")]
    public void SelectsClientAndPreservesModelHelpers(string connection, string endpoint, string section)
    {
        foreach (var keyed in new[] { false, true })
        {
            var builder = Host.CreateEmptyApplicationBuilder(null);
            builder.Configuration["ConnectionStrings:chat"] = connection;
            builder.Configuration["Aspire:Azure:AI:OpenAI:Deployment"] = "chat";
            builder.Configuration["Aspire:Azure:AI:OpenAI:chat:Deployment"] = "chat";
            var registration = Register(builder, keyed);
            Assert.Equal(keyed ? $"{section}:chat" : section, registration.ConfigurationSectionName);
            AddChat(registration, keyed);
            using var host = builder.Build();
            var client = Resolve(host.Services, keyed);
            Assert.Equal(new Uri(endpoint), client.Endpoint);
            Assert.Same(client, Resolve(host.Services, keyed));
            var chat = keyed ? host.Services.GetRequiredKeyedService<IChatClient>("chat") : host.Services.GetRequiredService<IChatClient>();
            Assert.Equal("chat", chat.GetService<ChatClient>()!.Model);
            if (keyed)
            {
                Assert.Null(host.Services.GetService<IChatClient>());
            }
        }
    }

    [Theory]
    [InlineData(false, "Endpoint=https://test.services.ai.azure.com/api/projects/project;Deployment=chat")]
    [InlineData(true, "Endpoint=https://test.services.ai.azure.com/api/projects/project;Deployment=chat")]
    [InlineData(false, "https://test.services.ai.azure.com/api/projects/project")]
    [InlineData(true, "https://test.services.ai.azure.com/api/projects/project")]
    [InlineData(false, "Endpoint=https://custom.example.com/api/projects/project;IsAzure=true;Deployment=chat")]
    [InlineData(true, "Endpoint=https://custom.example.com/api/projects/project;IsAzure=true;Deployment=chat")]
    public void SelectsFoundryProject(bool keyed, string connection)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:chat"] = connection;
        builder.Configuration["Aspire:Azure:AI:Extensions:OpenAI:Deployment"] = "chat";
        builder.Configuration["Aspire:Azure:AI:Extensions:OpenAI:chat:Deployment"] = "chat";
        AddChat(Register(builder, keyed), keyed);
        using var host = builder.Build();
        var client = Resolve(host.Services, keyed);
        Assert.IsType<ProjectOpenAIClient>(client);
        Assert.Same(client, keyed ? host.Services.GetRequiredKeyedService<ProjectOpenAIClient>("chat") : host.Services.GetRequiredService<ProjectOpenAIClient>());
        var chat = keyed ? host.Services.GetRequiredKeyedService<IChatClient>("chat") : host.Services.GetRequiredService<IChatClient>();
        Assert.Equal("chat", chat.GetService<ChatClient>()!.Model);
    }

    [Theory]
    [InlineData("Endpoint=relative;Deployment=chat")]
    [InlineData("Endpoint=relative;Key=test;Model=chat")]
    [InlineData("Endpoint=file:///tmp/openai;Key=test;Model=chat")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("Endpoint=https://test.openai.azure.com;IsAzure=maybe")]
    [InlineData("malformed")]
    public void RejectsInvalidConnectionInformation(string connection)
    {
        foreach (var keyed in new[] { false, true })
        {
            var builder = Host.CreateEmptyApplicationBuilder(null);
            builder.Configuration["ConnectionStrings:chat"] = connection;
            Assert.ThrowsAny<ArgumentException>(() => Register(builder, keyed));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingConnectionInformation(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:chat"] = "Deployment=chat";
        Assert.Throws<InvalidOperationException>(() => Register(builder, keyed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesSettingsOnlyOpenAIRegistration(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["Aspire:OpenAI:Key"] = "test";
        builder.Configuration["Aspire:OpenAI:Deployment"] = "chat";
        AddChat(Register(builder, keyed), keyed);
        using var host = builder.Build();
        Assert.Equal(new Uri("https://api.openai.com/v1"), Resolve(host.Services, keyed).Endpoint);
    }

    [Fact]
    public void ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AspireConfigurableOpenAIExtensions.AddOpenAIClientFromConfiguration(null!, "chat"));
        Assert.Throws<ArgumentNullException>(() => AspireConfigurableOpenAIExtensions.AddKeyedOpenAIClientFromConfiguration(null!, "chat"));
        var builder = Host.CreateEmptyApplicationBuilder(null);
        Assert.Throws<ArgumentException>(() => builder.AddOpenAIClientFromConfiguration(""));
        Assert.Throws<ArgumentException>(() => builder.AddKeyedOpenAIClientFromConfiguration(""));
    }

    [Fact]
    public void KeyedClientsWithDifferentBackendsCoexist()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:local"] = "Endpoint=http://localhost:5272/v1;Key=local;Model=local-model";
        builder.Configuration["ConnectionStrings:account"] = "Endpoint=https://test.openai.azure.com;Deployment=account-model";
        builder.Configuration["ConnectionStrings:project"] = "Endpoint=https://test.services.ai.azure.com/api/projects/project;Deployment=project-model";
        foreach (var key in new[] { "local", "account", "project" })
        {
            builder.AddKeyedOpenAIClientFromConfiguration(key).AddKeyedChatClient(key);
        }

        using var host = builder.Build();
        Assert.Equal("local-model", host.Services.GetRequiredKeyedService<IChatClient>("local").GetService<ChatClient>()!.Model);
        Assert.Equal("account-model", host.Services.GetRequiredKeyedService<IChatClient>("account").GetService<ChatClient>()!.Model);
        Assert.Equal("project-model", host.Services.GetRequiredKeyedService<IChatClient>("project").GetService<ChatClient>()!.Model);
        Assert.Null(host.Services.GetService<IChatClient>());
    }

    [Theory]
    [InlineData(false, "Endpoint=https://test.openai.azure.com;Key=test-key;Deployment=chat", "https://test.openai.azure.com/openai/v1/chat/completions")]
    [InlineData(true, "Endpoint=https://test.openai.azure.com;Key=test-key;Deployment=chat", "https://test.openai.azure.com/openai/v1/chat/completions")]
    [InlineData(false, "Endpoint=https://api.openai.com/v1;Key=test-key;Model=chat", "https://api.openai.com/v1/chat/completions")]
    [InlineData(true, "Endpoint=https://api.openai.com/v1;Key=test-key;Model=chat", "https://api.openai.com/v1/chat/completions")]
    [InlineData(false, "Endpoint=http://localhost:5272/v1;Key=test-key;Model=chat", "http://localhost:5272/v1/chat/completions")]
    [InlineData(true, "Endpoint=http://localhost:5272/v1;Key=test-key;Model=chat", "http://localhost:5272/v1/chat/completions")]
    public async Task DispatchedChatClientUsesConfiguredEndpointAndApiKey(bool keyed, string connection, string endpoint)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:chat"] = connection;
        using var handler = new FoundryTestHttpMessageHandler("""
            {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"chat",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """);
        using var httpClient = new HttpClient(handler);
        builder.Services.ConfigureAll<OpenAIClientOptions>(options => options.Transport = new HttpClientPipelineTransport(httpClient));
        AddChat(Register(builder, keyed), keyed);
        using var host = builder.Build();
        var chat = keyed ? host.Services.GetRequiredKeyedService<IChatClient>("chat") : host.Services.GetRequiredService<IChatClient>();

        var response = await chat.GetResponseAsync("Hello");

        Assert.Equal("Hello", response.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(endpoint), request.Uri);
        Assert.Equal("Bearer test-key", request.Authorization);
    }

    private static AspireOpenAIClientBuilder Register(HostApplicationBuilder builder, bool keyed)
        => keyed ? builder.AddKeyedOpenAIClientFromConfiguration("chat") : builder.AddOpenAIClientFromConfiguration("chat");

    private static void AddChat(AspireOpenAIClientBuilder builder, bool keyed)
    {
        if (keyed)
        {
            builder.AddKeyedChatClient("chat");
        }
        else
        {
            builder.AddChatClient();
        }
    }

    private static OpenAIClient Resolve(IServiceProvider services, bool keyed)
        => keyed ? services.GetRequiredKeyedService<OpenAIClient>("chat") : services.GetRequiredService<OpenAIClient>();
}
