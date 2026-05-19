// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIRECOMMAND001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Go.Tests;

public class AddGoAppTests
{
    // ---- Manifest: go run . (baseline) ------------------------------------

    [Fact]
    public async Task VerifyManifest_GoRunDot()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithHttpEndpoint(port: 8080, env: "PORT");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = $$"""
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "."
              ],
              "env": {
                "PORT": "{api.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "port": 8080,
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: packagePath ----------------------------------------------

    [Fact]
    public async Task VerifyManifest_AddGoApp_PackagePath()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory, packagePath: "./cmd/server");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "./cmd/server"
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyPublish_PackagePath_UsedInDockerfileBuildCommand()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path, packagePath: "./cmd/server");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    // ---- Manifest: AddGoApp build params ------------------------------------

    [Fact]
    public async Task VerifyManifest_AddGoApp_BuildTagsParam()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory, buildTags: ["netgo", "osusergo"]);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "-tags=netgo,osusergo",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_AddGoApp_LdFlagsParam()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory, ldFlags: "-X main.version=1.0.0");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "-ldflags=-X main.version=1.0.0",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_AddGoApp_GcFlagsParam()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory, gcFlags: "all=-N -l");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "-gcflags=all=-N -l",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_AddGoApp_RaceDetectorParam()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory, raceDetector: true);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "-race",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_AddGoApp_AllBuildParams()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory,
            buildTags: ["netgo"],
            ldFlags: "-s -w",
            gcFlags: "all=-N -l",
            raceDetector: true);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "-race",
                "-tags=netgo",
                "-ldflags=-s -w",
                "-gcflags=all=-N -l",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithAppArgs --------------------------------------------

    [Fact]
    public async Task VerifyManifest_WithAppArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithAppArgs("--config", "prod.yaml");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                ".",
                "--config",
                "prod.yaml"
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithModTidy does not appear in manifest ---------------

    [Fact]
    public async Task VerifyManifest_WithModTidy_DoesNotAlterMainManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // WithModTidy only creates a sibling in run mode; in publish mode the manifest is unchanged.
        var app = builder.AddGoApp("api", AppContext.BaseDirectory).WithModTidy();

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithModVendor does not appear in manifest -------------

    [Fact]
    public async Task VerifyManifest_WithModVendor_DoesNotAlterMainManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory).WithModVendor();

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithModDownload does not appear in manifest -----------

    [Fact]
    public async Task VerifyManifest_WithModDownload_DoesNotAlterMainManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory).WithModDownload();

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithDelveServer changes command to dlv -----------------

    [Fact]
    public async Task VerifyManifest_WithDelveServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithDelveServer(port: 2345);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "dlv",
              "args": [
                "--headless=true",
                "--listen=127.0.0.1:2345",
                "--api-version=2",
                "debug",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithDelveServer with build flags -----------------------

    [Fact]
    public async Task VerifyManifest_WithDelveServer_AndBuildFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory,
                buildTags: ["netgo"],
                ldFlags: "-s -w")
            .WithDelveServer(port: 2345);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "dlv",
              "args": [
                "--headless=true",
                "--listen=127.0.0.1:2345",
                "--api-version=2",
                "debug",
                "--build-flags=-tags=\u0027netgo\u0027 -ldflags=\u0027-s -w\u0027",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithDelveServer with race detector --------------------

    [Fact]
    public async Task VerifyManifest_WithDelveServer_AndRaceDetector()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory, raceDetector: true)
            .WithDelveServer(port: 2345);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "dlv",
              "args": [
                "--headless=true",
                "--listen=127.0.0.1:2345",
                "--api-version=2",
                "debug",
                "--build-flags=-race",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithDelveServer with gcflags --------------------------

    [Fact]
    public async Task VerifyManifest_WithDelveServer_AndGcFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory, gcFlags: "all=-N -l")
            .WithDelveServer(port: 2345);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "dlv",
              "args": [
                "--headless=true",
                "--listen=127.0.0.1:2345",
                "--api-version=2",
                "debug",
                "--build-flags=-gcflags=\u0027all=-N -l\u0027",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Manifest: WithDelveServer with extra program args ----------------

    [Fact]
    public async Task VerifyManifest_WithDelveServer_AndAppArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithAppArgs("--port", "9090")
            .WithDelveServer(port: 2345);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "dlv",
              "args": [
                "--headless=true",
                "--listen=127.0.0.1:2345",
                "--api-version=2",
                "debug",
                ".",
                "--",
                "--port",
                "9090"
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Publish: Dockerfile generation -------------------------------------

    [Fact]
    public async Task VerifyPublish_GeneratesDockerfile_WithGoVersionFromGoMod()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.23\n");
        File.WriteAllText(Path.Combine(sourceDir.Path, "main.go"), "package main\nfunc main() {}");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path);

        builder.Build().Run();

        var dockerfilePath = Path.Combine(outputDir.Path, "api.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), "Dockerfile should be generated in publish mode");

        var content = await File.ReadAllTextAsync(dockerfilePath);

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_UsesDefaultGoVersion_WhenGoModAbsent()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_PropagatesBuildFlagsToDockerfile()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.22\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path,
            buildTags: ["netgo", "osusergo"],
            ldFlags: "-X main.version=1.0.0",
            raceDetector: true);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_ShellQuote_HandlesEmbeddedSingleQuotes()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        // ldFlags contains an embedded single quote (e.g. a message string).
        // ShellQuote must escape it using the POSIX '\'' technique so the
        // generated Dockerfile RUN command is valid shell.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path, ldFlags: "-X main.msg=it's alive");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public void VerifyPublish_SkipsDockerfileGeneration_WhenDockerfileExists()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        // Pre-existing Dockerfile — generator should leave it alone
        File.WriteAllText(Path.Combine(sourceDir.Path, "Dockerfile"), "FROM scratch");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        var app = builder.AddGoApp("api", sourceDir.Path);

        Assert.False(app.Resource.TryGetLastAnnotation<DockerfileBuilderCallbackAnnotation>(out _),
            "No DockerfileBuilderCallbackAnnotation should be added when a Dockerfile already exists");
    }

    [Fact]
    public async Task VerifyPublish_RespectsDockerfileBaseImageAnnotation()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.22\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path)
               .WithDockerfileBaseImage(buildImage: "golang:1.22-bookworm", runtimeImage: "debian:bookworm-slim");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    // ---- Publish: private module authentication --------------------------------

    [Fact]
    public async Task VerifyPublish_WithGoPrivate_GeneratesNetrcAndGoprivate()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path)
               .WithGoPrivate(["github.com/myorg"], "github.com", usernameArgName: "GIT_USER", tokenSecretId: "gittoken");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_WithGoPrivate_CustomTokenSecretId()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path)
               .WithGoPrivate(["gitlab.mycompany.com"], "gitlab.mycompany.com", tokenSecretId: "gl_token");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    // ---- Container files (IContainerFilesDestinationResource) ---------------

    [Fact]
    public void GoAppResource_ImplementsIContainerFilesDestinationResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddGoApp("api", AppContext.BaseDirectory);

        Assert.IsType<GoAppResource>(app.Resource, exactMatch: false);
        Assert.True(app.Resource is IContainerFilesDestinationResource);
    }

    [Fact]
    public void PublishWithContainerFiles_AddsAnnotationToGoResource()
    {
        using var outputDir = new TestTempDirectory();
        // PublishWithContainerFiles only adds the annotation in publish mode.
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        var source = builder.AddResource(new GoFilesContainer("frontend", "node", "."))
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var api = builder.AddGoApp("api", AppContext.BaseDirectory);
        api.PublishWithContainerFiles(source, "/app/static");

        Assert.True(
            api.Resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out var annotations),
            "ContainerFilesDestinationAnnotation should be present after PublishWithContainerFiles");

        var annotation = Assert.Single(annotations);
        Assert.Same(source.Resource, annotation.Source);
        Assert.Equal("/app/static", annotation.DestinationPath);
    }

    [Fact]
    public async Task VerifyPublish_ContainerFiles_GeneratesFromAndCopyInstructions()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // A container resource that exposes static files (e.g. a built frontend).
        var frontend = builder.AddResource(new GoFilesContainer("frontend", "node", "."))
            .PublishAsDockerFile(c =>
                c.WithDockerfileBuilder(".", ctx => ctx.Builder.From("scratch"))
                 .WithImageTag("deterministic-tag"))
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var api = builder.AddGoApp("api", sourceDir.Path);
        api.PublishWithContainerFiles(frontend, "/app/static");

        builder.Build().Run();

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        // The builder stage ARG + FROM should reference the frontend image.
        Assert.Contains("frontend", dockerfile);
        // The runtime stage should COPY the static files from the frontend stage.
        Assert.Contains("COPY --from=", dockerfile);
        Assert.Contains("/app/dist", dockerfile);
        Assert.Contains("/app/static", dockerfile);
    }

    [Fact]
    public async Task VerifyPublish_ContainerFiles_MultipleSourcesAllPresent()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        var frontend = builder.AddResource(new GoFilesContainer("frontend", "node", "."))
            .PublishAsDockerFile(c =>
                c.WithDockerfileBuilder(".", ctx => ctx.Builder.From("scratch"))
                 .WithImageTag("frontend-tag"))
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var assets = builder.AddResource(new GoFilesContainer("assets", "node", "."))
            .PublishAsDockerFile(c =>
                c.WithDockerfileBuilder(".", ctx => ctx.Builder.From("scratch"))
                 .WithImageTag("assets-tag"))
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/public" });

        var api = builder.AddGoApp("api", sourceDir.Path);
        api.PublishWithContainerFiles(frontend, "/app/static");
        api.PublishWithContainerFiles(assets, "/app/public");

        builder.Build().Run();

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        // Both sources should have a FROM stage and COPY instruction.
        Assert.Contains("frontend", dockerfile);
        Assert.Contains("assets", dockerfile);
        Assert.Contains("/app/dist", dockerfile);
        Assert.Contains("/app/public", dockerfile);
    }

    // Minimal resource that implements IResourceWithContainerFiles so tests can
    // call PublishWithContainerFiles without depending on a real container integration.
    // ---- Issue 1: required command annotations ------------------------------

    [Fact]
    public void AddGoApp_HasRequiredCommandAnnotationForGo()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddGoApp("api", AppContext.BaseDirectory);

        Assert.True(
            app.Resource.TryGetAnnotationsOfType<RequiredCommandAnnotation>(out var annotations),
            "GoAppResource should have at least one RequiredCommandAnnotation");
        Assert.Contains(annotations, a => a.Command == "go");
    }

    [Fact]
    public void WithDelveServer_AddsRequiredCommandAnnotationForDlv()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddGoApp("api", AppContext.BaseDirectory).WithDelveServer();

        Assert.True(
            app.Resource.TryGetAnnotationsOfType<RequiredCommandAnnotation>(out var annotations));
        Assert.Contains(annotations, a => a.Command == "dlv");
    }

    // ---- Issue 3: race detector not in Dockerfile ---------------------------

    [Fact]
    public async Task VerifyPublish_RaceDetector_NotPropagatedToDockerfile()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path, raceDetector: true);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    // ---- Issue 4: mod tool ordering -----------------------------------------

    [Fact]
    public void WithModTidy_ThenWithModVendor_VendorWaitsForTidy()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        builder.AddGoApp("api", AppContext.BaseDirectory)
               .WithModTidy()
               .WithModVendor();

        var tidyResource = builder.Resources.First(r => r.Name == "api-mod-tidy");
        var vendorResource = builder.Resources.First(r => r.Name == "api-mod-vendor");

        // The vendor sibling must carry a WaitAnnotation pointing to tidy.
        Assert.True(
            vendorResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations),
            "vendor sibling should have a WaitAnnotation");
        Assert.Contains(waitAnnotations, w => w.Resource == tidyResource);
    }

    [Fact]
    public void WithModTidy_ThenWithModDownload_DownloadWaitsForTidy()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        builder.AddGoApp("api", AppContext.BaseDirectory)
               .WithModTidy()
               .WithModDownload();

        var tidyResource = builder.Resources.First(r => r.Name == "api-mod-tidy");
        var downloadResource = builder.Resources.First(r => r.Name == "api-mod-download");

        Assert.True(
            downloadResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations));
        Assert.Contains(waitAnnotations, w => w.Resource == tidyResource);
    }

    [Fact]
    public void WithModTidy_ThenWithModVendor_ThenWithModDownload_DownloadWaitsForVendor()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        builder.AddGoApp("api", AppContext.BaseDirectory)
               .WithModTidy()
               .WithModVendor()
               .WithModDownload();

        var vendorResource = builder.Resources.First(r => r.Name == "api-mod-vendor");
        var downloadResource = builder.Resources.First(r => r.Name == "api-mod-download");

        Assert.True(
            downloadResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations));
        Assert.Contains(waitAnnotations, w => w.Resource == vendorResource);
    }

    // ---- Issue 5: non-root user in Dockerfile --------------------------------

    [Fact]
    public async Task VerifyPublish_RuntimeStage_HasNonRootUser_Alpine()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_RuntimeStage_HasNonRootUser_NonAlpine()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(sourceDir.Path, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.Path)
               .WithDockerfileBaseImage(runtimeImage: "debian:bookworm-slim");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.Path, "api.Dockerfile"));

        await Verify(content);
    }

    private sealed class GoFilesContainer(string name, string command, string workingDirectory)
        : ExecutableResource(name, command, workingDirectory), IResourceWithContainerFiles;
}
