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

public class AddJavaScriptAppTests(ITestOutputHelper outputHelper)
{
    private const string InternalNpmRegistry = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/";

    [Fact]
    public async Task VerifyDockerfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"]);

        await ManifestUtils.GetManifest(yarnApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));

        var dockerBuildAnnotation = yarnApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.False(dockerBuildAnnotation.HasEntrypoint);
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsStaticWebsiteWithoutSpaFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"])
            .PublishAsStaticWebsite();

        await ManifestUtils.GetManifest(yarnApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsNodeServer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"])
            .PublishAsNodeServer(".output/server/index.mjs", ".output");

        await ManifestUtils.GetManifest(yarnApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Fact]
    public async Task VerifyDockerfileWhenPublishedAsPackageScript()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var yarnApp = builder.AddJavaScriptApp("js", appDir)
            .WithYarn(installArgs: ["--immutable"])
            .WithBuildScript("do", ["--build"])
            .PublishAsPackageScript("start", "-- --port $PORT");

        await ManifestUtils.GetManifest(yarnApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
        await Verify(File.ReadAllText(dockerfilePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyPnpmDockerfile(bool hasLockFile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        // Create directory to ensure manifest generates correct relative build context path
        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        if (hasLockFile)
        {
            File.WriteAllText(Path.Combine(appDir, "pnpm-lock.yaml"), string.Empty);
        }

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm(installArgs: ["--prefer-frozen-lockfile"])
            .WithBuildScript("mybuild");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);

        await Verify(dockerfileContents);
    }

    [Fact]
    public async Task VerifyPnpmDockerfileUsesBootstrapRegistryOnlyForNpm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm(installArgs: ["--prefer-frozen-lockfile"])
            .WithBuildScript("mybuild");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfileLines = await File.ReadAllLinesAsync(Path.Combine(workspace.Path, "js.Dockerfile"));
        var registryAndInstallLines = dockerfileLines
            .Where(line =>
                line.StartsWith("ARG NPM_", StringComparison.Ordinal) ||
                line.StartsWith("RUN npm install", StringComparison.Ordinal) ||
                line.Contains(" pnpm install", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            [
                $"ARG NPM_REGISTRY={InternalNpmRegistry}",
                "RUN npm install --global --registry \"$NPM_REGISTRY\" pnpm@10.30.1",
                "RUN --mount=type=cache,target=/pnpm/store pnpm install --prefer-frozen-lockfile"
            ],
            registryAndInstallLines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyPnpmDockerfileWhenPublishedAsPackageScript(bool hasLockFile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        if (hasLockFile)
        {
            File.WriteAllText(Path.Combine(appDir, "pnpm-lock.yaml"), string.Empty);
        }

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm(installArgs: ["--prefer-frozen-lockfile"])
            .WithBuildScript("mybuild")
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);

        await Verify(dockerfileContents);
    }

    [Fact]
    public async Task PublishWithExistingDockerfileThrowsWhenRunScriptNameIsExplicit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
        var app = builder.AddJavaScriptApp("js", appDir, "migrate")
            .WithBun();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, workspace.Path));

        Assert.Contains("runScriptName", exception.Message);
        Assert.Contains("WithRunScript", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task PublishModelWithExistingDockerfileThrowsWhenRunScriptNameIsExplicit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
        builder.AddJavaScriptApp("js", appDir, "migrate")
            .WithBun();

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifestForModel(appModel, workspace.Path));

        Assert.Contains("runScriptName", exception.Message);
        Assert.Contains("WithRunScript", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task PublishWithExistingDockerfileThrowsWhenWithRunScriptOverridesDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun()
            .WithRunScript("migrate");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, workspace.Path));

        Assert.Contains("runScriptName", exception.Message);
        Assert.Contains("WithRunScript", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task PublishPipelineWithExistingDockerfileThrowsFromValidationStepWhenRunScriptNameIsExplicit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "validate-javascript-dockerfile-run-script-js").WithResourceCleanUp(true);
        builder.Services.AddSingleton<IPipelineActivityReporter, NullPublishingActivityReporter>();

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun();

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsExplicitEntrypointOverride()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
        var app = builder.AddJavaScriptApp("js", appDir, "migrate")
            .WithBun()
            .PublishAsDockerFile(container => container
                .WithEntrypoint("bun")
                .WithArgs("src/migrate.ts"));

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.Equal("bun", manifest["entrypoint"]?.ToString());
        Assert.Contains("src/migrate.ts", manifest.ToJsonString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileAllowsWithRunScriptMatchingDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun()
            // Re-stating the default script name explicitly should not be treated as a conflict
            // with the existing Dockerfile, because the effective run script still matches the default.
            .WithRunScript("dev");

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.Equal("container.v1", manifest["type"]?.ToString());
    }

    [Fact]
    public async Task PublishWithExistingDockerfileThrowsAndIncludesArgsWhenDefaultScriptHasArgs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = CreateJavaScriptAppWithDockerfile(workspace.Path);
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithBun()
            .WithRunScript("dev", ["--port", "8080"]);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, workspace.Path));

        Assert.Contains("run script 'dev'", exception.Message);
        Assert.Contains("with args [--port, 8080]", exception.Message);
        Assert.Contains("Dockerfile", exception.Message);
    }

    [Fact]
    public async Task VerifyPnpmDockerfileCopiesWorkspaceFileBeforeInstall()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Combine(appDir, "pnpm-workspace.yaml"), "allowBuilds: {}\n");

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm(installArgs: ["--prefer-frozen-lockfile"])
            .WithBuildScript("mybuild");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
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
    public async Task VerifyPnpmDockerfileUsesPackageManagerVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        await File.WriteAllTextAsync(
            Path.Combine(appDir, "package.json"),
            """
            {
              "packageManager": "pnpm@10.30.1+sha512.3590e550d5384caa39bd5c7c739f72270234b2f6059e13018f975c313b1eb9fefcc09714048765d4d9efe961382c312e624572c0420762bdc5d5940cdf9be73a"
            }
            """);

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm()
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Contains($"ARG NPM_REGISTRY={InternalNpmRegistry}", dockerfile);
        Assert.Contains("npm pack --json pnpm@10.30.1 --registry \"$NPM_REGISTRY\"", dockerfile);
        Assert.Contains("createHash(algorithm)", dockerfile);
        Assert.Contains("\"sha512\" \"3590e550d5384caa39bd5c7c739f72270234b2f6059e13018f975c313b1eb9fefcc09714048765d4d9efe961382c312e624572c0420762bdc5d5940cdf9be73a\" \"$archive\"", dockerfile);
        Assert.Contains("npm install --global --registry \"$NPM_REGISTRY\" \"./$archive\"", dockerfile);
    }

    [Theory]
    [InlineData("sha224")]
    [InlineData("sha256")]
    [InlineData("sha384")]
    [InlineData("sha512")]
    public async Task VerifyPnpmDockerfileUsesNodeForPackageManagerIntegrity(string algorithm)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        await File.WriteAllTextAsync(
            Path.Combine(appDir, "package.json"),
            $$"""
            {
              "packageManager": "pnpm@10.30.1+{{algorithm}}.abcdef"
            }
            """);

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm()
            .WithBuildScript("build")
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "js.Dockerfile"));
        var integrityLines = dockerfile
            .Split('\n')
            .Where(line => line.Contains("createHash(algorithm)", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, integrityLines.Length);
        Assert.All(integrityLines, line => Assert.Contains($"\"{algorithm}\" \"abcdef\" \"$archive\"", line));
    }

    [Fact]
    public async Task VerifyPnpmDockerfileNormalizesPackageManagerIntegrityHash()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        await File.WriteAllTextAsync(
            Path.Combine(appDir, "package.json"),
            """
            {
              "packageManager": "pnpm@10.30.1+sha512.ABCDEF"
            }
            """);

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm()
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Contains("\"sha512\" \"abcdef\" \"$archive\"", dockerfile);
    }

    [Theory]
    [InlineData("10.30.1")]
    [InlineData("v10.30.1")]
    [InlineData("10.30.1-beta.1")]
    public async Task VerifyPnpmDockerfileUsesValidPackageManagerVersion(string version)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        await File.WriteAllTextAsync(
            Path.Combine(appDir, "package.json"),
            $$"""
            {
              "packageManager": "pnpm@{{version}}"
            }
            """);

        var pnpmApp = builder.AddJavaScriptApp("js", appDir)
            .WithPnpm()
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Contains($"npm install --global --registry \"$NPM_REGISTRY\" pnpm@{version}", dockerfile);
    }

    [Theory]
    [InlineData("pnpm@")]
    [InlineData("pnpm@10")]
    [InlineData("pnpm@10.30")]
    [InlineData("pnpm@01.30.1")]
    [InlineData("pnpm@10.030.1")]
    [InlineData("pnpm@10.30.01")]
    [InlineData("pnpm@10.30.1/invalid")]
    [InlineData("pnpm@10.30.1-alpha..1")]
    [InlineData("pnpm@10.30.1+")]
    [InlineData("pnpm@10.30.1+sha1.abcdef")]
    [InlineData("pnpm@10.30.1+sha512")]
    [InlineData("pnpm@10.30.1+sha512.")]
    [InlineData("pnpm@10.30.1+sha512.not-hex")]
    public void VerifyPnpmRejectsInvalidPackageManagerSpecification(string packageManager)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        var packageJsonPath = Path.Combine(appDir, "package.json");
        File.WriteAllText(
            packageJsonPath,
            $$"""
            {
              "packageManager": "{{packageManager}}"
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddJavaScriptApp("js", appDir).WithPnpm());

        Assert.Equal(
            $"The packageManager value '{packageManager}' in '{packageJsonPath}' is invalid. Expected 'pnpm@<version>' or 'pnpm@<version>+<sha224|sha256|sha384|sha512>.<hex hash>'.",
            exception.Message);
    }

    [Fact]
	[RequiresFeature(TestFeature.Docker | TestFeature.ContainerImageBuild)]
    [OuterloopTest("Builds a Docker image to verify the generated pnpm Dockerfile works")]
    public async Task VerifyPnpmDockerfileBuildSucceeds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        // Create app directory
        var appDir = Path.Combine(workspace.Path, "pnpm-app");
        Directory.CreateDirectory(appDir);

        // Create a minimal package.json with no dependencies
        var packageJson = """
            {
              "name": "pnpm-test-app",
              "version": "1.0.0",
                            "packageManager": "pnpm@10.30.1+sha512.3590e550d5384caa39bd5c7c739f72270234b2f6059e13018f975c313b1eb9fefcc09714048765d4d9efe961382c312e624572c0420762bdc5d5940cdf9be73a",
              "scripts": {
                "build": "echo 'build completed'"
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(appDir, "package.json"), packageJson);

        var pnpmApp = builder.AddJavaScriptApp("pnpm-app", appDir)
            .WithPnpm()
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "pnpm-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");

        // Read the generated Dockerfile and verify it installs pnpm through npm.
        var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath);
        Assert.Contains($"ARG NPM_REGISTRY={InternalNpmRegistry}", dockerfileContent);
        Assert.Contains("npm pack --json pnpm@10.30.1 --registry \"$NPM_REGISTRY\"", dockerfileContent);
        Assert.Contains("createHash(algorithm)", dockerfileContent);
        Assert.Contains("npm install --global --registry \"$NPM_REGISTRY\" \"./$archive\"", dockerfileContent);

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
    [RequiresFeature(TestFeature.Docker | TestFeature.ContainerImageBuild)]
    [OuterloopTest("Builds and runs a Docker image to verify the generated pnpm PublishAsPackageScript Dockerfile works")]
    public async Task VerifyPnpmDockerfileWhenPublishedAsPackageScriptRunsWithoutNetwork()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "pnpm-app");
        Directory.CreateDirectory(appDir);

        var packageJson = """
            {
              "name": "pnpm-runtime-test-app",
              "version": "1.0.0",
              "packageManager": "pnpm@10.30.1+sha384.06222487b91b2da4282562ca67a7e77b00ebce036cc416deb4f136696811d9fd9b804bb8c967547525717d8f7b069229",
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

        await ManifestUtils.GetManifest(pnpmApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "pnpm-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");

        var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath);
        Assert.Contains($"ARG NPM_REGISTRY={InternalNpmRegistry}", dockerfileContent);
        Assert.Contains("npm pack --json pnpm@10.30.1 --registry \"$NPM_REGISTRY\"", dockerfileContent);
        Assert.Contains("createHash(algorithm)", dockerfileContent);
        Assert.Contains("npm install --global --registry \"$NPM_REGISTRY\" \"./$archive\"", dockerfileContent);

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