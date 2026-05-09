// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class MarkdownPreviewResourceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddMarkdownPreviewAddsResourceWithInitialSnapshot()
    {
        using var tempDirectory = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = tempDirectory.Path;
        }, testOutputHelper);

        var markdown = builder.AddMarkdownPreview("readme", "README.md");

        var expectedPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "README.md"));
        Assert.Equal("readme", markdown.Resource.Name);
        Assert.Equal(expectedPath, markdown.Resource.Path);

        var snapshot = markdown.Resource.Annotations.OfType<ResourceSnapshotAnnotation>().Single().InitialSnapshot;
        Assert.Equal("MarkdownPreview", snapshot.ResourceType);
        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Contains(snapshot.Properties, p => p.Name == CustomResourceKnownProperties.Source && Equals(p.Value, "README.md"));
        Assert.Contains(snapshot.Properties, p => p.Name == "Path" && Equals(p.Value, expectedPath));
    }

    [Fact]
    public void AddMarkdownPreviewAddsViewMarkdownCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var markdown = builder.AddMarkdownPreview("readme", "README.md");

        var command = markdown.Resource.Annotations.OfType<ResourceCommandAnnotation>().Single();
        Assert.Equal("markdown-preview-view", command.Name);
        Assert.Equal("View markdown", command.DisplayName);
        Assert.Equal("Opens the Markdown file.", command.DisplayDescription);
        Assert.Equal("DocumentBulletList", command.IconName);
        Assert.True(command.IsHighlighted);
    }

    [Fact]
    public async Task ViewMarkdownCommandReturnsMarkdownResult()
    {
        using var tempDirectory = new TestTempDirectory();
        var readmePath = Path.Combine(tempDirectory.Path, "README.md");
        await File.WriteAllTextAsync(readmePath, "# Hello");

        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = tempDirectory.Path;
        }, testOutputHelper);

        var markdown = builder.AddMarkdownPreview("readme", "README.md");
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(markdown.Resource, "markdown-preview-view").DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal($"Opened Markdown file '{readmePath}'.", result.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("# Hello", result.Data.Value);
        Assert.Equal(CommandResultFormat.Markdown, result.Data.Format);
        Assert.True(result.Data.DisplayImmediately);
    }

    [Fact]
    public async Task ViewMarkdownCommandReturnsFailureForMissingFile()
    {
        using var tempDirectory = new TestTempDirectory();
        var readmePath = Path.Combine(tempDirectory.Path, "README.md");
        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = tempDirectory.Path;
        }, testOutputHelper);

        var markdown = builder.AddMarkdownPreview("readme", "README.md");
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(markdown.Resource, "markdown-preview-view").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal($"Markdown file '{readmePath}' does not exist.", result.Message);
    }
}

