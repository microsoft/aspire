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

    // ---- Manifest: with build tags ----------------------------------------

    [Fact]
    public async Task VerifyManifest_WithBuildTags()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithBuildTags("netgo", "osusergo");

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

    // ---- Manifest: with ldflags -------------------------------------------

    [Fact]
    public async Task VerifyManifest_WithLdFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithLdFlags("-X main.version=1.0.0");

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

    // ---- Manifest: with build tags AND ldflags together -------------------

    [Fact]
    public async Task VerifyManifest_WithBuildTagsAndLdFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithBuildTags("netgo")
            .WithLdFlags("-s -w");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "go",
              "args": [
                "run",
                "-tags=netgo",
                "-ldflags=-s -w",
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

    // ---- Manifest: WithRaceDetector injects -race flag -------------------

    [Fact]
    public async Task VerifyManifest_WithRaceDetector()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithRaceDetector();

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

    // ---- Manifest: WithGcFlags injects -gcflags --------------------------

    [Fact]
    public async Task VerifyManifest_WithGcFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithGcFlags("all=-N -l");

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

    // ---- Manifest: WithDelveServer with race detector --------------------

    [Fact]
    public async Task VerifyManifest_WithDelveServer_AndRaceDetector()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithRaceDetector()
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

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithGcFlags("all=-N -l")
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

    // ---- Manifest: WithTidy does not appear in manifest ------------------

    [Fact]
    public async Task VerifyManifest_WithTidy_DoesNotAlterMainManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // WithTidy only creates a sibling in run mode; in publish mode the manifest is unchanged.
        var app = builder.AddGoApp("api", AppContext.BaseDirectory).WithTidy();

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

    // ---- Manifest: WithVendor does not appear in manifest ----------------

    [Fact]
    public async Task VerifyManifest_WithVendor_DoesNotAlterMainManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory).WithVendor();

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

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithBuildTags("netgo")
            .WithLdFlags("-s -w")
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
