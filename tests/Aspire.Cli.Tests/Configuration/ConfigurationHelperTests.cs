// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Configuration;

public class ConfigurationHelperTests(ITestOutputHelper outputHelper)
{
    private static IConfiguration BuildConfigurationFromSettingsFile(
        TemporaryWorkspace workspace, string content)
    {
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, content);

        var globalDir = workspace.CreateDirectory("global-aspire");
        var globalSettingsFile = new FileInfo(Path.Combine(globalDir.FullName, AspireConfigFile.FileName));

        var builder = new ConfigurationBuilder();
        ConfigurationHelper.RegisterSettingsFiles(builder, workspace.WorkspaceRoot, globalSettingsFile);
        return builder.Build();
    }

    [Fact]
    public void RegisterSettingsFiles_LoadsValidJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              "appHost": { "path": "MyApp.csproj" },
              "channel": "daily"
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
        Assert.Equal("daily", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesJsonComments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              // This is a comment
              "appHost": {
                "path": "MyApp.csproj" // inline comment
              },
              "channel": "stable"
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
        Assert.Equal("stable", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesTrailingCommas()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              "appHost": { "path": "MyApp.csproj", },
              "channel": "stable",
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesBlockComments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              /* Block comment */
              "channel": "daily"
            }
            """);

        Assert.Equal("daily", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesCommentsAndTrailingCommas()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              // Full-line comment
              "appHost": {
                "path": "MyApp.csproj", // trailing comma after value
              },
              /* Block comment */
              "channel": "daily", // trailing comma
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
        Assert.Equal("daily", config["channel"]);
    }
}
