// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

namespace Aspire.Hosting.Docker.Tests;

public class UpdateConfigTests
{
    [Fact]
    public void UpdateConfig_SerializesParallelismAsInteger()
    {
        // Arrange
        var updateConfig = new UpdateConfig
        {
            Parallelism = 2
        };

        var composeFile = new ComposeFile
        {
            Services = new Dictionary<string, Service>
            {
                ["test-service"] = new Service
                {
                    Name = "test-service",
                    Image = "nginx",
                    Deploy = new Deploy
                    {
                        UpdateConfig = updateConfig
                    }
                }
            }
        };

        // Act
        var yaml = composeFile.ToYaml();

        // Assert - should serialize as integer (correct)
        Assert.Contains("parallelism: 2", yaml);
        Assert.DoesNotContain("parallelism: \"2\"", yaml);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("rollback")]
    [InlineData("pause")]
    public void UpdateConfig_AcceptsValidFailureActionValues(string failureAction)
    {
        // Arrange & Act
        var updateConfig = new UpdateConfig
        {
            FailureAction = failureAction
        };

        // Assert - no exception should be thrown
        Assert.Equal(failureAction, updateConfig.FailureAction);
    }

    [Fact]
    public void UpdateConfig_NullValuesOmittedFromYaml()
    {
        // Arrange - Only set some properties
        var updateConfig = new UpdateConfig
        {
            Parallelism = 3, // Set to verify not all are null
            FailureAction = null, // Should be omitted
            Delay = null // Should be omitted
        };

        var composeFile = new ComposeFile
        {
            Services = new Dictionary<string, Service>
            {
                ["test-service"] = new Service
                {
                    Name = "test-service",
                    Image = "nginx",
                    Deploy = new Deploy
                    {
                        UpdateConfig = updateConfig
                    }
                }
            }
        };

        // Act
        var yaml = composeFile.ToYaml();

        // Assert - null values should be omitted
        Assert.Contains("parallelism: 3", yaml);
        Assert.DoesNotContain("failure_action:", yaml);
        Assert.DoesNotContain("delay:", yaml);
    }
}