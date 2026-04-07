// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Foundry.Tests;

public class ProjectWithReferenceTests
{
    [Fact]
    public async Task WithReference_InjectsFoundryProjectEndpointAsConnectionProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("test-account")
            .AddProject("test-project");

        var pyapp = builder.AddPythonApp("app", "./app.py", "main:app")
            .WithReference(project);

        builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pyapp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Contains(envVars, kvp =>
            kvp.Key == "TEST_PROJECT_URI"
            && kvp.Value == "{test-project.outputs.endpoint}");
    }

    [Fact]
    public async Task WithReference_InjectsApplicationInsightsAsConnectionProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("test-account")
            .AddProject("test-project");

        var pyapp = builder.AddPythonApp("app", "./app.py", "main:app")
            .WithReference(project);

        builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pyapp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Contains(envVars, kvp =>
            kvp.Key == "TEST_PROJECT_APPLICATIONINSIGHTSCONNECTIONSTRING"
            && kvp.Value == "{test-project.outputs.APPLICATION_INSIGHTS_CONNECTION_STRING}");
    }

    [Fact]
    public async Task WithReference_InjectsStandardConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("test-account")
            .AddProject("test-project");

        var pyapp = builder.AddPythonApp("app", "./app.py", "main:app")
            .WithReference(project);

        builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pyapp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        // Standard connection string is also injected via ResourceBuilderExtensions.WithReference
        Assert.Contains(envVars, kvp =>
            kvp.Key.StartsWith("ConnectionStrings__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithReference_DeploymentInjectsModelNameAsConnectionProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("test-account");
        var deployment = foundry.AddDeployment("chat", "gpt-4", "1", "OpenAI");

        var pyapp = builder.AddPythonApp("app", "./app.py", "main:app")
            .WithReference(deployment);

        builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pyapp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Contains(envVars, kvp =>
            kvp.Key == "CHAT_MODELNAME" && kvp.Value == "chat");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "CHAT_FORMAT" && kvp.Value == "OpenAI");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "CHAT_VERSION" && kvp.Value == "1");
        Assert.Contains(envVars, kvp =>
            kvp.Key == "CHAT_URI");
    }
}
