// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Mcp;

public class ListIntegrationsToolTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListIntegrationsTool_CallToolAsync_UsesConfiguredSourcesFromResolvedDirectory(bool useAppHostDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """
            {"integrationSources":{"default":{"enabled":false},"team":{"source":"https://example.com/wrong","patterns":["Wrong.*"]}}}
            """);
        var directory = useAppHostDirectory ? workspace.CreateDirectory("app") : workspace.WorkspaceRoot;
        File.WriteAllText(Path.Combine(directory.FullName, AspireConfigFile.FileName), """
            {"integrationSources":{
              "default":{"enabled":false},
              "team":{"source":"https://example.com/team","patterns":["Contoso.Hosting.*"]},
              "other":{"source":"https://example.com/other","patterns":["Fabrikam.Hosting.*"]}
            }}
            """);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        if (useAppHostDirectory)
        {
            var appHostFile = new FileInfo(Path.Combine(directory.FullName, "AppHost.csproj"));
            File.WriteAllText(appHostFile.FullName, "<Project />");
            var connection = new TestAppHostAuxiliaryBackchannel
            {
                Hash = "app",
                SocketPath = "app-socket",
                AppHostInfo = new AppHostInformation
                {
                    AppHostPath = appHostFile.FullName,
                    ProcessId = Environment.ProcessId,
                    CliProcessId = null
                }
            };
            monitor.AddConnection(connection.SocketPath, connection);
        }
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Built-in discovery is disabled.")
        };
        var queries = new ConcurrentBag<(string Source, string Query)>();
        var cache = new FakeNuGetPackageCache
        {
            SearchLimitReached = true,
            GetPackagesAsyncCallback = (_, query, filter, includePrerelease, config, _, _) =>
            {
                var source = Assert.Single(XDocument.Load(config!.FullName).Root!.Element("packageSources")!.Elements("add")).Attribute("value")!.Value;
                queries.Add((source, query));
                Assert.True(includePrerelease);
                NuGetPackage[] packages = source switch
                {
                    "https://example.com/team" =>
                    [
                        new() { Id = "Contoso.Hosting.Redis", Version = "1.0.0", Source = source },
                        new() { Id = "Contoso.Hosting.Redis", Version = "2.0.0-beta.1", Source = source },
                        new() { Id = "Unapproved.Package", Version = "1.0.0", Source = source }
                    ],
                    "https://example.com/other" =>
                    [
                        new() { Id = "Fabrikam.Hosting.Storage", Version = "3.0.0", Source = source },
                        new() { Id = "Unapproved.Package", Version = "1.0.0", Source = source }
                    ],
                    _ => throw new InvalidOperationException($"Unexpected source: {source}")
                };
                return Task.FromResult(packages.Where(package => filter!(package.Id)));
            }
        };
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        services.AddSingleton<IPackagingService>(packagingService);
        services.AddSingleton<INuGetPackageCache>(cache);
        using var provider = services.BuildServiceProvider();
        var tool = new ListIntegrationsTool(provider.GetRequiredService<IntegrationPackageSearchService>(), provider.GetRequiredService<CliExecutionContext>(), monitor);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.Empty(interactionService.DisplayedMessages);
        Assert.Equal(
            [("https://example.com/other", "Fabrikam.Hosting."), ("https://example.com/team", "Contoso.Hosting.")],
            queries.OrderBy(static query => query.Source, StringComparer.Ordinal));
        var textContent = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        await VerifyJson(textContent.Text).UseParameters(useAppHostDirectory);
    }

    [Fact]
    public async Task ListIntegrationsTool_CallToolAsync_ReturnsWarningWhenLimitedSearchHasNoMatches()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """
            {"integrationSources":{"default":{"enabled":false},"team":{"source":"https://example.com/team","patterns":["Contoso.Hosting.*"]}}}
            """);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        services.AddSingleton<INuGetPackageCache>(new FakeNuGetPackageCache { SearchLimitReached = true });
        using var provider = services.BuildServiceProvider();
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.Empty(interactionService.DisplayedMessages);
        var textContent = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        await Verify(textContent.Text, "json");
    }

    [Fact]
    public async Task ListIntegrationsTool_CallToolAsync_ReturnsEmptyWhenBuiltInDiscoveryDisabled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """
            {"integrationSources":{"default":{"enabled":false}}}
            """);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Built-in discovery is disabled.")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IPackagingService>(packagingService);
        using var provider = services.BuildServiceProvider();
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        await Verify(textContent.Text, "json");
    }

    [Fact]
    public void ListIntegrationsTool_HasCorrectName()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CreateServiceProvider(workspace, MockPackagingServiceFactory.Create());
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);

        Assert.Equal("list_integrations", tool.Name);
    }

    [Fact]
    public void ListIntegrationsTool_HasCorrectDescription()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CreateServiceProvider(workspace, MockPackagingServiceFactory.Create());
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);

        Assert.Contains("List available Aspire hosting integrations", tool.Description);
        Assert.Contains("This tool does not require a running AppHost", tool.Description);
    }

    [Fact]
    public void ListIntegrationsTool_GetInputSchema_ReturnsValidSchema()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CreateServiceProvider(workspace, MockPackagingServiceFactory.Create());
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);
        var schema = tool.GetInputSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("type", out var typeElement));
        Assert.Equal("object", typeElement.GetString());
        Assert.True(schema.TryGetProperty("properties", out var propsElement));
        Assert.Equal(JsonValueKind.Object, propsElement.ValueKind);
        Assert.True(schema.TryGetProperty("description", out var descElement));
        Assert.Contains("This tool takes no input parameters", descElement.GetString());
        Assert.True(schema.TryGetProperty("additionalProperties", out var additionalPropsElement));
        Assert.False(additionalPropsElement.GetBoolean());
    }

    [Fact]
    public async Task ListIntegrationsTool_CallToolAsync_ReturnsEmptyJsonArray_WhenNoPackagesFound()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var mockPackagingService = MockPackagingServiceFactory.Create();
        using var provider = CreateServiceProvider(workspace, mockPackagingService);
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // Verify it's valid JSON with empty integrations array
        using var json = JsonDocument.Parse(textContent.Text);
        Assert.True(json.RootElement.TryGetProperty("integrations", out var integrations));
        Assert.Equal(JsonValueKind.Array, integrations.ValueKind);
        Assert.Equal(0, integrations.GetArrayLength());
    }

    [Fact]
    public async Task ListIntegrationsTool_CallToolAsync_ReturnsJsonWithPackages_WhenPackagesFound()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var mockPackagingService = MockPackagingServiceFactory.Create(new[]
        {
            new Aspire.Shared.NuGetPackageCli { Id = "CommunityToolkit.Aspire.Hosting.Aardvark", Version = "9.0.0" },
            new Aspire.Shared.NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.0.0" },
            new Aspire.Shared.NuGetPackageCli { Id = "Acme.Aspire.Hosting.Custom", Version = "9.0.0" },
            new Aspire.Shared.NuGetPackageCli { Id = "Aspire.Hosting.PostgreSQL", Version = "9.0.0" },
            new Aspire.Shared.NuGetPackageCli { Id = "Aspire.Hosting.CommunityToolkit.Redis", Version = "9.0.0" }
        });
        using var provider = CreateServiceProvider(workspace, mockPackagingService);
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // Verify it's valid JSON with proper structure
        using var json = JsonDocument.Parse(textContent.Text);
        Assert.True(json.RootElement.TryGetProperty("integrations", out var integrations));
        Assert.Equal(JsonValueKind.Array, integrations.ValueKind);
        Assert.Equal(5, integrations.GetArrayLength());

        // Verify the first integration has the expected properties
        var firstIntegration = integrations[0];
        Assert.True(firstIntegration.TryGetProperty("name", out _));
        Assert.True(firstIntegration.TryGetProperty("packageId", out _));
        Assert.True(firstIntegration.TryGetProperty("version", out _));

        var packageIds = integrations.EnumerateArray()
            .Select(e => e.GetProperty("packageId").GetString())
            .ToList();
        Assert.Equal(
            ["Aspire.Hosting.CommunityToolkit.Redis", "Aspire.Hosting.PostgreSQL", "Aspire.Hosting.Redis", "CommunityToolkit.Aspire.Hosting.Aardvark", "Acme.Aspire.Hosting.Custom"],
            packageIds);
    }

    [Fact]
    public async Task ListIntegrationsTool_UsesSharedDefaultDiscoveryWhenNoSourcesConfigured()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var mockPackagingService = MockPackagingServiceFactory.Create(new[]
        {
            new Aspire.Shared.NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.0.0" }
        });
        using var provider = CreateServiceProvider(workspace, mockPackagingService);
        var tool = ActivatorUtilities.CreateInstance<ListIntegrationsTool>(provider);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);

        using var json = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content![0]).Text);
        Assert.True(json.RootElement.TryGetProperty("integrations", out var integrations));
        Assert.Equal(1, integrations.GetArrayLength());
    }

    private ServiceProvider CreateServiceProvider(TemporaryWorkspace workspace, IPackagingService packagingService)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton(packagingService);

        return services.BuildServiceProvider();
    }
}
