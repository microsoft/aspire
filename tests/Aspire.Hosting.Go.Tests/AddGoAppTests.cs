// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001

using Aspire.Hosting.Utils;
using Aspire.Hosting.ApplicationModel;

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
                "--listen=:2345",
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
                "--listen=:2345",
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
                "--listen=:2345",
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
                "--listen=:2345",
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
                "--listen=:2345",
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
        Assert.Contains("FROM golang:1.23-alpine AS build", content);
        Assert.Contains("FROM alpine:latest", content);
        Assert.Contains("go build -o /app/server .", content);
        Assert.Contains("ca-certificates", content);
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
        Assert.Contains("FROM golang:1.26-alpine AS build", content);
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
        Assert.Contains("-race", content);
        Assert.Contains("-tags='netgo,osusergo'", content);
        Assert.Contains("-ldflags='-X main.version=1.0.0'", content);
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
        // POSIX: 'it'\''s' is the correctly escaped form of it's inside single quotes.
        Assert.Contains("-ldflags='-X main.msg=it'\\''s alive'", content);
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
        Assert.Contains("FROM golang:1.22-bookworm AS build", content);
        Assert.Contains("FROM debian:bookworm-slim", content);
        Assert.DoesNotContain("alpine", content);
    }
}
