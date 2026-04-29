// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Microsoft.DurableTask.AzureManaged.Tests;

public class DurableTaskSchedulerConnectionStringTests
{
    [Theory]
    [InlineData("Endpoint=http://localhost:8080;Authentication=None;TaskHub=MyHub", "http://localhost:8080")]
    [InlineData("Endpoint=https://my-scheduler.durabletask.io;Authentication=DefaultAzure;TaskHub=Hub1", "https://my-scheduler.durabletask.io")]
    [InlineData("endpoint=http://localhost:8080;Authentication=None;TaskHub=MyHub", "http://localhost:8080")]
    [InlineData("  Endpoint = http://localhost:8080 ;Authentication=None;TaskHub=MyHub", "http://localhost:8080")]
    public void GetEndpoint_ReturnsExpectedValue(string connectionString, string expectedEndpoint)
    {
        var result = DurableTaskSchedulerConnectionString.GetEndpoint(connectionString);
        Assert.Equal(expectedEndpoint, result);
    }

    [Theory]
    [InlineData("Endpoint=http://localhost:8080;Authentication=None;TaskHub=MyHub", "MyHub")]
    [InlineData("Endpoint=https://my-scheduler.durabletask.io;Authentication=DefaultAzure;TaskHub=Hub1", "Hub1")]
    [InlineData("Endpoint=http://localhost:8080;Authentication=None;taskhub=MyHub", "MyHub")]
    [InlineData("Endpoint=http://localhost:8080;Authentication=None; TaskHub = MyHub ", "MyHub")]
    public void GetTaskHubName_ReturnsExpectedValue(string connectionString, string expectedTaskHub)
    {
        var result = DurableTaskSchedulerConnectionString.GetTaskHubName(connectionString);
        Assert.Equal(expectedTaskHub, result);
    }

    [Theory]
    [InlineData("Authentication=None;TaskHub=MyHub")]
    [InlineData("")]
    [InlineData("TaskHub=MyHub")]
    public void GetEndpoint_ReturnsNull_WhenEndpointIsMissing(string connectionString)
    {
        var result = DurableTaskSchedulerConnectionString.GetEndpoint(connectionString);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Endpoint=http://localhost:8080;Authentication=None")]
    [InlineData("")]
    [InlineData("Endpoint=http://localhost:8080")]
    public void GetTaskHubName_ReturnsNull_WhenTaskHubIsMissing(string connectionString)
    {
        var result = DurableTaskSchedulerConnectionString.GetTaskHubName(connectionString);
        Assert.Null(result);
    }

    [Fact]
    public void GetEndpoint_HandlesMalformedSegments()
    {
        // Segment without '=' should be skipped gracefully
        var result = DurableTaskSchedulerConnectionString.GetEndpoint("NoEqualsHere;Endpoint=http://localhost:8080;TaskHub=Hub");
        Assert.Equal("http://localhost:8080", result);
    }
}
