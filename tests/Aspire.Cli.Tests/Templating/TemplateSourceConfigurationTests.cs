// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Templating;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests.Templating;

public class TemplateSourceConfigurationTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true, "../escape")]
    [InlineData(false, "../project/templates")]
    public void ValidateRepositoryPathRejectsLogicalEscapeRegardlessOfWorkingDirectory(bool volumeRoot, string path)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = volumeRoot
            ? Path.GetPathRoot(workspace.Path)!
            : workspace.CreateDirectory("project").FullName;

        using (RemoteExecutor.Invoke(static repositoryPath =>
        {
            Assert.Throws<TemplateSourceConfigurationException>(() =>
                TemplateSourceConfiguration.ValidateRepositoryPath(repositoryPath, "templateSources.example.path"));
        }, path, options))
        {
        }
    }

    [Theory]
    [InlineData(".", ".")]
    [InlineData("./templates", "templates")]
    [InlineData("templates/../api", "api")]
    [InlineData("templates/sub/../..", ".")]
    public void RepositoryRelativePathAllowsNormalizationWithinRoot(string path, string expected)
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;

        Assert.Equal(expected, TemplateSource.GetRepositoryRelativePath(path, root));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("templates/../../escape")]
    [InlineData("./../escape")]
    public void RepositoryRelativePathRejectsLogicalEscapeAtVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;

        Assert.Throws<TemplateDiscoveryException>(() => TemplateSource.GetRepositoryRelativePath(path, root));
    }

    [Theory]
    [InlineData("\"/absolute/path\"", "templateSources.example")]
    [InlineData("\"https://example.test/templates\"", "templateSources.example")]
    [InlineData("{}", "templateSources.example")]
    [InlineData("""{"path":"relative"}""", "templateSources.example")]
    [InlineData("""{"ref":"main"}""", "templateSources.example")]
    [InlineData("""{"repository":false}""", "templateSources.example.repository")]
    [InlineData("""{"repository":"http://example.test/repo"}""", "templateSources.example.repository")]
    [InlineData("""{"repository":"https://example.test/repo","ref":42}""", "templateSources.example.ref")]
    [InlineData("""{"repository":"https://example.test/repo","path":"../escape"}""", "templateSources.example.path")]
    public void ParseRejectsInvalidCompleteSourceWithSettingPath(string json, string settingPath)
    {
        using var document = JsonDocument.Parse(json);
        var exception = Assert.Throws<TemplateSourceConfigurationException>(() =>
            TemplateSourceConfiguration.Parse(document.RootElement, "templateSources.example", requireComplete: true));
        Assert.Contains($"'{settingPath}'", exception.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ref":"release/17.0"}""")]
    [InlineData("""{"path":"templates/api"}""")]
    [InlineData("""{"ref":"main","path":"templates"}""")]
    public void ParsePermitsIncompleteObjectsOnlyDuringEditing(string json)
    {
        using var document = JsonDocument.Parse(json);
        var source = TemplateSourceConfiguration.Parse(document.RootElement, "templateSources.example", requireComplete: false);
        Assert.Null(source.Template);
        Assert.Throws<TemplateSourceConfigurationException>(() =>
            TemplateSourceConfiguration.Parse(document.RootElement, "templateSources.example", requireComplete: true));
    }

    [Fact]
    public void ParseAcceptsAbsoluteLocalPathObject()
    {
        var directory = Path.GetFullPath(".");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { path = directory }));
        var source = TemplateSourceConfiguration.Parse(document.RootElement, "templateSources.example", requireComplete: true);
        Assert.Equal(new TemplateSource(directory, null, null), source.Template);
    }

    [Theory]
    [InlineData("""{"repository":"https://example.test/repo","ref":null,"path":null}""")]
    [InlineData("""{"Repository":"https://example.test/repo","Ref":null,"Path":null}""")]
    [InlineData("""{"repository":"https://example.test/repo","futureFeature":{"enabled":true}}""")]
    public void ParseUsesConfigurationDeserializationConventions(string json)
    {
        using var document = JsonDocument.Parse(json);

        var source = TemplateSourceConfiguration.Parse(document.RootElement, "templateSources.example", requireComplete: true);

        Assert.Equal(new TemplateSource(".", "https://example.test/repo", null), source.Template);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("name.with.dots")]
    [InlineData("name:with:colons")]
    [InlineData("name\n")]
    public void ValidateNameRejectsAmbiguousConfigKeys(string name) =>
        Assert.Throws<TemplateSourceConfigurationException>(() => TemplateSourceConfiguration.ValidateName(name));
}
