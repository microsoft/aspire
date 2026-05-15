// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Tests;

public class ServerReadyActionSerializationTests
{
    [Fact]
    public void ServerReadyAction_Serializes_ExpectedJsonShape()
    {
        var launchConfiguration = new ExecutableLaunchConfiguration("project")
        {
            ServerReadyAction = new ServerReadyAction
            {
                Action = ServerReadyActionAction.StartDebugging,
                Pattern = "\\bNow listening on: (https?://\\S+)",
                UriFormat = "%s",
                WebRoot = "/client",
                Name = "FollowUp",
                Config = new JsonObject
                {
                    ["type"] = "pwa-chrome",
                    ["request"] = "launch",
                    ["port"] = 1234
                },
                KillOnServerStop = true
            }
        };

        var json = JsonSerializer.Serialize(launchConfiguration);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("project", document.RootElement.GetProperty("type").GetString());

        var serverReadyAction = document.RootElement.GetProperty("serverReadyAction");
        Assert.Equal("startDebugging", serverReadyAction.GetProperty("action").GetString());
        Assert.Equal("\\bNow listening on: (https?://\\S+)", serverReadyAction.GetProperty("pattern").GetString());
        Assert.Equal("%s", serverReadyAction.GetProperty("uriFormat").GetString());
        Assert.Equal("/client", serverReadyAction.GetProperty("webRoot").GetString());
        Assert.Equal("FollowUp", serverReadyAction.GetProperty("name").GetString());
        Assert.True(serverReadyAction.GetProperty("killOnServerStop").GetBoolean());

        var config = serverReadyAction.GetProperty("config");
        Assert.Equal("pwa-chrome", config.GetProperty("type").GetString());
        Assert.Equal("launch", config.GetProperty("request").GetString());
        Assert.Equal(1234, config.GetProperty("port").GetInt32());
    }

    [Fact]
    public void ServerReadyAction_RoundTrips_KnownAction()
    {
        var json = """
        {
          "type": "project",
          "serverReadyAction": {
            "action": "debugWithChrome",
            "pattern": "listening on port ([0-9]+)",
            "uriFormat": "https://localhost:5001",
            "killOnServerStop": true
          }
        }
        """;

        var launchConfiguration = JsonSerializer.Deserialize<ExecutableLaunchConfiguration>(json);

        Assert.NotNull(launchConfiguration);
        Assert.NotNull(launchConfiguration.ServerReadyAction);
        Assert.Equal(ServerReadyActionAction.DebugWithChrome, launchConfiguration.ServerReadyAction.Action);
        Assert.Equal("listening on port ([0-9]+)", launchConfiguration.ServerReadyAction.Pattern);
        Assert.Equal("https://localhost:5001", launchConfiguration.ServerReadyAction.UriFormat);
        Assert.True(launchConfiguration.ServerReadyAction.KillOnServerStop);
        Assert.True(launchConfiguration.ServerReadyAction.Action!.Value.TryGetKind(out var kind));
        Assert.Equal(ServerReadyActionKind.DebugWithChrome, kind);
    }

    [Fact]
    public void ServerReadyAction_Allows_UnknownActionStrings()
    {
        var json = """
        {
          "type": "project",
          "serverReadyAction": {
            "action": "someFutureAction",
            "pattern": "listening on port ([0-9]+)"
          }
        }
        """;

        var launchConfiguration = JsonSerializer.Deserialize<ExecutableLaunchConfiguration>(json);

        Assert.NotNull(launchConfiguration);
        Assert.NotNull(launchConfiguration.ServerReadyAction);
        Assert.NotNull(launchConfiguration.ServerReadyAction.Action);
        Assert.Equal("someFutureAction", launchConfiguration.ServerReadyAction.Action.Value.Value);
        Assert.False(launchConfiguration.ServerReadyAction.Action.Value.TryGetKind(out _));
    }
}
