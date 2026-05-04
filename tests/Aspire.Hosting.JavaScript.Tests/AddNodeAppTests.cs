// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddNodeAppTests
{
    private static readonly MethodInfo s_polyglotWithReferenceMethod = typeof(ResourceBuilderExtensions)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(m => m.Name == nameof(ResourceBuilderExtensions.WithReference)
            && m.IsGenericMethodDefinition
            && m.GetParameters() is { Length: 5 } parameters
            && parameters[1].ParameterType == typeof(IResourceBuilder<IResource>));

    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var workingDirectory = AppContext.BaseDirectory;
        var nodeApp = builder.AddNodeApp("nodeapp", workingDirectory, "..\\foo\\app.js")
            .WithHttpEndpoint(port: 5031, env: "PORT");
        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource);

        var expectedManifest = $$"""
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "node",
              "args": [
                "..\\foo\\app.js"
              ],
              "env": {
                "NODE_ENV": "{{builder.Environment.EnvironmentName.ToLowerInvariant()}}",
                "PORT": "{nodeapp.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "port": 5031,
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

#pragma warning disable CS0618 // Type or member is obsolete
        var npmApp = builder.AddJavaScriptApp("npmapp", workingDirectory)
            .WithHttpEndpoint(port: 5032, env: "PORT");
#pragma warning restore CS0618 // Type or member is obsolete

        manifest = await ManifestUtils.GetManifest(npmApp.Resource);

        expectedManifest = $$"""
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "npm",
              "args": [
                "run",
                "dev"
              ],
              "env": {
                "NODE_ENV": "{{builder.Environment.EnvironmentName.ToLowerInvariant()}}",
                "PORT": "{npmapp.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "port": 5032,
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyDockerfile(bool includePackageJson)
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        if (includePackageJson)
        {
            File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");
            File.WriteAllText(Path.Combine(appDir, "package-lock.json"), "{}");
        }

        var nodeApp = builder.AddNodeApp("js", appDir, "app.js");

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);
        var expectedDockerfile = includePackageJson ?
            """
            FROM node:22-alpine AS build

            WORKDIR /app
            COPY package*.json ./
            RUN --mount=type=cache,target=/root/.npm npm ci
            COPY . .

            FROM node:22-alpine AS runtime

            WORKDIR /app
            COPY --from=build /app /app

            ENV NODE_ENV=production

            USER node

            ENTRYPOINT ["node","app.js"]

            """.Replace("\r\n", "\n") :
            """
            FROM node:22-alpine AS build

            WORKDIR /app
            COPY . .

            FROM node:22-alpine AS runtime

            WORKDIR /app
            COPY --from=build /app /app

            ENV NODE_ENV=production

            USER node

            ENTRYPOINT ["node","app.js"]

            """.Replace("\r\n", "\n");
        Assert.Equal(expectedDockerfile, dockerfileContents);

        var dockerBuildAnnotation = nodeApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        Assert.Empty(nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>());
    }

    [Fact]
    public async Task VerifyDockerfileWithBuildScript()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        var nodeApp = builder.AddNodeApp("js", appDir, "app.js")
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("mypm", runScriptCommand: null, cacheMount: null)
            {
                PackageFilesPatterns = { new CopyFilePattern("package*.json", "./") }
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["myinstall"]))
            .WithBuildScript("mybuild");

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);
        var expectedDockerfile = $"""
            FROM node:22-alpine AS build

            WORKDIR /app
            COPY package*.json ./
            RUN mypm myinstall
            COPY . .

            RUN mypm mybuild

            FROM node:22-alpine AS runtime

            WORKDIR /app
            COPY --from=build /app /app

            ENV NODE_ENV=production

            USER node

            ENTRYPOINT ["node","app.js"]

            """.Replace("\r\n", "\n");
        Assert.Equal(expectedDockerfile, dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileWithCustomBaseImage()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        var customBuildImage = "node:22-mySpecialBuildImage";
        var customRuntimeImage = "node:22-mySpecialRuntimeImage";
        var nodeApp = builder.AddNodeApp("js", appDir, "app.js")
            .WithNpm(install: true)
            .WithDockerfileBaseImage(customBuildImage, customRuntimeImage);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        // Verify the Dockerfile contains the custom base image
        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "js.Dockerfile"));
        Assert.Contains($"FROM {customBuildImage}", dockerfileContents);
        Assert.Contains($"FROM {customRuntimeImage}", dockerfileContents);
    }

    [Fact]
    public void AddNodeApp_DoesNotAddNpmWhenNoPackageJson()
    {
        var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "app.js"), "{}");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddNodeApp("nodeapp", tempDir.Path, "app.js");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<NodeAppResource>());

        // Verify the package manager annotation
        Assert.False(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out _));

        // Verify the install command annotation
        Assert.False(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out _));

        // Verify the JavaScriptInstallerResource resource does not exist
        Assert.Empty(appModel.Resources.OfType<JavaScriptInstallerResource>());
    }

    [Fact]
    public void AddNodeApp_AddsNpmWhenPackageJsonExists()
    {
        var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{}");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddNodeApp("nodeapp", tempDir.Path, "app.js");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<NodeAppResource>());

        // Verify the package manager annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("npm", packageManager.ExecutableName);
        Assert.Equal("run", packageManager.ScriptCommand);

        // Verify the install command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // Verify the JavaScriptInstallerResource resource exists
        var installerResources = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.NotNull(installerResources);
    }

    [Fact]
    public async Task WithRunScript_SetsCustomRunCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddNodeApp("nodeapp", ".", "app.js")
            .WithYarn()
            .WithRunScript("start", ["--my-arg1"]);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<NodeAppResource>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(nodeResource);

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("start", arg),
            arg => Assert.Equal("--my-arg1", arg));
    }

    [Fact]
    public async Task VerifyNodeAppWithContainerFilesGeneratesCorrectDockerfile()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var appDirectory = sourceDir.Path;

        // Create a simple Node.js app
        var packageJsonContent = """
            {
              "name": "test-app",
              "version": "1.0.0",
              "scripts": {
                "start": "node app.js"
              }
            }
            """;

        var appContent = """
            console.log('Hello from Node.js!');
            """;

        File.WriteAllText(Path.Combine(appDirectory, "package.json"), packageJsonContent);
        File.WriteAllText(Path.Combine(appDirectory, "app.js"), appContent);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        var nodeApp = builder.AddNodeApp("nodeapp", appDirectory, "app.js");

        // Create a source container that provides files
        var sourceFiles = builder.AddResource(new MyFilesContainer("source", "exe", "."))
            .PublishAsDockerFile(c =>
            {
                c.WithDockerfileBuilder(".", dockerfileContext =>
                {
                    dockerfileContext.Builder.From("scratch");
                })
                .WithImageTag("source-tag");
            })
            .WithAnnotation(new ContainerFilesSourceAnnotation() { SourcePath = "/app/dist" });

        // Configure NodeApp to consume the source files using the proper PublishWithContainerFiles API
        nodeApp.PublishWithContainerFiles(sourceFiles, "./static");

        var app = builder.Build();
        await app.RunAsync();

        // Verify that Dockerfile was generated for the NodeApp
        var nodeDockerfilePath = Path.Combine(outputDir.Path, "nodeapp.Dockerfile");
        Assert.True(File.Exists(nodeDockerfilePath), "Dockerfile should be generated for NodeApp");

        var dockerfileContent = File.ReadAllText(nodeDockerfilePath);

        await Verify(dockerfileContent);
    }

    [Fact]
    public async Task VerifyNodeAppWithContainerFilesFromResourceWithDashesGeneratesCorrectDockerfile()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var appDirectory = sourceDir.Path;

        // Create a simple Node.js app
        var packageJsonContent = """
            {
              "name": "test-app",
              "version": "1.0.0",
              "scripts": {
                "start": "node app.js"
              }
            }
            """;

        var appContent = """
            console.log('Hello from Node.js!');
            """;

        File.WriteAllText(Path.Combine(appDirectory, "package.json"), packageJsonContent);
        File.WriteAllText(Path.Combine(appDirectory, "app.js"), appContent);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        var nodeApp = builder.AddNodeApp("nodeapp", appDirectory, "app.js");

        // Create a source container with dashes in the name
        var sourceFiles = builder.AddResource(new MyFilesContainer("static-dev", "exe", "."))
            .PublishAsDockerFile(c =>
            {
                c.WithDockerfileBuilder(".", dockerfileContext =>
                {
                    dockerfileContext.Builder.From("scratch");
                })
                .WithImageTag("static-dev-tag");
            })
            .WithAnnotation(new ContainerFilesSourceAnnotation() { SourcePath = "/app/dist" });

        // Configure NodeApp to consume the source files using the proper PublishWithContainerFiles API
        nodeApp.PublishWithContainerFiles(sourceFiles, "./static");

        var app = builder.Build();
        await app.RunAsync();

        // Verify that Dockerfile was generated for the NodeApp
        var nodeDockerfilePath = Path.Combine(outputDir.Path, "nodeapp.Dockerfile");
        Assert.True(File.Exists(nodeDockerfilePath), "Dockerfile should be generated for NodeApp");

        var dockerfileContent = File.ReadAllText(nodeDockerfilePath);

        await Verify(dockerfileContent);
    }

    private sealed class MyFilesContainer(string name, string command, string workingDirectory)
        : ExecutableResource(name, command, workingDirectory), IResourceWithContainerFiles;

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only
#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only

    [Fact]
    public void NodeApp_WithVSCodeDebugging_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var nodeApp = builder.AddNodeApp("nodeapp", tempDir.Path, "app.js");

        var annotation = nodeApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("node", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void NodeApp_WithVSCodeDebugging_DoesNotAddAnnotationInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TestTempDirectory();

        var nodeApp = builder.AddNodeApp("nodeapp", tempDir.Path, "app.js");

        var annotation = nodeApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }

    [Fact]
    public void ViteApp_WithVSCodeDebugging_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var viteApp = builder.AddViteApp("viteapp", tempDir.Path);

        var annotation = viteApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("node", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void ViteApp_WithBrowserDebugger_CreatesChildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var viteApp = builder.AddViteApp("viteapp", tempDir.Path)
            .WithBrowserDebugger();

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var browserDebuggerResource = appModel.Resources.OfType<BrowserDebuggerResource>().SingleOrDefault();
        Assert.NotNull(browserDebuggerResource);
        Assert.Equal("viteapp-browser", browserDebuggerResource.Name);

        // Verify parent relationship
        Assert.True(browserDebuggerResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var parentRelationship = Assert.Single(relationships, r => r.Type == "Parent");
        Assert.Same(viteApp.Resource, parentRelationship.Resource);

        // Verify supports debugging annotation
        var annotation = browserDebuggerResource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("browser", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void ViteApp_WithBrowserDebugger_DefaultsToEdgeBrowser()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var viteApp = builder.AddViteApp("viteapp", tempDir.Path)
            .WithBrowserDebugger();

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var browserDebuggerResource = appModel.Resources.OfType<BrowserDebuggerResource>().Single();
        // The BrowserDebuggerResource's command is the browser name
        Assert.Equal("msedge", browserDebuggerResource.Command);
    }

    [Fact]
    public void ViteApp_WithBrowserDebugger_UsesSpecifiedBrowser()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var viteApp = builder.AddViteApp("viteapp", tempDir.Path)
            .WithBrowserDebugger(browser: "chrome");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var browserDebuggerResource = appModel.Resources.OfType<BrowserDebuggerResource>().Single();
        Assert.Equal("chrome", browserDebuggerResource.Command);
    }

    [Fact]
    public void ViteApp_WithBrowserDebugger_WithoutEndpoint_DeferredValidation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        // Create a minimal JavaScriptAppResource without endpoints
        var resource = new JavaScriptAppResource("jsapp", "npm", tempDir.Path);
        var jsApp = builder.AddResource(resource);

        // WithBrowserDebugger no longer throws immediately; endpoint validation is deferred
        // to when the launch configuration callback is actually invoked at debug time
        jsApp.WithBrowserDebugger();

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The browser debugger resource should still be created
        var browserDebuggerResource = appModel.Resources.OfType<BrowserDebuggerResource>().SingleOrDefault();
        Assert.NotNull(browserDebuggerResource);
    }

    [Fact]
    public async Task WithReferenceDispatchesNodeAppServiceReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(tempDir.Path, "app.js"), "console.log('hello');");

        var nodeApp = builder.AddNodeApp("nodeapp", tempDir.Path, "app.js")
            .WithHttpEndpoint(port: 5031, env: "PORT")
            .WithEndpoint("http", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5031);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "nodeapp.dev.internal", 5031, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            });
        var consumer = builder.AddContainer("consumer", "fake");

        InvokeWithReference(consumer, nodeApp, name: "custom-name");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("http://nodeapp.dev.internal:5031", config["services__custom-name__http__0"]);
        Assert.Equal("http://nodeapp.dev.internal:5031", config["custom_name_HTTP"]);
    }

    private static IResourceBuilder<TDestination> InvokeWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName = null,
        bool optional = false,
        string? name = null)
        where TDestination : IResourceWithEnvironment
    {
        return (IResourceBuilder<TDestination>)s_polyglotWithReferenceMethod
            .MakeGenericMethod(typeof(TDestination))
            .Invoke(null, [builder, source, connectionName, optional, name])!;
    }

#pragma warning restore ASPIREEXTENSION001 // Type is for evaluation purposes only
#pragma warning restore ASPIREJAVASCRIPT001 // Type is for evaluation purposes only
}
