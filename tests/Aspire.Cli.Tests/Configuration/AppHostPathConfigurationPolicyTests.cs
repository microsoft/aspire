// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Configuration;

public class AppHostPathConfigurationPolicyTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("appHost.path")]
    [InlineData("appHost:path")]
    [InlineData("APPHOST.PATH")]
    [InlineData("appHostPath")]
    [InlineData("APPHOSTPATH")]
    public void IsGloballySettableKey_ReturnsFalse_ForBlockedKeys(string key)
    {
        Assert.False(AppHostPathConfigurationPolicy.IsGloballySettableKey(key));
    }

    [Theory]
    [InlineData("appHost.language")]
    [InlineData("appHost:language")]
    [InlineData("channel")]
    [InlineData("features.defaultWatchEnabled")]
    [InlineData("")]
    public void IsGloballySettableKey_ReturnsTrue_ForOtherKeys(string key)
    {
        Assert.True(AppHostPathConfigurationPolicy.IsGloballySettableKey(key));
    }

    [Theory]
    [InlineData("""{ "appHost": { "path": "AppHost.csproj" } }""", "appHost.path")]
    [InlineData("""{ "appHostPath": "../AppHost.csproj" }""", "appHostPath")]
    [InlineData("""{ "appHost:path": "AppHost.csproj" }""", "appHost.path")]
    public void TryFindAppHostPathKey_ReturnsConfiguredKey(string json, string expectedKey)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(path, json);

        Assert.True(ConfigurationHelper.TryLoadSettingsFile(path, out var configuration));
        Assert.True(AppHostPathConfigurationPolicy.TryFindAppHostPathKey(configuration, out var key));
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    public void TryFindAppHostPathKey_ReturnsFalse_WhenFileContainsNonObjectJson(string json)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(path, json);

        Assert.False(ConfigurationHelper.TryLoadSettingsFile(path, out _));
    }

    [Fact]
    public void TryFindAppHostPathKey_ReturnsFalse_WhenNoAppHostPathIsConfigured()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(path, """{ "appHost": { "language": "typescript/nodejs" }, "channel": "daily" }""");

        Assert.True(ConfigurationHelper.TryLoadSettingsFile(path, out var configuration));
        Assert.False(AppHostPathConfigurationPolicy.TryFindAppHostPathKey(configuration, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void TryFindAppHostPathKey_ReturnsFalse_WhenFileContainsInvalidJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(path, """{ "appHost": { "path": "AppHost.csproj" }""");

        Assert.False(ConfigurationHelper.TryLoadSettingsFile(path, out _));
    }

    [Fact]
    public void TryFindAppHostPathKey_ReturnsFalse_WhenFileDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);

        Assert.False(ConfigurationHelper.TryLoadSettingsFile(path, out _));
    }
}
