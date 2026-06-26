// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Azd.Tests;

public class AzdProjectParserTests
{
    [Fact]
    public void ParsesTopLevelFields()
    {
        using var sample = SampleAzdProject.Create();

        var project = AzdProjectParser.Parse(File.ReadAllText(sample.AzureYamlPath));

        Assert.Equal("contoso-app", project.Name);
        Assert.Equal("contoso@1.0", project.Metadata?.Template);
        Assert.Equal("bicep", project.Infra?.Provider);
        Assert.Equal("infra", project.Infra?.Path);
        Assert.Equal("main", project.Infra?.Module);
    }

    [Fact]
    public void ParsesServices()
    {
        using var sample = SampleAzdProject.Create();

        var project = AzdProjectParser.Parse(File.ReadAllText(sample.AzureYamlPath));

        Assert.Equal(["api", "legacy", "web"], project.Services.Keys.OrderBy(k => k));

        var web = project.Services["web"];
        Assert.Equal("./src/web", web.Project);
        Assert.Equal("dotnet", web.Language);
        Assert.Equal("containerapp", web.Host);
        Assert.Equal(["cache", "secrets", "pg"], web.Uses);
        Assert.Equal("Production", web.Env["ASPNETCORE_ENVIRONMENT"]);

        var api = project.Services["api"];
        Assert.Equal("appservice", api.Host);
        Assert.Equal("./Dockerfile", api.Docker?.Path);

        var legacy = project.Services["legacy"];
        Assert.Equal("python", legacy.Language);
        Assert.Equal("function", legacy.Host);
    }

    [Fact]
    public void ParsesResourcesWithTypeSpecificProperties()
    {
        using var sample = SampleAzdProject.Create();

        var project = AzdProjectParser.Parse(File.ReadAllText(sample.AzureYamlPath));

        Assert.Equal("db.redis", project.Resources["cache"].Type);
        Assert.Equal("keyvault", project.Resources["secrets"].Type);
        Assert.Equal("db.postgres", project.Resources["pg"].Type);
        Assert.Equal("db.mysql", project.Resources["db2"].Type);

        var sb = project.Resources["sb"];
        var queues = Assert.IsAssignableFrom<IReadOnlyList<object?>>(sb.Properties["queues"]);
        var topics = Assert.IsAssignableFrom<IReadOnlyList<object?>>(sb.Properties["topics"]);
        Assert.Equal(["jobs"], queues.Select(q => q?.ToString()));
        Assert.Equal(["events"], topics.Select(t => t?.ToString()));

        var orders = project.Resources["orders"];
        Assert.Equal("db.cosmos", orders.Type);
        var containers = Assert.IsAssignableFrom<IReadOnlyList<object?>>(orders.Properties["containers"]);
        var firstContainer = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(containers[0]);
        Assert.Equal("items", firstContainer["name"]);

        var openai = project.Resources["openai"];
        var model = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(openai.Properties["model"]);
        Assert.Equal("gpt-4o", model["name"]);
        Assert.Equal("2024-08-06", model["version"]);
    }

    [Fact]
    public void ToleratesUnknownTopLevelKeys()
    {
        var yaml =
            """
            name: tolerant
            workflows:
              up:
                steps:
                  - azd: provision
            customField: anything
            services:
              app:
                host: containerapp
            """;

        var project = AzdProjectParser.Parse(yaml);

        Assert.Equal("tolerant", project.Name);
        Assert.True(project.Services.ContainsKey("app"));
        Assert.True(project.Raw.ContainsKey("workflows"));
        Assert.True(project.Raw.ContainsKey("customField"));
    }
}
