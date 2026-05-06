// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

public class WithEndpointsTests
{
    [Fact]
    public async Task ResourceNamesWithDashesAreEncodedInEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("project-a")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var projectB = builder.AddProject<ProjectB>("consumer")
            .WithEndpoints(projectA);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__project-a__https__0"]);
        Assert.Equal("https://localhost:2000", config["PROJECT_A_MYBINDING"]);
        Assert.DoesNotContain("services__project_a__mybinding__0", config.Keys);
        Assert.DoesNotContain("PROJECT-A_MYBINDING", config.Keys);
    }

    [Fact]
    public async Task OverriddenServiceNamesAreEncodedInEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("project-a")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var projectB = builder.AddProject<ProjectB>("consumer")
            .WithEndpoints(projectA, "custom-name");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__custom-name__https__0"]);
        Assert.Equal("https://localhost:2000", config["custom_name_MYBINDING"]);
        Assert.DoesNotContain("services__custom_name__https__0", config.Keys);
        Assert.DoesNotContain("custom-name_MYBINDING", config.Keys);
    }

    [Theory]
    [InlineData(ReferenceEnvironmentInjectionFlags.All)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ConnectionProperties)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ConnectionString)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ServiceDiscovery)]
    [InlineData(ReferenceEnvironmentInjectionFlags.Endpoints)]
    [InlineData(ReferenceEnvironmentInjectionFlags.None)]
    public async Task ProjectWithEndpointRespectsCustomEnvironmentVariableNaming(ReferenceEnvironmentInjectionFlags flags)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a binding and its matching annotation (simulating DCP behavior)
        var projectA = builder.AddProject<ProjectA>("projecta")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        // Get the service provider.
        var projectB = builder.AddProject<ProjectB>("b")
            .WithEndpoints(projectA, "custom")
            .WithReferenceEnvironment(flags);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        switch (flags)
        {
            case ReferenceEnvironmentInjectionFlags.All:
                Assert.Equal("https://localhost:2000", config["custom_MYBINDING"]);
                Assert.Equal("https://localhost:2000", config["services__custom__https__0"]);
                break;

            case ReferenceEnvironmentInjectionFlags.ConnectionProperties:
            case ReferenceEnvironmentInjectionFlags.ConnectionString:
            case ReferenceEnvironmentInjectionFlags.None:
                Assert.False(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey("services__custom__https__0"));
                break;

            case ReferenceEnvironmentInjectionFlags.ServiceDiscovery:
                Assert.False(config.ContainsKey("custom_MYBINDING"));
                Assert.True(config.ContainsKey("services__custom__https__0"));
                break;

            case ReferenceEnvironmentInjectionFlags.Endpoints:
                Assert.True(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey("services__custom__https__0"));
                break;
        }
    }

    [Theory]
    [InlineData(ReferenceEnvironmentInjectionFlags.All)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ConnectionProperties)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ConnectionString)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ServiceDiscovery)]
    [InlineData(ReferenceEnvironmentInjectionFlags.Endpoints)]
    [InlineData(ReferenceEnvironmentInjectionFlags.None)]
    public async Task ContainerResourceWithEndpointRespectsCustomEnvironmentVariableNaming(ReferenceEnvironmentInjectionFlags flags)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a binding and its matching annotation (simulating DCP behavior)
        var container = builder.AddContainer("mycontainer", "myimage")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        // Get the service provider.
        var project = builder.AddProject<ProjectB>("b")
            .WithEndpoints(container, "custom")
            .WithReferenceEnvironment(flags);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        switch (flags)
        {
            case ReferenceEnvironmentInjectionFlags.All:
                Assert.Equal("https://localhost:2000", config["custom_MYBINDING"]);
                Assert.False(config.ContainsKey["services__custom__https__0"]);
                break;

            case ReferenceEnvironmentInjectionFlags.ConnectionProperties:
            case ReferenceEnvironmentInjectionFlags.ConnectionString:
            case ReferenceEnvironmentInjectionFlags.ServiceDiscovery:
            case ReferenceEnvironmentInjectionFlags.None:
                Assert.False(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey["services__custom__https__0"]);
                break;

            case ReferenceEnvironmentInjectionFlags.Endpoints:
                Assert.True(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey["services__custom__https__0"]);
                break;
        }
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class ProjectB : IProjectMetadata
    {
        public string ProjectPath => "projectB";
        public LaunchSettings LaunchSettings { get; } = new();
    }
}
