// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Tests.Publishing;

public class ComposeServiceParsingTests
{
    [Fact]
    public void ParseComposeServiceEntries_NdjsonFormat_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            """{"Service":"web","Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}""",
            """{"Service":"cache","Publishers":[{"URL":"","TargetPort":6379,"PublishedPort":6379,"Protocol":"tcp"}]}"""
        };

        var results = ContainerRuntimeBase<DockerContainerRuntime>.ParseComposeServiceEntries(lines);

        Assert.Equal(2, results.Count);
        Assert.Equal("web", results[0].Service);
        Assert.Equal(80, results[0].Publishers?[0].TargetPort);
        Assert.Equal(8080, results[0].Publishers?[0].PublishedPort);
        Assert.Equal("cache", results[1].Service);
    }

    [Fact]
    public void ParseComposeServiceEntries_JsonArrayFormat_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            """[{"Service":"web","Publishers":[{"TargetPort":80,"PublishedPort":8080}]},{"Service":"db","Publishers":[]}]"""
        };

        var results = ContainerRuntimeBase<DockerContainerRuntime>.ParseComposeServiceEntries(lines);

        Assert.Equal(2, results.Count);
        Assert.Equal("web", results[0].Service);
        Assert.Equal("db", results[1].Service);
    }

    [Fact]
    public void ParseComposeServiceEntries_EmptyLines_ReturnsEmpty()
    {
        var results = ContainerRuntimeBase<DockerContainerRuntime>.ParseComposeServiceEntries([]);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseComposeServiceEntries_InvalidJson_SkipsLine()
    {
        var lines = new List<string>
        {
            "not json",
            """{"Service":"web","Publishers":[{"TargetPort":80,"PublishedPort":8080}]}"""
        };

        var results = ContainerRuntimeBase<DockerContainerRuntime>.ParseComposeServiceEntries(lines);

        Assert.Single(results);
        Assert.Equal("web", results[0].Service);
    }

    [Fact]
    public void ParsePodmanPsOutput_ParsesPortsAndLabels()
    {
        var lines = new List<string>
        {
            """[{"Labels":{"com.docker.compose.service":"web"},"Ports":[{"host_ip":"","container_port":80,"host_port":8080,"range":1,"protocol":"tcp"}]},{"Labels":{"com.docker.compose.service":"cache"},"Ports":[{"host_ip":"","container_port":6379,"host_port":6379,"range":1,"protocol":"tcp"}]}]"""
        };

        var results = PodmanContainerRuntime.ParsePodmanPsOutput(lines);

        Assert.Equal(2, results.Count);
        Assert.Equal("web", results[0].Service);
        Assert.Equal(80, results[0].Publishers?[0].TargetPort);
        Assert.Equal(8080, results[0].Publishers?[0].PublishedPort);
        Assert.Equal("cache", results[1].Service);
        Assert.Equal(6379, results[1].Publishers?[0].TargetPort);
    }

    [Fact]
    public void ParsePodmanPsOutput_AggregatesMultipleContainersPerService()
    {
        var lines = new List<string>
        {
            """[{"Labels":{"com.docker.compose.service":"web"},"Ports":[{"container_port":80,"host_port":8080}]},{"Labels":{"com.docker.compose.service":"web"},"Ports":[{"container_port":443,"host_port":8443}]}]"""
        };

        var results = PodmanContainerRuntime.ParsePodmanPsOutput(lines);

        Assert.Single(results);
        Assert.Equal("web", results[0].Service);
        Assert.Equal(2, results[0].Publishers?.Count);
    }

    [Fact]
    public void ParsePodmanPsOutput_NoLabels_SkipsContainer()
    {
        var lines = new List<string>
        {
            """[{"Labels":{},"Ports":[{"container_port":80,"host_port":8080}]}]"""
        };

        var results = PodmanContainerRuntime.ParsePodmanPsOutput(lines);

        Assert.Empty(results);
    }

    [Fact]
    public void ParsePodmanPsOutput_EmptyInput_ReturnsEmpty()
    {
        var results = PodmanContainerRuntime.ParsePodmanPsOutput([]);

        Assert.Empty(results);
    }

    [Fact]
    public void ParsePodmanPsOutput_InvalidJson_ReturnsEmpty()
    {
        var results = PodmanContainerRuntime.ParsePodmanPsOutput(["not json"]);

        Assert.Empty(results);
    }
}
