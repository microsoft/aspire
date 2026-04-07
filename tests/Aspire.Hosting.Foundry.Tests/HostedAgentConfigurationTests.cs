// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry.Tests;

public class HostedAgentConfigurationTests
{
    [Fact]
    public void DefaultCpu_Is2()
    {
        var config = new HostedAgentConfiguration("myimage:latest");
        Assert.Equal(2.0m, config.Cpu);
    }

    [Fact]
    public void DefaultMemory_Is4()
    {
        var config = new HostedAgentConfiguration("myimage:latest");
        Assert.Equal(4.0m, config.Memory);
    }

    [Fact]
    public void CpuString_FormatsCorrectly()
    {
        var config = new HostedAgentConfiguration("myimage:latest") { Cpu = 1.5m };
        Assert.Equal("1.5", config.CpuString);
    }

    [Fact]
    public void MemoryString_FormatsCorrectly()
    {
        var config = new HostedAgentConfiguration("myimage:latest") { Cpu = 1.5m };
        Assert.Equal("3.0Gi", config.MemoryString);
    }

    [Fact]
    public void Cpu_ThrowsForInvalidValues()
    {
        var config = new HostedAgentConfiguration("myimage:latest");
        Assert.Throws<ArgumentException>(() => config.Cpu = 0.1m);
        Assert.Throws<ArgumentException>(() => config.Cpu = 4.0m);
        Assert.Throws<ArgumentException>(() => config.Cpu = 1.1m); // Not a 0.25 increment
    }

    [Fact]
    public void Memory_ThrowsForInvalidValues()
    {
        var config = new HostedAgentConfiguration("myimage:latest");
        Assert.Throws<ArgumentException>(() => config.Memory = 0.5m);
        Assert.Throws<ArgumentException>(() => config.Memory = 8.0m);
        Assert.Throws<ArgumentException>(() => config.Memory = 1.3m); // Not a 0.5 increment
    }

    [Fact]
    public void Image_IsSetFromConstructor()
    {
        var config = new HostedAgentConfiguration("myregistry.azurecr.io/myagent:v1");
        Assert.Equal("myregistry.azurecr.io/myagent:v1", config.Image);
    }

    [Fact]
    public void ToAgentVersionCreationOptions_ProducesValidOptions()
    {
        var config = new HostedAgentConfiguration("myimage:latest")
        {
            Description = "Test agent",
            Cpu = 1.0m,
        };

        var options = config.ToAgentVersionCreationOptions();

        Assert.NotNull(options);
        Assert.Equal("Test agent", options.Description);
    }

    [Fact]
    public void EnvironmentVariables_CanBeAdded()
    {
        var config = new HostedAgentConfiguration("myimage:latest");
        config.EnvironmentVariables["KEY"] = "VALUE";

        Assert.Single(config.EnvironmentVariables);
        Assert.Equal("VALUE", config.EnvironmentVariables["KEY"]);
    }

    [Fact]
    public void DefaultMetadata_ContainsDeployedByAndOn()
    {
        var config = new HostedAgentConfiguration("myimage:latest");

        Assert.Contains(config.Metadata, kvp => kvp.Key == "DeployedBy");
        Assert.Contains(config.Metadata, kvp => kvp.Key == "DeployedOn");
    }
}
