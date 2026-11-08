// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Tests.Configuration;

public class AppHostDisplayNameResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void NameAppliesWhenAnAncestorConfigPathSelectsTheAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = CreateAppHost(workspace, "src/apphost.cs");
        WriteConfig(workspace.WorkspaceRoot.FullName, """{ "appHost": { "path": "src/apphost.cs", "name": "  Contoso Shop " } }""");

        Assert.Equal("Contoso Shop", AppHostDisplayNameResolver.GetConfiguredName(appHostPath));
    }

    [Fact]
    public void NameAppliesWhenConfigWithoutPathIsInTheAppHostDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = CreateAppHost(workspace, "src/apphost.cs");
        WriteConfig(Path.GetDirectoryName(appHostPath)!, """{ "appHost": { "name": "Contoso Shop" } }""");

        Assert.Equal("Contoso Shop", AppHostDisplayNameResolver.GetConfiguredName(appHostPath));
    }

    [Theory]
    [InlineData("""{ "appHost": { "path": "other/apphost.cs", "name": "Other" } }""")]
    [InlineData("""{ "appHost": { "name": "Ancestor without a path" } }""")]
    [InlineData("""{ "appHost": { "path": "src/apphost.cs", "name": "   " } }""")]
    [InlineData("""{ "appHost": { "path": "src/apphost.cs" } }""")]
    [InlineData("""{ "appHost": { "path": "src/apphost.cs", "name": """)]
    public void NameIsAbsentWhenTheNearestConfigDoesNotNameThisAppHost(string json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = CreateAppHost(workspace, "src/apphost.cs");
        WriteConfig(workspace.WorkspaceRoot.FullName, json);

        Assert.Null(AppHostDisplayNameResolver.GetConfiguredName(appHostPath));
    }

    [Fact]
    public void NearestConfigShadowsAnAncestorName()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = CreateAppHost(workspace, "src/apphost.cs");
        WriteConfig(workspace.WorkspaceRoot.FullName, """{ "appHost": { "path": "src/apphost.cs", "name": "Root" } }""");
        WriteConfig(Path.GetDirectoryName(appHostPath)!, """{ "channel": "daily" }""");

        Assert.Null(AppHostDisplayNameResolver.GetConfiguredName(appHostPath));
    }

    [Fact]
    public void ConfigRoundTripsTheName()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteConfig(workspace.WorkspaceRoot.FullName, """{ "appHost": { "path": "apphost.cs", "name": "Contoso Shop" } }""");

        var config = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName)!;
        config.Save(workspace.WorkspaceRoot.FullName);

        Assert.Equal("Contoso Shop", AspireConfigFile.Load(workspace.WorkspaceRoot.FullName)!.AppHost?.Name);
    }

    private static string CreateAppHost(TemporaryWorkspace workspace, string relativePath)
    {
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static void WriteConfig(string directory, string json)
        => File.WriteAllText(Path.Combine(directory, AspireConfigFile.FileName), json);
}
