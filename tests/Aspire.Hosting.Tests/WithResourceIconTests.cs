// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class WithResourceIconTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("app.svg", "image/svg+xml")]
    [InlineData("app.PNG", "image/png")]
    [InlineData("app.jpg", "image/jpeg")]
    [InlineData("app.jpeg", "image/jpeg")]
    [InlineData("app.ico", "image/x-icon")]
    public void WithResourceIcon_EmbedsFileRelativeToAppHost(string fileName, string mediaType)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(configureOptions: options => options.ProjectDirectory = workspace.Path);
        var content = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><circle r="8"/></svg>""");
        File.WriteAllBytes(Path.Combine(workspace.Path, fileName), content);

        var resource = builder.AddContainer("app", "image").WithIconName("Database").WithResourceIcon(fileName);
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Equal($"data:{mediaType};base64,{Convert.ToBase64String(content)}", annotation.IconData);
        Assert.Equal(string.Empty, annotation.IconName);

        resource.WithIconName("Box");
        annotation = Assert.Single(resource.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Null(annotation.IconData);
        Assert.Equal("Box", annotation.IconName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256 * 1024 + 1)]
    public void WithResourceIcon_RejectsInvalidSize(int length)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create();
        var path = Path.Combine(workspace.Path, "app.png");
        File.WriteAllBytes(path, new byte[length]);
        var resource = builder.AddContainer("app", "image");

        Assert.Throws<ArgumentException>(() => resource.WithResourceIcon(path));
        Assert.Empty(resource.Resource.Annotations.OfType<ResourceIconAnnotation>());
    }

    [Fact]
    public void WithResourceIcon_RejectsUnsupportedAndMissingFiles()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("app", "image");
        Assert.Throws<ArgumentNullException>(() => resource.WithResourceIcon(null!));
        Assert.Throws<ArgumentException>(() => resource.WithResourceIcon(" "));
        Assert.Throws<ArgumentException>(() => resource.WithResourceIcon("app.html"));
        Assert.Throws<FileNotFoundException>(() => resource.WithResourceIcon("missing-icon.png"));
    }
}
