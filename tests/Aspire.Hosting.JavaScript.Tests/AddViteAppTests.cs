// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only
#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddViteAppTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task VerifyDefaultDockerfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        // Create vite directory to ensure manifest generates correct relative build context path
        var viteDir = Path.Combine(workspace.Path, "vite");
        Directory.CreateDirectory(viteDir);

        // Create a lock file so npm ci is used in the Dockerfile
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true);

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var expectedManifest = $$"""
            {
              "type": "container.v1",
              "build": {
                "context": "vite",
                "dockerfile": "vite.Dockerfile",
                "buildOnly": true
              },
              "env": {
                "NODE_ENV": "production",
                "PORT": "{vite.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());

        var dockerfilePath = Path.Combine(workspace.Path, "vite.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);
        var expectedDockerfile = $$"""
            FROM node:22-slim
            WORKDIR /app
            COPY package*.json ./
            RUN --mount=type=cache,target=/root/.npm npm ci
            COPY . .
            RUN npm run build

            """.Replace("\r\n", "\n");
        Assert.Equal(expectedDockerfile, dockerfileContents);

        var dockerBuildAnnotation = nodeApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.False(dockerBuildAnnotation.HasEntrypoint);

        var containerFilesSource = nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/dist", containerFilesSource.SourcePath);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsStaticWebsite()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(workspace.Path, "vite");
        Directory.CreateDirectory(viteDir);
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite();

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "vite.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var dockerBuildAnnotation = nodeApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        var containerFilesSource = nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/dist", containerFilesSource.SourcePath);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsStaticWebsiteWithApiProxy()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(workspace.Path, "vite");
        Directory.CreateDirectory(viteDir);
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var apiDir = Path.Combine(workspace.Path, "api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "package-lock.json"), "empty");

        var api = builder.AddNodeApp("api", apiDir, "server.js")
            .WithHttpEndpoint(name: "http", targetPort: 3001);

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite("/api", api);

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "vite.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task PublishAsStaticWebsiteSetsYarpEnvironmentVariables()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(workspace.Path, "vite");
        Directory.CreateDirectory(viteDir);
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite();

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(nodeApp.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("true", env["YARP_ENABLE_STATIC_FILES"]);
        // PORT is set by the endpoint — verify it's present
        Assert.True(env.ContainsKey("PORT"), "PORT should be set by the HTTP endpoint");
    }

    [Fact]
    public async Task PublishAsStaticWebsiteWithApiProxySetsReverseProxyEnvironmentVariables()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(workspace.Path, "vite");
        Directory.CreateDirectory(viteDir);
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var apiDir = Path.Combine(workspace.Path, "api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "package-lock.json"), "empty");

        var api = builder.AddNodeApp("api", apiDir, "server.js")
            .WithHttpEndpoint(name: "http", targetPort: 3001);

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite("/api", api);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(nodeApp.Resource, DistributedApplicationOperation.Publish);

        Assert.Equal("true", env["YARP_ENABLE_STATIC_FILES"]);
        Assert.Equal("api", env["REVERSEPROXY__ROUTES__api__CLUSTERID"]);
        Assert.Equal("/api/{**catch-all}", env["REVERSEPROXY__ROUTES__api__MATCH__PATH"]);
        Assert.False(env.ContainsKey("REVERSEPROXY__ROUTES__api__TRANSFORMS__0__PATHREMOVEPREFIX"), "StripPrefix defaults to false");
        Assert.True(env.ContainsKey("REVERSEPROXY__CLUSTERS__api__DESTINATIONS__destination1__ADDRESS"));
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsStaticWebsiteWithCustomOutputPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "angular");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("angular", appDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite(o => o.OutputPath = "dist/browser");

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "angular.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var dockerBuildAnnotation = nodeApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        var containerFilesSource = nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/dist/browser", containerFilesSource.SourcePath);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsNodeServer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "vite");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("vite", appDir)
            .WithNpm(install: true)
            .PublishAsNodeServer(".output/server/index.mjs", ".output");

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "vite.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var containerFilesSource = nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/.output", containerFilesSource.SourcePath);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsNextStandalone()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var nextDir = Path.Combine(workspace.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir)
            .WithNpm(install: true);

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "nextjs.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        Assert.Empty(nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>());
    }

    [Fact]
    public async Task VerifyDockerfileWhenNextJsAppUsesPnpm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var nextDir = Path.Combine(workspace.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "pnpm-lock.yaml"), "");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir)
            .WithPnpm(install: true);

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "nextjs.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWhenPackageScriptUsesPnpm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "nuxt");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "pnpm-lock.yaml"), "");

        var nodeApp = builder.AddViteApp("nuxt", appDir)
            .WithPnpm(install: true)
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "nuxt.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWhenPackageScriptUsesBun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "nuxt");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "bun.lock"), "");

        var nodeApp = builder.AddViteApp("nuxt", appDir)
            .WithBun(install: true)
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "nuxt.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromNvmrc()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create an .nvmrc file
        File.WriteAllText(Path.Combine(workspace.Path, ".nvmrc"), "18.20.0");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));

        // Should detect version 18 from .nvmrc
        Assert.Contains("FROM node:18-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromNodeVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a .node-version file
        File.WriteAllText(Path.Combine(workspace.Path, ".node-version"), "v21.5.0");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));

        // Should detect version 21 from .node-version
        Assert.Contains("FROM node:21-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromToolVersions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a .tool-versions file
        var toolVersions = """
            ruby 3.2.0
            nodejs 19.8.1
            python 3.11.0
            """;
        File.WriteAllText(Path.Combine(workspace.Path, ".tool-versions"), toolVersions);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));

        // Should detect version 19 from .tool-versions
        Assert.Contains("FROM node:19-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromToolVersionsUsingTabs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a .tool-versions file using tabs between the tool name and version
        var toolVersions = string.Join(Environment.NewLine,
        [
            "ruby 3.2.0",
            "nodejs\t19.8.1",
            "python 3.11.0"
        ]);
        File.WriteAllText(Path.Combine(workspace.Path, ".tool-versions"), toolVersions);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));

        Assert.Contains("FROM node:19-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileIgnoresPackageJsonEnginesWhenNoPinnedVersionExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var packageJson = """
            {
              "name": "test-vite",
              "engines": {
                "node": "18.x"
              }
            }
            """;
        File.WriteAllText(Path.Combine(workspace.Path, "package.json"), packageJson);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));

        Assert.Contains("FROM node:22-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileDefaultsTo22WhenNoVersionFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Don't create any version files - should default to 22
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));

        // Should default to version 22
        Assert.Contains("FROM node:22-slim", dockerfileContents);
    }

    [Theory]
    [InlineData("18", "node:18-slim")]
    [InlineData("v20.1.0", "node:20-slim")]
    [InlineData(">=18.12", "node:18-slim")]
    [InlineData("^16.0.0", "node:16-slim")]
    [InlineData("~19.5.0", "node:19-slim")]
    public async Task VerifyDockerfileHandlesVariousVersionFormats(string versionString, string expectedImage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, ".nvmrc"), versionString);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));

        Assert.Contains($"FROM {expectedImage}", dockerfileContents);
    }

    [Fact]
    public async Task VerifyCustomBaseImage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var customImage = "node:22-myspecialimage";
        var nodeApp = builder.AddViteApp("vite", workspace.Path)
            .WithNpm(install: true)
            .WithDockerfileBaseImage(buildImage: customImage);

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, workspace.Path);

        // Verify the manifest structure
        Assert.Equal("container.v1", manifest["type"]?.ToString());

        // Verify the Dockerfile contains the custom base image
        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "vite.Dockerfile"));
        Assert.Contains($"FROM {customImage}", dockerfileContents);
    }

    [Fact]
    public void AddViteApp_WithViteConfigPath_AppliesConfigArgument()
    {
        var builder = DistributedApplication.CreateBuilder();

        var viteApp = builder.AddViteApp("test-app", "./test-app")
            .WithViteConfig("custom.vite.config.js");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the first command line args annotation (WithDebugSupport adds additional callbacks)
        var commandLineArgsAnnotation = nodeResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().First();
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args, nodeResource);
        commandLineArgsAnnotation.Callback(context);

        // Should include --config argument
        Assert.Contains("--config", args);
        var configIndex = args.IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < args.Count);
        Assert.Equal("custom.vite.config.js", args[configIndex + 1]);
    }

    [Fact]
    public void AddViteApp_WithoutViteConfigPath_DoesNotApplyConfigArgument()
    {
        var builder = DistributedApplication.CreateBuilder();

        var viteApp = builder.AddViteApp("test-app", "./test-app");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the first command line args annotation (WithDebugSupport adds additional callbacks)
        var commandLineArgsAnnotation = nodeResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().First();
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args, nodeResource);
        commandLineArgsAnnotation.Callback(context);

        // Should NOT include --config argument in base args
        Assert.DoesNotContain("--config", args);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WithExistingConfigArgument_ReplacesConfigPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create node_modules directory for wrapper config generation
        Directory.CreateDirectory(Path.Combine(workspace.Path, "node_modules"));

        // Create a vite config file
        var viteConfigPath = Path.Combine(workspace.Path, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", workspace.Path)
            .WithViteConfig("vite.config.js");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the HttpsCertificateConfigurationCallbackAnnotation
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        // Set up a context to invoke the callback with an existing --config argument
        var args = new List<object> { "run", "dev", "--", "--port", "3000", "--config", "vite.config.js" };
        var env = new Dictionary<string, object>();

        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = app.Services }),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        // Invoke the callback
        await certConfigAnnotation.Callback(context);

        // Verify the existing --config was replaced with the Aspire wrapper path
        var configIndex = args.IndexOf("--config");
        Assert.True(configIndex >= 0);
        Assert.True(configIndex + 1 < args.Count);
        var newConfigPath = args[configIndex + 1] as string;
        Assert.NotNull(newConfigPath);
        Assert.Contains("aspire.", newConfigPath);
        Assert.Contains(Path.Combine("node_modules", ".aspire"), newConfigPath);

        // Verify environment variables were set
        Assert.Contains("TLS_CONFIG_PFX", env.Keys);
        Assert.IsType<ReferenceExpression>(env["TLS_CONFIG_PFX"]);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WithoutExistingConfigArgument_DetectsDefaultConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create node_modules directory for wrapper config generation
        Directory.CreateDirectory(Path.Combine(workspace.Path, "node_modules"));

        // Create a default vite config file that would be auto-detected
        var viteConfigPath = Path.Combine(workspace.Path, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", workspace.Path);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the HttpsCertificateConfigurationCallbackAnnotation
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        // Set up a context without --config argument
        var args = new List<object> { "run", "dev", "--", "--port", "3000" };
        var env = new Dictionary<string, object>();

        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = app.Services }),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        // Invoke the callback
        await certConfigAnnotation.Callback(context);

        // Verify a --config was added with Aspire-specific path
        var configIndex = args.IndexOf("--config");
        Assert.True(configIndex >= 0);
        Assert.True(configIndex + 1 < args.Count);
        var newConfigPath = args[configIndex + 1] as string;
        Assert.NotNull(newConfigPath);
        Assert.Contains("aspire.vite.config.js", newConfigPath);

        // Verify environment variables were set
        Assert.Contains("TLS_CONFIG_PFX", env.Keys);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WithMissingConfigFile_DoesNotAddConfigArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Don't create any vite config file
        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", workspace.Path);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the HttpsCertificateConfigurationCallbackAnnotation
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        // Set up a context without --config argument
        var args = new List<object> { "run", "dev", "--", "--port", "3000" };
        var env = new Dictionary<string, object>();

        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        // Invoke the callback
        await certConfigAnnotation.Callback(context);

        // Verify no --config was added
        Assert.DoesNotContain("--config", args);

        // Environment variables should NOT be set
        Assert.Empty(env);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WithMissingNodeModules_PreservesConfigArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var viteConfigPath = Path.Combine(workspace.Path, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        builder.AddViteApp("test-app", workspace.Path);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        var args = new List<object> { "run", "dev", "--", "--port", "3000", "--config", viteConfigPath };
        var env = new Dictionary<string, object>();

        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = app.Services }),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        await certConfigAnnotation.Callback(context);

        Assert.Equal(["run", "dev", "--", "--port", "3000", "--config", viteConfigPath], args);
        Assert.Empty(env);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WithPassword_SetsPasswordEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create node_modules directory for wrapper config generation
        Directory.CreateDirectory(Path.Combine(workspace.Path, "node_modules"));

        // Create a vite config file
        var viteConfigPath = Path.Combine(workspace.Path, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", workspace.Path);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the HttpsCertificateConfigurationCallbackAnnotation
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        // Set up a context with a password
        var args = new List<object> { "run", "dev", "--", "--port", "3000" };
        var env = new Dictionary<string, object>();

        // Create a mock password provider
        var password = new TestValueProvider("test-password");

        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = app.Services }),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = password,
            CancellationToken = CancellationToken.None
        };

        // Invoke the callback
        await certConfigAnnotation.Callback(context);

        // Verify both PFX and password environment variables were set
        Assert.Contains("TLS_CONFIG_PFX", env.Keys);
        Assert.Contains("TLS_CONFIG_PASSWORD", env.Keys);
        Assert.Equal(password, env["TLS_CONFIG_PASSWORD"]);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WritesWrapperToNearestNodeModules()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Simulate a hoisted monorepo layout: node_modules is at the repo root, not in the app directory
        var repoRoot = Path.Combine(workspace.Path, "repo");
        var appDir = Path.Combine(repoRoot, "packages", "frontend");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(Path.Combine(repoRoot, "node_modules"));

        // Create a vite config file in the app directory
        var viteConfigPath = Path.Combine(appDir, "vite.config.ts");
        File.WriteAllText(viteConfigPath, "export default {}");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        builder.AddViteApp("test-app", appDir)
            .WithHttpsDeveloperCertificate();

        using var app = builder.Build();

        // Execute the before-start hooks which triggers SubscribeHttpsEndpointsUpdate (endpoint scheme change)
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var viteResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Now invoke the cert config callback which generates the wrapper file
        var certConfigAnnotation = viteResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        var args = new List<object> { "run", "dev", "--", "--port", "3000" };
        var env = new Dictionary<string, object>();

        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = app.Services }),
            Resource = viteResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        await certConfigAnnotation.Callback(context);

        // Verify the wrapper was written under the hoisted node_modules/.aspire (repo root, not app dir)
        var expectedDir = Path.Combine(repoRoot, "node_modules", ".aspire", "test-app");
        Assert.True(Directory.Exists(expectedDir), $"Expected .aspire directory at {expectedDir}");

        var wrapperFiles = Directory.GetFiles(expectedDir, "aspire.vite.config.ts");
        Assert.Single(wrapperFiles);

        // Verify the --config argument points to the wrapper in the hoisted location
        var configIndex = args.IndexOf("--config");
        Assert.True(configIndex >= 0);
        var configPath = args[configIndex + 1] as string;
        Assert.NotNull(configPath);
        Assert.StartsWith(expectedDir, configPath);

        // Verify wrapper content
        var wrapperContent = File.ReadAllText(wrapperFiles[0]);

        // The import path should be a relative path with forward slashes (no backslashes)
        Assert.Contains("import config from '", wrapperContent);
        Assert.DoesNotMatch(@"import config from '[^']*\\[^']*'", wrapperContent);

        // The console.log line should contain properly escaped backslashes for JavaScript
        var absoluteConfigPath = Path.GetFullPath(viteConfigPath);
        var expectedEscapedPath = absoluteConfigPath.Replace("\\", "\\\\");
        Assert.Contains($"Found original Vite configuration at \"{expectedEscapedPath}\"", wrapperContent);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_SharedNodeModules_WritesResourceSpecificWrappers()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var repoRoot = Path.Combine(workspace.Path, "repo");
        var firstAppDirectory = Path.Combine(repoRoot, "packages", "frontend-a");
        var secondAppDirectory = Path.Combine(repoRoot, "packages", "frontend-b");
        Directory.CreateDirectory(firstAppDirectory);
        Directory.CreateDirectory(secondAppDirectory);
        Directory.CreateDirectory(Path.Combine(repoRoot, "node_modules"));

        var firstConfigPath = Path.Combine(firstAppDirectory, "vite.config.ts");
        var secondConfigPath = Path.Combine(secondAppDirectory, "vite.config.ts");
        File.WriteAllText(firstConfigPath, "export default { app: 'a' }");
        File.WriteAllText(secondConfigPath, "export default { app: 'b' }");

        var builder = DistributedApplication.CreateBuilder();
        builder.AddViteApp("frontend-a", firstAppDirectory);
        builder.AddViteApp("frontend-b", secondAppDirectory);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resources = appModel.Resources.OfType<ViteAppResource>().ToDictionary(resource => resource.Name);
        var configPaths = new Dictionary<string, string>();

        foreach (var resourceName in resources.Keys)
        {
            var resource = resources[resourceName];
            var args = new List<object> { "run", "dev", "--", "--port", "3000" };
            var context = new HttpsCertificateConfigurationCallbackAnnotationContext
            {
                ExecutionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = app.Services }),
                Resource = resource,
                Arguments = args,
                EnvironmentVariables = new Dictionary<string, object>(),
                CertificatePath = ReferenceExpression.Create($"cert.pem"),
                KeyPath = ReferenceExpression.Create($"key.pem"),
                CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
                PfxPath = ReferenceExpression.Create($"cert.pfx"),
                Password = null,
                CancellationToken = CancellationToken.None
            };

            var certConfigAnnotation = resource.Annotations
                .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
                .Single();
            await certConfigAnnotation.Callback(context);

            var configIndex = args.IndexOf("--config");
            configPaths[resourceName] = Assert.IsType<string>(args[configIndex + 1]);
        }

        var firstWrapperPath = Path.Combine(repoRoot, "node_modules", ".aspire", "frontend-a", "aspire.vite.config.ts");
        var secondWrapperPath = Path.Combine(repoRoot, "node_modules", ".aspire", "frontend-b", "aspire.vite.config.ts");
        Assert.Equal(firstWrapperPath, configPaths["frontend-a"]);
        Assert.Equal(secondWrapperPath, configPaths["frontend-b"]);
        Assert.Contains(Path.GetFullPath(firstConfigPath).Replace("\\", "\\\\"), File.ReadAllText(firstWrapperPath));
        Assert.Contains(Path.GetFullPath(secondConfigPath).Replace("\\", "\\\\"), File.ReadAllText(secondWrapperPath));
    }

    [Theory]
    [InlineData("vite.config.js")]
    [InlineData("vite.config.mjs")]
    [InlineData("vite.config.ts")]
    [InlineData("vite.config.cjs")]
    [InlineData("vite.config.mts")]
    [InlineData("vite.config.cts")]
    public async Task AddViteApp_ServerAuthCertConfig_DetectsAllDefaultConfigFileFormats(string configFileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create node_modules directory for wrapper config generation
        Directory.CreateDirectory(Path.Combine(workspace.Path, "node_modules"));

        // Create the specific config file format
        var viteConfigPath = Path.Combine(workspace.Path, configFileName);
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", workspace.Path);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the HttpsCertificateConfigurationCallbackAnnotation
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        // Set up a context without --config argument
        var args = new List<object> { "run", "dev", "--", "--port", "3000" };
        var env = new Dictionary<string, object>();

        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = app.Services }),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            CertificateWithKeyPath = ReferenceExpression.Create($"cert-with-key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        // Invoke the callback
        await certConfigAnnotation.Callback(context);

        // Verify the specific config file was detected and wrapped
        var configIndex = args.IndexOf("--config");
        Assert.True(configIndex >= 0);
        var newConfigPath = args[configIndex + 1] as string;
        Assert.NotNull(newConfigPath);
        Assert.Contains($"aspire.{configFileName}", newConfigPath);
    }

    [Fact]
    public void NextJsAppHasBuildValidationStep()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var nextDir = Path.Combine(workspace.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir);

        var stepAnnotations = nodeApp.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
        Assert.NotEmpty(stepAnnotations);
    }

    [Fact]
    public void DisableBuildValidationAddsSuppressAnnotation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var nextDir = Path.Combine(workspace.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir)
            .DisableBuildValidation();

        Assert.True(nodeApp.Resource.TryGetLastAnnotation<SuppressPublishValidationAnnotation>(out _));
    }

    [Fact]
    public async Task NextJsStandaloneCheckFailsInPipelineWhenMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithResourceCleanUp(true);
        builder.Services.AddSingleton<IPipelineActivityReporter, NullPublishingActivityReporter>();

        var nextDir = Path.Combine(workspace.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "package-lock.json"), "empty");
        File.WriteAllText(Path.Combine(nextDir, "next.config.ts"), "const nextConfig = {}; export default nextConfig;");

        builder.AddNextJsApp("nextjs", nextDir);

        var app = builder.Build();
        var pipeline = new DistributedApplicationPipeline();
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<AddViteAppTests>>(),
            CancellationToken.None);

        // Pipeline throws AggregateException when multiple steps fail
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => pipeline.ExecuteAsync(context));
        // Verify our standalone check step fired with the right message
        Assert.Contains("output: \"standalone\"", ex.ToString());
    }

    // Helper class for testing IValueProvider
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    private sealed class TestValueProvider : IValueProvider
    {
        private readonly string _value;

        public TestValueProvider(string value)
        {
            _value = value;
        }

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            return new ValueTask<string?>(_value);
        }
    }
}