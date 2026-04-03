// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddJavaScriptAppTests
{
    [Fact]
    public async Task VerifyDockerfile()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"]);

        await ManifestUtils.GetManifest(yarnApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var dockerBuildAnnotation = yarnApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.False(dockerBuildAnnotation.HasEntrypoint);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsStaticWebsiteWithoutSpaFallback()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"])
            .PublishAsStaticWebsite();

        await ManifestUtils.GetManifest(yarnApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsNodeServer()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"])
            .PublishAsNodeServer(".output/server/index.mjs", ".output");

        await ManifestUtils.GetManifest(yarnApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsNpmScript()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"])
            .PublishAsNpmScript("start", "-- --port $PORT");

        await ManifestUtils.GetManifest(yarnApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyPnpmDockerfile(bool hasLockFile)
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // Create directory to ensure manifest generates correct relative build context path
        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        if (hasLockFile)
        {
            File.WriteAllText(Path.Combine(appDir, "pnpm-lock.yaml"), string.Empty);
        }

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm(installArgs: ["--prefer-frozen-lockfile"])
            .WithBuildScript("mybuild");

        await ManifestUtils.GetManifest(pnpmApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);

        await Verify(dockerfileContents);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    [OuterloopTest("Builds a Docker image to verify the generated pnpm Dockerfile works")]
    public async Task VerifyPnpmDockerfileBuildSucceeds()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // Create app directory
        var appDir = Path.Combine(tempDir.Path, "pnpm-app");
        Directory.CreateDirectory(appDir);

        // Create a minimal package.json with no dependencies
        var packageJson = """
            {
              "name": "pnpm-test-app",
              "version": "1.0.0",
              "scripts": {
                "build": "echo 'build completed'"
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(appDir, "package.json"), packageJson);

        var pnpmApp = builder.AddJavaScriptApp("pnpm-app", appDir)
            .WithPnpm()
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(pnpmApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "pnpm-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");

        // Read the generated Dockerfile and verify it contains the corepack enable pnpm command
        var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath);
        Assert.Contains("corepack enable pnpm", dockerfileContent);

        // Modify the Dockerfile to add NODE_TLS_REJECT_UNAUTHORIZED=0 for test environments
        // that may have corporate proxies with self-signed certificates
        var modifiedDockerfile = dockerfileContent.Replace(
            "WORKDIR /app",
            "WORKDIR /app\nENV NODE_TLS_REJECT_UNAUTHORIZED=0");
        var dockerfileInContext = Path.Combine(appDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfileInContext, modifiedDockerfile);

        // Build the Docker image using docker build with host network for registry access
        var imageName = $"aspire-pnpm-test-{Guid.NewGuid():N}";
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"build --network=host -t {imageName} -f Dockerfile .",
            WorkingDirectory = appDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        Assert.NotNull(process);

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        // Clean up the image regardless of success/failure
        try
        {
            using var cleanupProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"rmi {imageName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (cleanupProcess != null)
            {
                await cleanupProcess.WaitForExitAsync();
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        // Assert the build succeeded
        Assert.True(process.ExitCode == 0, $"Docker build failed with exit code {process.ExitCode}.\nStdout: {stdout}\nStderr: {stderr}");
    }
}
