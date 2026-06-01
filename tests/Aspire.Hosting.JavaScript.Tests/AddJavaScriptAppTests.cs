// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    public async Task VerifyDockerfileWhenPublishedAsPackageScript()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"])
            .PublishAsPackageScript("start", "-- --port $PORT");

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyPnpmDockerfileWhenPublishedAsPackageScript(bool hasLockFile)
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        if (hasLockFile)
        {
            File.WriteAllText(Path.Combine(appDir, "pnpm-lock.yaml"), string.Empty);
        }

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm(installArgs: ["--prefer-frozen-lockfile"])
            .WithBuildScript("mybuild")
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(pnpmApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);

        await Verify(dockerfileContents);
    }

    [Fact]
    public async Task PublishWithExistingDockerfileThrowsWhenRunScriptNameIsExplicit()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        var app = builder.AddJavaScriptApp("js", appDir, "migrate")
            .WithBun();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("runScriptName", exception.Message);
        Assert.Contains("WithRunScript", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task PublishModelWithExistingDockerfileThrowsWhenRunScriptNameIsExplicit()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        builder.AddJavaScriptApp("js", appDir, "migrate")
            .WithBun();

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifestForModel(appModel, tempDir.Path));

        Assert.Contains("runScriptName", exception.Message);
        Assert.Contains("WithRunScript", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task PublishWithExistingDockerfileThrowsWhenWithRunScriptOverridesDefault()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun()
            .WithRunScript("migrate");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("runScriptName", exception.Message);
        Assert.Contains("WithRunScript", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task PublishPipelineWithExistingDockerfileThrowsFromValidationStepWhenRunScriptNameIsExplicit()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "validate-javascript-dockerfile-run-script-js").WithResourceCleanUp(true);
        builder.Services.AddSingleton<IPipelineActivityReporter, NullPublishingActivityReporter>();

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        builder.AddJavaScriptApp("js", appDir, "migrate")
            .WithBun();

        using var app = builder.Build();
        var pipeline = new DistributedApplicationPipeline();
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<AddJavaScriptAppTests>>(),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => pipeline.ExecuteAsync(context));

        Assert.Contains("runScriptName", exception.Message);
        Assert.Contains("WithRunScript", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsImplicitDefaultRunScript()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun();

        var manifest = await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsExplicitEntrypointOverride()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        var app = builder.AddJavaScriptApp("js", appDir, "migrate")
            .WithBun()
            .PublishAsDockerFile(container => container
                .WithEntrypoint("bun")
                .WithArgs("src/migrate.ts"));

        var manifest = await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        Assert.Equal("bun", manifest["entrypoint"]?.ToString());
        Assert.Contains("src/migrate.ts", manifest.ToJsonString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsWithRunScriptMatchingDefault()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun()
            // Re-stating the default script name explicitly should not be treated as a conflict
            // with the existing Dockerfile, because the effective run script still matches the default.
            .WithRunScript("dev");

        var manifest = await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileThrowsAndIncludesArgsWhenDefaultScriptHasArgs()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(tempDir.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun()
            .WithRunScript("dev", ["--port", "8080"]);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("run script 'dev'", exception.Message);
        Assert.Contains("with args [--port, 8080]", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task VerifyPnpmDockerfileCopiesWorkspaceFileBeforeInstall()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Combine(appDir, "pnpm-workspace.yaml"), "allowBuilds: {}\n");

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm(installArgs: ["--prefer-frozen-lockfile"])
            .WithBuildScript("mybuild");

        await ManifestUtils.GetManifest(pnpmApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        var dockerfileLines = await File.ReadAllLinesAsync(dockerfilePath);

        var copyLineIndex = Array.FindIndex(
            dockerfileLines,
            line => line.StartsWith("COPY ", StringComparison.Ordinal)
                && line.Contains("pnpm-workspace.yaml", StringComparison.Ordinal));
        var installLineIndex = Array.FindIndex(dockerfileLines, line => line.Contains("pnpm install", StringComparison.Ordinal));

        Assert.NotEqual(-1, copyLineIndex);
        Assert.NotEqual(-1, installLineIndex);
        Assert.True(copyLineIndex < installLineIndex);
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

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    [OuterloopTest("Builds and runs a Docker image to verify the generated pnpm PublishAsPackageScript Dockerfile works")]
    public async Task VerifyPnpmDockerfileWhenPublishedAsPackageScriptRunsWithoutNetwork()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "pnpm-app");
        Directory.CreateDirectory(appDir);

        var packageJson = """
            {
              "name": "pnpm-runtime-test-app",
              "version": "1.0.0",
              "scripts": {
                "build": "echo 'build completed'",
                "start": "node -e \"console.log('runtime ok')\""
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(appDir, "package.json"), packageJson);

        var pnpmApp = builder.AddJavaScriptApp("pnpm-app", appDir)
            .WithPnpm()
            .WithBuildScript("build")
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(pnpmApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "pnpm-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");

        var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath);
        Assert.Contains("RUN corepack enable pnpm && pnpm --version", dockerfileContent);

        var dockerfileInContext = Path.Combine(appDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfileInContext, dockerfileContent);

        var imageName = $"aspire-pnpm-runtime-test-{Guid.NewGuid():N}";

        try
        {
            var buildResult = await RunDockerCommandAsync($"build --network=host -t {imageName} -f Dockerfile .", appDir);
            Assert.True(buildResult.ExitCode == 0, $"Docker build failed with exit code {buildResult.ExitCode}.\nStdout: {buildResult.Stdout}\nStderr: {buildResult.Stderr}");

            var runResult = await RunDockerCommandAsync($"run --rm --network=none {imageName}", appDir);
            Assert.True(runResult.ExitCode == 0, $"Docker run failed with exit code {runResult.ExitCode}.\nStdout: {runResult.Stdout}\nStderr: {runResult.Stderr}");
            Assert.Contains("runtime ok", runResult.Stdout);
        }
        finally
        {
            await RunDockerCommandAsync($"rmi {imageName}", appDir);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCommandAsync(string arguments, string workingDirectory)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        Assert.NotNull(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateJavaScriptAppWithDockerfile(string rootDirectory)
    {
        var appDir = Path.Combine(rootDirectory, "js");
        Directory.CreateDirectory(appDir);

        var dockerfile = """
            FROM oven/bun:1
            WORKDIR /app
            COPY . .
            ENTRYPOINT ["bun","src/index.ts"]
            """;

        File.WriteAllText(Path.Combine(appDir, "Dockerfile"), dockerfile);
        File.WriteAllText(Path.Combine(appDir, "package.json"), """
            {
              "scripts": {
                "migrate": "bun src/migrate.ts"
              }
            }
            """);

        return appDir;
    }
}
