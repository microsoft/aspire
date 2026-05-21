// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.JavaScript.Tests;

public class NodeJsPublicApiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorNodeAppResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string command = "npm";
        const string workingDirectory = ".\\app";

        var action = () => new NodeAppResource(name, command, workingDirectory);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorNodeAppResourceShouldThrowWhenCommandIsNullOrEmpty(bool isNull)
    {
        const string name = "NodeApp";
        var command = isNull ? null! : string.Empty;
        const string workingDirectory = ".\\app";

        var action = () => new NodeAppResource(name, command, workingDirectory);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(command), exception.ParamName);
    }

    [Fact]
    public void CtorNodeAppResourceShouldThrowWhenWorkingDirectoryIsNull()
    {
        const string name = "NodeApp";
        const string command = "npm";
        string workingDirectory = null!;

        var action = () => new NodeAppResource(name, command, workingDirectory);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(workingDirectory), exception.ParamName);
    }

    [Fact]
    public void AddNodeAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "NodeApp";
        const string scriptPath = ".\\app.js";

        var action = () => builder.AddNodeApp(name, ".", scriptPath);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddNodeAppShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;
        const string scriptPath = ".\\app.js";

        var action = () => builder.AddNodeApp(name, ".", scriptPath);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddNodeAppShouldThrowWhenScriptPathIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        const string name = "NodeApp";
        var scriptPath = isNull ? null! : string.Empty;

        var action = () => builder.AddNodeApp(name, ".", scriptPath);

        var exception = isNull
             ? Assert.Throws<ArgumentNullException>(action)
             : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(scriptPath), exception.ParamName);
    }

    [Fact]
    public void AddJavaScriptAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "NpmApp";
        const string workingDirectory = ".\\app";

        var action = () => builder.AddJavaScriptApp(name: name, workingDirectory);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddJavaScriptAppShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;
        const string workingDirectory = ".\\app";

        var action = () => builder.AddJavaScriptApp(name: name, workingDirectory);

        var exception = isNull
             ? Assert.Throws<ArgumentNullException>(action)
             : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddJavaScriptAppShouldThrowWhenWorkingDirectoryIsNull()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        const string name = "NpmApp";
        string appDirectory = null!;

        var action = () => builder.AddJavaScriptApp(name, appDirectory);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(appDirectory), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddJavaScriptAppShouldThrowWhenScriptNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        const string name = "NpmApp";
        const string workingDirectory = ".\\app";
        var runScriptName = isNull ? null! : string.Empty;

        var action = () => builder.AddJavaScriptApp(name, workingDirectory, runScriptName);

        var exception = isNull
             ? Assert.Throws<ArgumentNullException>(action)
             : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(runScriptName), exception.ParamName);
    }

    [Fact]
    public void PublishAsStaticWebsiteShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaScriptAppResource> builder = null!;

        var action = () => builder.PublishAsStaticWebsite();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void PublishAsNodeServerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaScriptAppResource> builder = null!;

        var action = () => builder.PublishAsNodeServer("server.js");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublishAsNodeServerShouldThrowWhenEntryPointIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddJavaScriptApp("app", ".");
        var entryPoint = isNull ? null! : string.Empty;

        var action = () => app.PublishAsNodeServer(entryPoint);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(entryPoint), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublishAsNodeServerShouldThrowWhenOutputPathIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddJavaScriptApp("app", ".");
        var outputPath = isNull ? null! : string.Empty;

        var action = () => app.PublishAsNodeServer("server.js", outputPath);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(outputPath), exception.ParamName);
    }

    [Fact]
    public void PublishAsNpmScriptShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaScriptAppResource> builder = null!;

        var action = () => builder.PublishAsNpmScript("start");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublishAsNpmScriptShouldThrowWhenStartScriptNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddJavaScriptApp("app", ".");
        var startScriptName = isNull ? null! : string.Empty;

        var action = () => app.PublishAsNpmScript(startScriptName);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(startScriptName), exception.ParamName);
    }

    [Fact]
    public void AddNextJsAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddNextJsApp("nextjs", ".");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }
}
