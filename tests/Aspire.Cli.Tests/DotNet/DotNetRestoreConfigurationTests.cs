// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.DotNet;

public class DotNetRestoreConfigurationTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("AppHost.csproj")]
    [InlineData("apphost.cs")]
    public async Task ReadAsync_QueriesCurrentProjectSettingsWithoutImplicitRestore(string fileName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, fileName));
        var identity = fileName.EndsWith(".cs", StringComparison.Ordinal)
            ? projectFile.FullName + ".csproj"
            : projectFile.FullName;
        var toolsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "sdk");
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (file, items, properties, targets, options, _) =>
            {
                Assert.Equal(projectFile.FullName, file.FullName);
                Assert.Empty(items);
                Assert.Empty(targets);
                Assert.True(options.NoRestore);
                Assert.True(options.ExcludeRestorePackageImports);
                Assert.True(options.SuppressLogging);
                var values = new JsonObject(properties.Select(name => KeyValuePair.Create<string, JsonNode?>(name, JsonValue.Create(""))));
                values["MSBuildProjectFullPath"] = identity;
                values["MSBuildToolsPath"] = toolsPath;
                values["NuGetRestoreTargets"] = Path.Combine(toolsPath, "NuGet.targets");
                return (0, JsonDocument.Parse(new JsonObject { ["Properties"] = values }.ToJsonString()));
            },
            RestoreAsyncCallback = (_, _, _) => throw new InvalidOperationException("Inspection must not restore.")
        };

        var settings = await DotNetRestoreConfiguration.ReadAsync(runner, projectFile, TestContext.Current.CancellationToken);

        Assert.True(settings.UsesAmbientConfiguration);
        Assert.Equal(identity, settings.ProjectIdentity);
        Assert.Equal(Path.Combine(toolsPath, "NuGet.targets"), settings.RestoreTargets);
    }

    [Theory]
    [InlineData("RestoreConfigFile")]
    [InlineData("RestoreRootConfigDirectory")]
    [InlineData("RestoreSources")]
    [InlineData("_RestoreSourcesOverride")]
    [InlineData("NuGetRestoreTargets")]
    public async Task ReadAsync_ReportsConfigurationOverride(string propertyName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (_, _, properties, _, _, _) =>
            {
                var values = new JsonObject(properties.Select(name => KeyValuePair.Create<string, JsonNode?>(name, JsonValue.Create(""))));
                values["MSBuildToolsPath"] = workspace.WorkspaceRoot.FullName;
                values[propertyName] = Path.Combine(workspace.WorkspaceRoot.FullName, "custom");
                return (0, JsonDocument.Parse(new JsonObject { ["Properties"] = values }.ToJsonString()));
            }
        };

        var settings = await DotNetRestoreConfiguration.ReadAsync(runner, projectFile, TestContext.Current.CancellationToken);

        Assert.False(settings.UsesAmbientConfiguration);
        Assert.Equal([propertyName], settings.ConfigurationOverrides);
    }

    [Fact]
    public async Task ReadAsync_ReportsFailedInspection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (_, _, _, _, _, _) => (1, null)
        };

        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(
            () => DotNetRestoreConfiguration.ReadAsync(runner, projectFile, TestContext.Current.CancellationToken));

        Assert.Contains(projectFile.FullName, exception.Message);
    }
}
