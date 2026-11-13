// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Text.Json;
using Aspire.Components.TestUtilities;
using Azure.AI.Projects;
using Azure.Core.Extensions;
using Json.Schema;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Azure.AI.Projects.Tests;

public class AspireAzureAIProjectsExtensionsTests
{
    private const string Endpoint = "https://test.services.ai.azure.com/api/projects/test";
    private const string Section = "Aspire:Azure:AI:Projects";

    [Fact]
    public void ValidatesArguments()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        Assert.Throws<ArgumentNullException>(() => AspireAzureAIProjectsExtensions.AddAzureAIProjectClient(null!, "project"));
        Assert.Throws<ArgumentNullException>(() => AspireAzureAIProjectsExtensions.AddKeyedAzureAIProjectClient(null!, "project"));
        Assert.Throws<ArgumentException>(() => builder.AddAzureAIProjectClient(""));
        Assert.Throws<ArgumentException>(() => builder.AddKeyedAzureAIProjectClient(""));
    }

    [Theory]
    [InlineData("""{"Endpoint":"https://test.services.ai.azure.com/api/projects/test","DisableTracing":false,"ClientOptions":{"NetworkTimeout":"00:00:30"}}""", true)]
    [InlineData("""{"Endpoint":"relative"}""", false)]
    [InlineData("""{"DisableTracing":"true"}""", false)]
    public void ConfigurationSchemaValidatesSettings(string settings, bool valid)
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json"),
            new BuildOptions { Dialect = Dialect.Draft07, SchemaRegistry = new SchemaRegistry() });
        using var configuration = JsonDocument.Parse("""{"Aspire":{"Azure":{"AI":{"Projects":""" + settings + "}}}}");
        Assert.Equal(valid, schema.Evaluate(configuration.RootElement, new EvaluationOptions { RequireFormatValidation = true }).IsValid);
    }

    [Theory]
    [InlineData(false, false, "root")]
    [InlineData(true, false, "named")]
    [InlineData(false, true, "connection")]
    [InlineData(true, true, "connection")]
    public void NamedSettingsAndConnectionStringsOverrideRoot(bool named, bool connectionString, string expected)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration[$"{Section}:Endpoint"] = "https://root.services.ai.azure.com/api/projects/test";
        if (named)
        {
            builder.Configuration[$"{Section}:project:Endpoint"] = "https://named.services.ai.azure.com/api/projects/test";
        }
        if (connectionString)
        {
            builder.Configuration["ConnectionStrings:project"] = "Endpoint=https://connection.services.ai.azure.com/api/projects/test";
        }

        builder.AddAzureAIProjectClient("project", settings => Assert.Equal($"{expected}.services.ai.azure.com", settings.Endpoint!.Host));
    }

    [Theory]
    [InlineData(false, Endpoint)]
    [InlineData(true, Endpoint)]
    [InlineData(false, "Endpoint=" + Endpoint)]
    [InlineData(true, "Endpoint=" + Endpoint)]
    public void RegistersSingleton(bool keyed, string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:project"] = connectionString;
        Register(builder, keyed);
        using var host = builder.Build();

        var client = Resolve(host.Services, keyed);
        Assert.Same(client, Resolve(host.Services, keyed));
        if (keyed)
        {
            Assert.Null(host.Services.GetService<AIProjectClient>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingEndpointFailsAtResolution(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        Register(builder, keyed);
        using var host = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(host.Services, keyed));
        Assert.Contains("ConnectionStrings:project", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationPrecedenceAndClientCustomization(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{Section}:Endpoint"] = "https://root.services.ai.azure.com/api/projects/root",
            [$"{Section}:project:Endpoint"] = "https://named.services.ai.azure.com/api/projects/named",
            ["ConnectionStrings:project"] = $"Endpoint={Endpoint}",
            [$"{Section}:ClientOptions:NetworkTimeout"] = "00:00:01",
            [$"{Section}:project:ClientOptions:NetworkTimeout"] = "00:00:02",
            [$"{Section}:project:ClientOptions:UserAgentApplicationId"] = "config-app"
        });
        var settingsCredential = new FoundryTestTokenCredential("settings-token");
        var clientCredential = new FoundryTestTokenCredential("client-token");
        using var handler = new FoundryTestHttpMessageHandler("""{"name":"agent","id":"agent","object":"agent"}""");
        using var httpClient = new HttpClient(handler);
        var finalEndpoint = new Uri("https://callback.services.ai.azure.com/api/projects/callback");

        void ConfigureSettings(AzureAIProjectsSettings settings)
        {
            Assert.Equal(new Uri(Endpoint), settings.Endpoint);
            settings.Endpoint = finalEndpoint;
            settings.Credential = settingsCredential;
        }

        void ConfigureClient(IAzureClientBuilder<AIProjectClient, AIProjectClientOptions> clientBuilder)
        {
            clientBuilder.WithCredential(clientCredential).ConfigureOptions(options =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2), options.NetworkTimeout);
                Assert.Equal("config-app", options.UserAgentApplicationId);
                options.NetworkTimeout = TimeSpan.FromSeconds(3);
                options.Transport = new HttpClientPipelineTransport(httpClient);
            });
        }

        if (keyed)
        {
            builder.AddKeyedAzureAIProjectClient("project", ConfigureSettings, ConfigureClient);
        }
        else
        {
            builder.AddAzureAIProjectClient("project", ConfigureSettings, ConfigureClient);
        }

        using var host = builder.Build();
        await Resolve(host.Services, keyed).AgentAdministrationClient.GetAgentAsync("agent");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/callback/agents/agent", request.Uri.AbsolutePath);
        Assert.Equal("Bearer client-token", request.Authorization);
        Assert.Contains("config-app", request.UserAgent);
        Assert.Empty(settingsCredential.RequestedScopes);
        Assert.NotEmpty(clientCredential.RequestedScopes);
        Assert.Equal(TimeSpan.FromSeconds(3), host.Services.GetRequiredService<IOptionsMonitor<AIProjectClientOptions>>().Get(keyed ? "project" : "Default").NetworkTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsCanSupplyEndpointWithoutConnectionString(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        if (keyed)
        {
            builder.AddKeyedAzureAIProjectClient("project", settings => settings.Endpoint = new(Endpoint));
        }
        else
        {
            builder.AddAzureAIProjectClient("project", settings => settings.Endpoint = new(Endpoint));
        }

        using var host = builder.Build();
        Assert.NotNull(Resolve(host.Services, keyed));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "Endpoint=relative")]
    [InlineData(true, "Endpoint=relative")]
    [InlineData(false, "Endpoint=https://test.services.ai.azure.com/api/projects/test;Key=secret")]
    [InlineData(true, "Endpoint=https://test.services.ai.azure.com/api/projects/test;Key=secret")]
    [InlineData(false, "ftp://test/project")]
    [InlineData(true, "ftp://test/project")]
    [InlineData(false, "http://test.services.ai.azure.com/api/projects/test")]
    [InlineData(true, "http://test.services.ai.azure.com/api/projects/test")]
    [InlineData(false, "Endpoint=http://test.services.ai.azure.com/api/projects/test")]
    [InlineData(true, "Endpoint=http://test.services.ai.azure.com/api/projects/test")]
    public void RejectsInvalidOrApiKeyConnectionStrings(bool keyed, string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:project"] = connectionString;
        Assert.Throws<ArgumentException>(() => Register(builder, keyed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RejectsHttpEndpointsFromSettings(bool keyed, bool configureSettings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var endpoint = new Uri("http://test.services.ai.azure.com/api/projects/test");
        var credential = new FoundryTestTokenCredential();
        if (!configureSettings)
        {
            builder.Configuration[keyed ? $"{Section}:project:Endpoint" : $"{Section}:Endpoint"] = endpoint.AbsoluteUri;
        }

        void ConfigureSettings(AzureAIProjectsSettings settings)
        {
            settings.Credential = credential;
            if (configureSettings)
            {
                settings.Endpoint = endpoint;
            }
        }

        if (keyed)
        {
            builder.AddKeyedAzureAIProjectClient("project", ConfigureSettings);
        }
        else
        {
            builder.AddAzureAIProjectClient("project", ConfigureSettings);
        }

        using var host = builder.Build();
        var exception = Assert.Throws<ArgumentException>(() => Resolve(host.Services, keyed));
        Assert.Equal(nameof(endpoint), exception.ParamName);
        Assert.Contains("absolute HTTPS URI", exception.Message);
        Assert.Empty(credential.RequestedScopes);
    }

    [Fact]
    public void KeyedClientsAreIndependent()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddKeyedAzureAIProjectClient("first", settings => settings.Endpoint = new(Endpoint));
        builder.AddKeyedAzureAIProjectClient("second", settings => settings.Endpoint = new(Endpoint));
        using var host = builder.Build();

        Assert.NotSame(host.Services.GetRequiredKeyedService<AIProjectClient>("first"), host.Services.GetRequiredKeyedService<AIProjectClient>("second"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportsSdkActivitiesWhenTracingEnabled(bool enabled)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        using var handler = new FoundryTestHttpMessageHandler("""{"name":"connection","id":"connection","type":"AzureOpenAI","target":"https://test.openai.azure.com","isDefault":false,"metadata":{}}""");
        using var httpClient = new HttpClient(handler);
        using var exporter = new FoundryTestActivityExporter();
        builder.AddAzureAIProjectClient("project", settings =>
        {
            settings.Endpoint = new(Endpoint);
            settings.Credential = new FoundryTestTokenCredential();
            settings.DisableTracing = !enabled;
        }, client => client.ConfigureOptions(options => options.Transport = new HttpClientPipelineTransport(httpClient)));
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(exporter)));
        using var host = builder.Build();
        host.Services.GetRequiredService<TracerProvider>();

        await host.Services.GetRequiredService<AIProjectClient>().Connections.GetConnectionAsync("connection");

        if (enabled)
        {
            Assert.Contains(exporter.Activities, activity => activity.Source.Name.StartsWith("Azure.AI.Projects.", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(exporter.Activities);
        }
    }

    private static void Register(HostApplicationBuilder builder, bool keyed)
    {
        if (keyed)
        {
            builder.AddKeyedAzureAIProjectClient("project");
        }
        else
        {
            builder.AddAzureAIProjectClient("project");
        }
    }

    private static AIProjectClient Resolve(IServiceProvider services, bool keyed)
        => keyed ? services.GetRequiredKeyedService<AIProjectClient>("project") : services.GetRequiredService<AIProjectClient>();
}
