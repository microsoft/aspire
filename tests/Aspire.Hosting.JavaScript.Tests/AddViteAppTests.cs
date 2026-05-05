// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only
#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddViteAppTests
{
    [Fact]
    public async Task VerifyDefaultDockerfile()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // Create vite directory to ensure manifest generates correct relative build context path
        var viteDir = Path.Combine(tempDir.Path, "vite");
        Directory.CreateDirectory(viteDir);

        // Create a lock file so npm ci is used in the Dockerfile
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true);

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

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

        var dockerfilePath = Path.Combine(tempDir.Path, "vite.Dockerfile");
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
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(tempDir.Path, "vite");
        Directory.CreateDirectory(viteDir);
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite();

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "vite.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var dockerBuildAnnotation = nodeApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        var containerFilesSource = nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/dist", containerFilesSource.SourcePath);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsStaticWebsiteWithApiProxy()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(tempDir.Path, "vite");
        Directory.CreateDirectory(viteDir);
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var apiDir = Path.Combine(tempDir.Path, "api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "package-lock.json"), "empty");

        var api = builder.AddNodeApp("api", apiDir, "server.js")
            .WithHttpEndpoint(name: "http", targetPort: 3001);

        var nodeApp = builder.AddViteApp("vite", viteDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite("/api", api);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "vite.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task PublishAsStaticWebsiteSetsYarpEnvironmentVariables()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(tempDir.Path, "vite");
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
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var viteDir = Path.Combine(tempDir.Path, "vite");
        Directory.CreateDirectory(viteDir);
        File.WriteAllText(Path.Combine(viteDir, "package-lock.json"), "empty");

        var apiDir = Path.Combine(tempDir.Path, "api");
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
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "angular");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("angular", appDir)
            .WithNpm(install: true)
            .PublishAsStaticWebsite(o => o.OutputPath = "dist/browser");

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "angular.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var dockerBuildAnnotation = nodeApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        var containerFilesSource = nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/dist/browser", containerFilesSource.SourcePath);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsNodeServer()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "vite");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddViteApp("vite", appDir)
            .WithNpm(install: true)
            .PublishAsNodeServer(".output/server/index.mjs", ".output");

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "vite.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var containerFilesSource = nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/.output", containerFilesSource.SourcePath);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsNextStandalone()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var nextDir = Path.Combine(tempDir.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir)
            .WithNpm(install: true);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "nextjs.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        Assert.Empty(nodeApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>());
    }

    [Fact]
    public async Task VerifyDockerfileWhenNextJsAppUsesPnpm()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var nextDir = Path.Combine(tempDir.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "pnpm-lock.yaml"), "");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir)
            .WithPnpm(install: true);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "nextjs.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWhenNpmScriptUsesPnpm()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "nuxt");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "pnpm-lock.yaml"), "");

        var nodeApp = builder.AddViteApp("nuxt", appDir)
            .WithPnpm(install: true)
            .PublishAsNpmScript("start");

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "nuxt.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromNvmrc()
    {
        using var tempDir = new TestTempDirectory();

        // Create an .nvmrc file
        File.WriteAllText(Path.Combine(tempDir.Path, ".nvmrc"), "18.20.0");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));

        // Should detect version 18 from .nvmrc
        Assert.Contains("FROM node:18-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromNodeVersion()
    {
        using var tempDir = new TestTempDirectory();

        // Create a .node-version file
        File.WriteAllText(Path.Combine(tempDir.Path, ".node-version"), "v21.5.0");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));

        // Should detect version 21 from .node-version
        Assert.Contains("FROM node:21-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromToolVersions()
    {
        using var tempDir = new TestTempDirectory();

        // Create a .tool-versions file
        var toolVersions = """
            ruby 3.2.0
            nodejs 19.8.1
            python 3.11.0
            """;
        File.WriteAllText(Path.Combine(tempDir.Path, ".tool-versions"), toolVersions);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));

        // Should detect version 19 from .tool-versions
        Assert.Contains("FROM node:19-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileWithNodeVersionFromToolVersionsUsingTabs()
    {
        using var tempDir = new TestTempDirectory();

        // Create a .tool-versions file using tabs between the tool name and version
        var toolVersions = string.Join(Environment.NewLine,
        [
            "ruby 3.2.0",
            "nodejs\t19.8.1",
            "python 3.11.0"
        ]);
        File.WriteAllText(Path.Combine(tempDir.Path, ".tool-versions"), toolVersions);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));

        Assert.Contains("FROM node:19-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileIgnoresPackageJsonEnginesWhenNoPinnedVersionExists()
    {
        using var tempDir = new TestTempDirectory();

        var packageJson = """
            {
              "name": "test-vite",
              "engines": {
                "node": "18.x"
              }
            }
            """;
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), packageJson);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));

        Assert.Contains("FROM node:22-slim", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileDefaultsTo22WhenNoVersionFound()
    {
        using var tempDir = new TestTempDirectory();

        // Don't create any version files - should default to 22
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));

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
        using var tempDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(tempDir.Path, ".nvmrc"), versionString);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm();

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));

        Assert.Contains($"FROM {expectedImage}", dockerfileContents);
    }

    [Fact]
    public async Task VerifyCustomBaseImage()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var customImage = "node:22-myspecialimage";
        var nodeApp = builder.AddViteApp("vite", tempDir.Path)
            .WithNpm(install: true)
            .WithDockerfileBaseImage(buildImage: customImage);

        var manifest = await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        // Verify the manifest structure
        Assert.Equal("container.v1", manifest["type"]?.ToString());

        // Verify the Dockerfile contains the custom base image
        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "vite.Dockerfile"));
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
        using var tempDir = new TestTempDirectory();

        // Create node_modules/.bin directory for Aspire config generation
        var nodeModulesBinDir = Path.Combine(tempDir.Path, "node_modules", ".bin");
        Directory.CreateDirectory(nodeModulesBinDir);

        // Create a vite config file
        var viteConfigPath = Path.Combine(tempDir.Path, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", tempDir.Path)
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
            ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        // Invoke the callback
        await certConfigAnnotation.Callback(context);

        // Verify a new --config was added with Aspire-specific path
        var configIndex = args.IndexOf("--config");
        Assert.True(configIndex >= 0);
        Assert.True(configIndex + 1 < args.Count);
        var newConfigPath = args[configIndex + 1] as string;
        Assert.NotNull(newConfigPath);
        Assert.Contains("aspire.", newConfigPath);
        Assert.Contains("node_modules", newConfigPath);

        // Verify environment variables were set
        Assert.Contains("TLS_CONFIG_PFX", env.Keys);
        Assert.IsType<ReferenceExpression>(env["TLS_CONFIG_PFX"]);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WithoutExistingConfigArgument_DetectsDefaultConfig()
    {
        using var tempDir = new TestTempDirectory();

        // Create node_modules/.bin directory for Aspire config generation
        var nodeModulesBinDir = Path.Combine(tempDir.Path, "node_modules", ".bin");
        Directory.CreateDirectory(nodeModulesBinDir);

        // Create a default vite config file that would be auto-detected
        var viteConfigPath = Path.Combine(tempDir.Path, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", tempDir.Path);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the HttpsCertificateConfigurationCallbackAnnotation
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        // Set up a context without --config argument (simulating default behavior)
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
        using var tempDir = new TestTempDirectory();

        // Don't create any vite config file
        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", tempDir.Path);

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
            PfxPath = ReferenceExpression.Create($"cert.pfx"),
            Password = null,
            CancellationToken = CancellationToken.None
        };

        // Invoke the callback
        await certConfigAnnotation.Callback(context);

        // Verify no --config was added since no default config file exists
        Assert.DoesNotContain("--config", args);

        // Environment variables should NOT be set if there was no config to wrap
        Assert.Empty(env);
    }

    [Fact]
    public async Task AddViteApp_ServerAuthCertConfig_WithPassword_SetsPasswordEnvironmentVariable()
    {
        using var tempDir = new TestTempDirectory();

        // Create node_modules/.bin directory for Aspire config generation
        var nodeModulesBinDir = Path.Combine(tempDir.Path, "node_modules", ".bin");
        Directory.CreateDirectory(nodeModulesBinDir);

        // Create a vite config file
        var viteConfigPath = Path.Combine(tempDir.Path, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", tempDir.Path);

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
            ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            Resource = nodeResource,
            Arguments = args,
            EnvironmentVariables = env,
            CertificatePath = ReferenceExpression.Create($"cert.pem"),
            KeyPath = ReferenceExpression.Create($"key.pem"),
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
    public async Task AddViteApp_ServerAuthCertConfig_EscapesBackslashesInAbsoluteConfigPath()
    {
        using var tempDir = new TestTempDirectory();

        // Create a subdirectory path that will contain backslashes on Windows
        var subDir = Path.Combine(tempDir.Path, "my-app", "frontend");
        Directory.CreateDirectory(subDir);

        // Create node_modules/.bin directory for Aspire config generation
        var nodeModulesBinDir = Path.Combine(subDir, "node_modules", ".bin");
        Directory.CreateDirectory(nodeModulesBinDir);

        // Create a vite config file
        var viteConfigPath = Path.Combine(subDir, "vite.config.js");
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", subDir);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        // Get the HttpsCertificateConfigurationCallbackAnnotation
        var certConfigAnnotation = nodeResource.Annotations
            .OfType<HttpsCertificateConfigurationCallbackAnnotation>()
            .Single();

        // Set up a context without --config argument (simulating default behavior)
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

        // Read the generated Aspire Vite config file
        var generatedConfigContent = File.ReadAllText(newConfigPath);

        // Verify the generated config contains the absolute path with properly escaped backslashes
        // The absolute path should have backslashes escaped as \\\\ in the JavaScript string
        var absoluteConfigPath = Path.GetFullPath(viteConfigPath);
        var expectedEscapedPath = absoluteConfigPath.Replace("\\", "\\\\");

        Assert.Contains($"console.log('Found original Vite configuration at \"{expectedEscapedPath}\"')", generatedConfigContent);

        // Verify the import statement uses forward slashes for the relative path
        Assert.Contains("import config from '", generatedConfigContent);
        // The import path should use forward slashes (not backslashes)
        Assert.DoesNotMatch(@"import config from '[^']*\\[^']*'", generatedConfigContent);
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
        using var tempDir = new TestTempDirectory();

        // Create node_modules/.bin directory for Aspire config generation
        var nodeModulesBinDir = Path.Combine(tempDir.Path, "node_modules", ".bin");
        Directory.CreateDirectory(nodeModulesBinDir);

        // Create the specific config file format
        var viteConfigPath = Path.Combine(tempDir.Path, configFileName);
        File.WriteAllText(viteConfigPath, "export default {}");

        var builder = DistributedApplication.CreateBuilder();
        var viteApp = builder.AddViteApp("test-app", tempDir.Path);

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
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var nextDir = Path.Combine(tempDir.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir);

        var stepAnnotations = nodeApp.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
        Assert.NotEmpty(stepAnnotations);
    }

    [Fact]
    public void DisableBuildValidationAddsSuppressAnnotation()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var nextDir = Path.Combine(tempDir.Path, "nextjs");
        Directory.CreateDirectory(nextDir);
        File.WriteAllText(Path.Combine(nextDir, "package-lock.json"), "empty");

        var nodeApp = builder.AddNextJsApp("nextjs", nextDir)
            .DisableBuildValidation();

        Assert.True(nodeApp.Resource.TryGetLastAnnotation<SuppressPublishValidationAnnotation>(out _));
    }

    [Fact]
    public async Task NextJsStandaloneCheckFailsInPipelineWhenMissing()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null).WithResourceCleanUp(true);
        builder.Services.AddSingleton<IPipelineActivityReporter, NullPublishingActivityReporter>();

        var nextDir = Path.Combine(tempDir.Path, "nextjs");
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
