// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECOMMAND001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Aspire.Hosting.Go.Tests;

public class AddGoAppTests(ITestOutputHelper outputHelper)
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName, packagePath: "./cmd/server");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

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
            .WithDelveServer();

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

    [Fact]
    public async Task VerifyManifest_WithDelveServer_EnableAcceptMultiClient()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithDelveServer(new DelveServerOptions { AcceptMultiClient = true });

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
                "--accept-multiclient",
                "debug",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_WithDelveServer_DisableOnlySameUser()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithDelveServer(new DelveServerOptions { OnlySameUser = false });

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
                "--only-same-user=false",
                "debug",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_WithDelveServer_ContinueOnStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithDelveServer(new DelveServerOptions { ContinueOnStart = true });

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
                "--continue",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_WithDelveServer_EnableLog()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithDelveServer(new DelveServerOptions
            {
                Log = true,
                LogOutput = "rpc,dap,debugger"
            });

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
                "--log",
                "--log-output=rpc,dap,debugger",
                "debug",
                "."
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task WithDelveServer_UsesDelveCommandWhenGoLaunchConfigurationIsSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["go"]
        };
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithDelveServer();
        var application = builder.Build();

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Equal("dlv", app.Resource.Command);
        Assert.Equal(["--headless=true", "--listen=127.0.0.1:2345", "--api-version=2", "debug", "."], commandArguments);
    }

    // ---- VS Code debugging --------------------------------------------------

    [Fact]
    public async Task WithVSCodeDebugging_PopulatesGoLaunchConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var packageDirectory = Path.Combine(sourceDir.FullName, "cmd", "server");

        var app = builder.AddGoApp("api", sourceDir.FullName,
            packagePath: "./cmd/server",
            buildTags: ["integration"],
            gcFlags: "all=-N -l",
            raceDetector: true);

        var launchConfig = await CreateLaunchConfigurationAsync(app.Resource);

        Assert.Equal("go", launchConfig.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, launchConfig.Mode);
        Assert.Equal(packageDirectory, launchConfig.Program);
        Assert.Equal(sourceDir.FullName, launchConfig.WorkingDirectory);
        Assert.Equal("-race -tags='integration' -gcflags='all=-N -l'", launchConfig.BuildFlags);
    }

    [Fact]
    public async Task WithVSCodeDebugging_OmitsBuildFlagsWhenNoneConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        var app = builder.AddGoApp("api", sourceDir.FullName);

        var launchConfig = await CreateLaunchConfigurationAsync(app.Resource);

        Assert.Null(launchConfig.BuildFlags);
    }

    [Fact]
    public async Task WithVSCodeDebugging_KeepsGoToolArgumentsInTheAppModel()
    {
        // The `go run [build flags] <pkg>` prefix is declared as launch tool arguments, so it stays part of the
        // resource's command line even during a debug session. Withholding it from the launched program is a
        // DCP-level concern (see DcpExecutorTests), which keeps the app model and the dashboard accurate.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["go"]
        };

        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var app = builder.AddGoApp("api", sourceDir.FullName,
                packagePath: "./cmd/server",
                buildTags: ["integration"],
                ldFlags: "-X main.version=1.0.0",
                gcFlags: "all=-N -l",
                raceDetector: true)
            .WithAppArgs("--config", "prod.yaml");

        var application = builder.Build();

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Collection(commandArguments,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("-race", arg),
            arg => Assert.Equal("-tags=integration", arg),
            arg => Assert.Equal("-ldflags=-X main.version=1.0.0", arg),
            arg => Assert.Equal("-gcflags=all=-N -l", arg),
            arg => Assert.Equal("./cmd/server", arg),
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("prod.yaml", arg));

        // The "go" launch configuration owns that prefix, which is what makes DCP withhold it from the debugger.
        var debugAnnotation = app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().Last();
        Assert.True(app.Resource.HasLaunchToolArgsOwnedBy(debugAnnotation));
    }

    [Fact]
    public async Task WithVSCodeDebugging_GoToolArgumentsLeadTheCommandLineRegardlessOfCallOrder()
    {
        // Regression coverage for https://github.com/microsoft/aspire/issues/18929: the tool prefix used to be
        // removed by an ordinary WithArgs callback, which silently no-opped when it ran before the callback that
        // added the prefix. Arguments added by the user must always land after the prefix, whenever they are added.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        var app = builder.AddGoApp("api", sourceDir.FullName, packagePath: "./cmd/server")
            .WithArgs("--login", "user");

        var application = builder.Build();

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Collection(commandArguments,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("./cmd/server", arg),
            arg => Assert.Equal("--login", arg),
            arg => Assert.Equal("user", arg));
    }

    [Fact]
    public async Task WithVSCodeDebugging_DoesNotRemoveGoToolArguments_WhenGoLaunchConfigurationUnsupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["python"]
        };

        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var app = builder.AddGoApp("api", sourceDir.FullName,
                packagePath: "./cmd/server",
                buildTags: ["integration"],
                raceDetector: true)
            .WithAppArgs("--config", "prod.yaml");

        var application = builder.Build();

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Collection(commandArguments,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("-race", arg),
            arg => Assert.Equal("-tags=integration", arg),
            arg => Assert.Equal("./cmd/server", arg),
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("prod.yaml", arg));
    }

    // ---- Manifest: WithDelveServer with build flags -----------------------

    [Fact]
    public async Task VerifyManifest_WithDelveServer_AndBuildFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory,
                buildTags: ["netgo"],
                ldFlags: "-s -w")
            .WithDelveServer();

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
            .WithDelveServer();

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
            .WithDelveServer();

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
            .WithDelveServer();

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.23\n");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "main.go"), "package main\nfunc main() {}");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName);

        builder.Build().Run();

        var dockerfilePath = Path.Combine(outputDir.FullName, "api.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), "Dockerfile should be generated in publish mode");

        var content = await File.ReadAllTextAsync(dockerfilePath);

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_UsesDefaultGoVersion_WhenGoModAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_PropagatesBuildFlagsToDockerfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.22\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName,
            buildTags: ["netgo", "osusergo"],
            ldFlags: "-X main.version=1.0.0",
            raceDetector: true);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_ShellQuote_HandlesEmbeddedSingleQuotes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        // ldFlags contains an embedded single quote (e.g. a message string).
        // ShellQuote must escape it using the POSIX '\'' technique so the
        // generated Dockerfile RUN command is valid shell.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName, ldFlags: "-X main.msg=it's alive");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public void VerifyPublish_SkipsDockerfileGeneration_WhenDockerfileExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        // Pre-existing Dockerfile — generator should leave it alone
        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        var app = builder.AddGoApp("api", sourceDir.FullName);

        Assert.False(app.Resource.TryGetLastAnnotation<DockerfileBuilderCallbackAnnotation>(out _),
            "No DockerfileBuilderCallbackAnnotation should be added when a Dockerfile already exists");
    }

    [Fact]
    public async Task PublishWithExistingDockerfileStillPublishesWhenPackagePathIsNotDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);

        // A mismatch is only warned about, so an existing Dockerfile that already builds the package keeps publishing.
        var app = builder.AddGoApp("api", CreateGoAppDirectoryWithDockerfile(workspace), packagePath: "./cmd/server");

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
    }

    [Fact]
    public async Task PublishPipelineWithExistingDockerfileWarnsFromValidationStepWhenPackagePathIsNotDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "validate-go-dockerfile-package-path-api");
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        var sourceDir = CreateGoAppDirectoryWithDockerfile(workspace);
        builder.AddGoApp("api", sourceDir, packagePath: "./cmd/server");

        using var app = builder.Build();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        await pipeline.ExecuteAsync(CreatePipelineContext(app));

        var warning = Assert.Single(GetWarnings(reporter));
        Assert.Equal(
            $"Go app resource 'api' is configured to run package './cmd/server', but publish is using the existing Dockerfile '{Path.Combine(sourceDir, "Dockerfile")}'. " +
            "Aspire does not change an existing Dockerfile, so the published image runs whatever that Dockerfile builds and starts. " +
            "Make sure it builds and runs that package, remove or rename the Dockerfile so Aspire can generate one, or call PublishAsDockerFile(...) and set the container entrypoint explicitly.",
            warning);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("./.")]
    public async Task PublishPipelineWithExistingDockerfileDoesNotWarnWhenPackagePathResolvesToAppDirectory(string packagePath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "validate-go-dockerfile-package-path-api");
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        // Only the literal "." is the default, but these spellings select the same package.
        builder.AddGoApp("api", CreateGoAppDirectoryWithDockerfile(workspace), packagePath: packagePath);

        using var app = builder.Build();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        await pipeline.ExecuteAsync(CreatePipelineContext(app));

        Assert.Empty(GetWarnings(reporter));
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsDefaultPackagePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);

        var app = builder.AddGoApp("api", CreateGoAppDirectoryWithDockerfile(workspace));

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsBuildFlagsWithDefaultPackagePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);

        // Build flags do not select what the Dockerfile runs, so they are not an entrypoint conflict.
        var app = builder.AddGoApp("api", CreateGoAppDirectoryWithDockerfile(workspace), buildTags: ["netgo"], ldFlags: "-s -w", gcFlags: "-N -l", raceDetector: true);

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsExplicitEntrypointOverride()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path);

        var app = builder.AddGoApp("api", CreateGoAppDirectoryWithDockerfile(workspace), packagePath: "./cmd/server")
            .PublishAsDockerFile(container => container.WithEntrypoint("/app/server"));

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
        Assert.Equal("/app/server", manifest["entrypoint"]?.ToString());
    }

    [Fact]
    public async Task PublishPipelineWithoutDockerfileDoesNotWarnWhenPackagePathIsNotDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "validate-go-dockerfile-package-path-api");
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        // Without an existing Dockerfile Aspire generates one that builds the requested package,
        // which VerifyPublish_PackagePath_UsedInDockerfileBuildCommand covers through the manifest.
        builder.AddGoApp("api", workspace.CreateDirectory("source").FullName, packagePath: "./cmd/server");

        using var app = builder.Build();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        await pipeline.ExecuteAsync(CreatePipelineContext(app));

        Assert.Empty(GetWarnings(reporter));
    }

    [Fact]
    public async Task PublishAsDockerFileWithDockerfileWarnsFromValidationStepWhenPackagePathIsNotDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "validate-go-dockerfile-package-path-api");
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        var sourceDir = CreateGoAppDirectoryWithDockerfile(workspace);
        var publishDockerfile = CreateDockerfile(sourceDir, "Dockerfile.prod");
        builder.AddGoApp("api", sourceDir, packagePath: "./cmd/server")
            .PublishAsDockerFile(container => container.WithDockerfile(sourceDir, publishDockerfile));

        using var app = builder.Build();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        await pipeline.ExecuteAsync(CreatePipelineContext(app));

        var warning = Assert.Single(GetWarnings(reporter));
        Assert.Contains("package './cmd/server'", warning);
        Assert.Contains(publishDockerfile, warning);
    }

    [Fact]
    public async Task PublishAsDockerFileWithDockerfileAndExplicitEntrypointDoesNotWarn()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "validate-go-dockerfile-package-path-api");
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        var sourceDir = CreateGoAppDirectoryWithDockerfile(workspace);
        var publishDockerfile = CreateDockerfile(sourceDir, "Dockerfile.prod");
        builder.AddGoApp("api", sourceDir, packagePath: "./cmd/server")
            .PublishAsDockerFile(container => container.WithDockerfile(sourceDir, publishDockerfile).WithEntrypoint("/app/server"));

        using var app = builder.Build();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        await pipeline.ExecuteAsync(CreatePipelineContext(app));

        Assert.Empty(GetWarnings(reporter));
    }

    [Fact]
    public async Task PublishPipelineValidationStepIsNoOpForResourceExcludedFromManifest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);

        var sourceDir = CreateGoAppDirectoryWithDockerfile(workspace);
        builder.AddGoApp("excluded", sourceDir, packagePath: "./cmd/server").ExcludeFromManifest();
        builder.AddGoApp("sibling", sourceDir);

        using var app = builder.Build();
        var pipeline = (DistributedApplicationPipeline)app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        var context = CreatePipelineContext(app);

        var validationStepNames = (await pipeline.ResolveStepsAsync(context))
            .Select(step => step.Name)
            .Where(name => name.StartsWith("validate-go-dockerfile-package-path-", StringComparison.Ordinal))
            .ToList();

        foreach (var stepName in validationStepNames)
        {
            await pipeline.ExecuteStepSequentiallyAsync(stepName, context);
        }

        Assert.Equal(
            ["validate-go-dockerfile-package-path-excluded", "validate-go-dockerfile-package-path-sibling"],
            validationStepNames.Order(StringComparer.Ordinal));
        Assert.Empty(GetWarnings(reporter));
    }

    [Fact]
    public async Task PublishPipelineRunsValidationStepBeforeImageBuildStep()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IPipelineActivityReporter, NullPublishingActivityReporter>();

        builder.AddGoApp("api", CreateGoAppDirectoryWithDockerfile(workspace), packagePath: "./cmd/server");

        using var app = builder.Build();
        var pipeline = (DistributedApplicationPipeline)app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        var steps = (await pipeline.ResolveStepsAsync(CreatePipelineContext(app)))
            .ToDictionary(step => step.Name, StringComparer.Ordinal);

        // Compute environments such as Azure Container Apps depend on the image build step without the aggregate Build step.
        var buildStepClosure = DistributedApplicationPipeline.ComputeTransitiveDependencies(steps["build-api"], steps);

        Assert.Contains(buildStepClosure, step => step.Name == "validate-go-dockerfile-package-path-api");
    }

    [Fact]
    public async Task VerifyPublish_RespectsDockerfileBaseImageAnnotation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.22\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName)
               .WithDockerfileBaseImage(buildImage: "golang:1.22-bookworm", runtimeImage: "debian:bookworm-slim");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

        await Verify(content);
    }

    // ---- Publish: private module authentication --------------------------------

    [Fact]
    public async Task VerifyPublish_WithGoPrivate_GeneratesNetrcAndGoprivate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName)
               .WithGoPrivate(["github.com/myorg"], "github.com", usernameArgName: "GIT_USER", tokenSecretId: "gittoken");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_WithGoPrivate_CustomTokenSecretId()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName)
               .WithGoPrivate(["gitlab.mycompany.com"], "gitlab.mycompany.com", tokenSecretId: "gl_token");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDir = workspace.CreateDirectory("output");
        // PublishWithContainerFiles only adds the annotation in publish mode.
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

        // A container resource that exposes static files (e.g. a built frontend).
        var frontend = builder.AddResource(new GoFilesContainer("frontend", "node", "."))
            .PublishAsDockerFile(c =>
                c.WithDockerfileBuilder(".", ctx => ctx.Builder.From("scratch"))
                 .WithImageTag("deterministic-tag"))
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var api = builder.AddGoApp("api", sourceDir.FullName);
        api.PublishWithContainerFiles(frontend, "/app/static");

        builder.Build().Run();

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

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

        var api = builder.AddGoApp("api", sourceDir.FullName);
        api.PublishWithContainerFiles(frontend, "/app/static");
        api.PublishWithContainerFiles(assets, "/app/public");

        builder.Build().Run();

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName, raceDetector: true);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName);

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_RuntimeStage_HasNonRootUser_NonAlpine()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddGoApp("api", sourceDir.FullName)
               .WithDockerfileBaseImage(runtimeImage: "debian:bookworm-slim");

        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"));

        await Verify(content);
    }

    [Fact]
    public void AddGoApp_RequestsPlatformCertificateTrustScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory);

        // On macOS and Windows, setting SSL_CERT_DIR makes Go load roots only from disk instead of the
        // platform verifier, and those platforms have no default cert files. Under the default Append
        // scope Go would then trust only Aspire's own certificates, so the bundle must include the
        // system roots as well. Linux still loads its default bundle file, so Append works there.
        var expectedScope = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? CertificateTrustScope.System
            : CertificateTrustScope.Append;
        Assert.True(app.Resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var certAnnotation));
        Assert.Equal(expectedScope, certAnnotation.Scope);
    }

    [Fact]
    public void AddGoApp_ExplicitCertificateTrustScope_OverridesDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddGoApp("api", AppContext.BaseDirectory)
            .WithCertificateTrustScope(CertificateTrustScope.Append);

        Assert.True(app.Resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var certAnnotation));
        Assert.Equal(CertificateTrustScope.Append, certAnnotation.Scope);
    }

    [Fact]
    public async Task AddGoApp_WithAppendCertificateTrustScope_LogsThatPublicRootsAreNotTrustedOnMacOSAndWindows()
    {
        // https://github.com/microsoft/aspire/issues/21198
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Go only replaces the platform roots on macOS and Windows.");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var goApp = builder.AddGoApp("api", AppContext.BaseDirectory);

        using var app = builder.Build();

        Assert.True(goApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        await annotation.Callback(new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(
                new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
                {
                    Services = app.Services
                }),
            Resource = goApp.Resource,
            Arguments = [],
            EnvironmentVariables = new Dictionary<string, object>(),
            CertificateBundlePath = ReferenceExpression.Create($"/aspire/cert.pem"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/aspire/certs"),
            Scope = CertificateTrustScope.Append,
            CancellationToken = default
        });

        // WatchAsync replays the logs already written for the resource as its first batch.
        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        await using var logs = resourceLoggerService.WatchAsync(goApp.Resource).GetAsyncEnumerator();
        Assert.True(await logs.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        // Log content is prefixed with a timestamp, for example "2026-10-22T06:54:13.6917970Z Certificate ...".
        var content = Assert.Single(logs.Current).Content;
        Assert.Equal(
            "Certificate trust scope is set to 'Append', but since Go 1.27, Go on macOS and Windows trusts only the configured certificates instead of adding them to the system roots, so public certificate authorities will not be trusted. Use 'System' to trust both.",
            content[(content.IndexOf(' ') + 1)..]);
    }

    private static string CreateGoAppDirectoryWithDockerfile(TemporaryWorkspace workspace)
    {
        var sourceDir = workspace.CreateDirectory("source");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "go.mod"), "module example.com/api\n\ngo 1.24\n");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\nENTRYPOINT [\"/app/api\"]\n");

        return sourceDir.FullName;
    }

    private static string CreateDockerfile(string directory, string fileName)
    {
        var dockerfilePath = Path.Combine(directory, fileName);

        File.WriteAllText(dockerfilePath, "FROM scratch\nENTRYPOINT [\"/app/api\"]\n");

        return dockerfilePath;
    }

    private static PipelineContext CreatePipelineContext(DistributedApplication app) => new(
        app.Services.GetRequiredService<DistributedApplicationModel>(),
        app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
        app.Services,
        app.Services.GetRequiredService<ILogger<AddGoAppTests>>(),
        CancellationToken.None);

    private static string[] GetWarnings(TestPipelineActivityReporter reporter) =>
        [.. reporter.LoggedMessages.Where(log => log.LogLevel == LogLevel.Warning).Select(log => log.Message)];

    private static async Task<GoLaunchConfiguration> CreateLaunchConfigurationAsync(IResource resource)
    {
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(resource);
        return Assert.IsType<GoLaunchConfiguration>(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(resource, callbackContext));
    }

    private sealed class GoFilesContainer(string name, string command, string workingDirectory)
        : ExecutableResource(name, command, workingDirectory), IResourceWithContainerFiles;
}
