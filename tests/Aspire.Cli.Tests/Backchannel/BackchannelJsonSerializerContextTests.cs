// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Backchannel;

public class BackchannelJsonSerializerContextTests
{
    [Fact]
    public void JsonSerializerOptionsCanSerializeAndDeserializeResourceSnapshotMcpServers()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        var servers = new Aspire.Cli.Backchannel.ResourceSnapshotMcpServer[]
        {
            new()
            {
                EndpointUrl = "http://localhost:8000",
                Tools =
                [
                    new Tool
                    {
                        Name = "query",
                        Description = "Runs a SQL query",
                        InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"sql\":{\"type\":\"string\"}}}").RootElement
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(servers, options);
        var roundTripped = JsonSerializer.Deserialize<Aspire.Cli.Backchannel.ResourceSnapshotMcpServer[]>(json, options);

        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped);
        Assert.Equal("http://localhost:8000", roundTripped[0].EndpointUrl);
        Assert.Single(roundTripped[0].Tools);
        Assert.Equal("query", roundTripped[0].Tools[0].Name);
    }

    [Fact]
    public void JsonSerializerOptionsCanSerializeAndDeserializeDictionaryStringJsonElement()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sql"] = JsonDocument.Parse("\"select 1\"").RootElement,
            ["limit"] = JsonDocument.Parse("1").RootElement
        };

        var json = JsonSerializer.Serialize(payload, options);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, options);

        Assert.NotNull(roundTripped);
        Assert.Equal("select 1", roundTripped["sql"].GetString());
        Assert.Equal(1, roundTripped["limit"].GetInt32());
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializePublishingActivityWithoutHierarchyMetadata()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        var json =
            """
            {
              "Type": "step",
              "Data": {
                "Id": "step-1",
                "StatusText": "Prepare",
                "CompletionState": "InProgress"
              }
            }
            """;

        var activity = JsonSerializer.Deserialize<PublishingActivity>(json, options);

        Assert.NotNull(activity);
        Assert.Equal(PublishingActivityTypes.Step, activity.Type);
        Assert.Equal("step-1", activity.Data.Id);
        Assert.Equal("Prepare", activity.Data.StatusText);
        Assert.Null(activity.Data.ParentStepId);
        Assert.Null(activity.Data.HierarchyLevel);
        Assert.Null(activity.Data.CompletionMessage);
        Assert.Equal(CompletionStates.InProgress, activity.Data.CompletionState);
    }
}
