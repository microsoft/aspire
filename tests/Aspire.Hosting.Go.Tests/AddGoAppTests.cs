// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

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
                "--build-flags=-tags=netgo -ldflags=-s -w",
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
                "--build-flags=-gcflags=all=-N -l",
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
}
