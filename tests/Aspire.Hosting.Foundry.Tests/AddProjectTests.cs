// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Foundry.Tests;

public class AddProjectTests
{
    [Fact]
    public void ShouldCreateProject()
    {
        const string name = "my-project";
        using var builder = TestDistributedApplicationBuilder.Create();

        var resourceBuilder = builder.AddFoundry("account")
            .AddProject(name);

        Assert.NotNull(resourceBuilder);
        Assert.NotNull(resourceBuilder.Resource);
        Assert.Equal(name, resourceBuilder.Resource.Name);
        Assert.IsType<AzureCognitiveServicesProjectResource>(resourceBuilder.Resource);
    }

    [Fact]
    public async Task AddProject_WithReference_ShouldBindUriConnectionProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("test-account")
            .AddProject("test-project");

        var pyapp = builder.AddPythonApp("app", "./app.py", "main:app")
            .WithReference(project);

        builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(pyapp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Contains(envVars, (kvp) =>
        {
            var (key, value) = kvp;
            return key is "TEST_PROJECT_URI"
                && value is "{test-project.outputs.endpoint}";
        });
    }

    [Fact]
    public void AddProject_AfterRunAsFoundryLocal_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var foundry = builder.AddFoundry("account")
            .RunAsFoundryLocal();

        var exception = Assert.Throws<InvalidOperationException>(() => foundry.AddProject("my-project"));

        Assert.Equal(FoundryExtensions.LocalProjectsNotSupportedMessage, exception.Message);
    }

    [Fact]
    public void RunAsFoundryLocal_AfterAddProject_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var foundry = builder.AddFoundry("account");
        foundry.AddProject("my-project");

        var exception = Assert.Throws<InvalidOperationException>(foundry.RunAsFoundryLocal);

        Assert.Equal(FoundryExtensions.LocalProjectsNotSupportedMessage, exception.Message);
    }
}
