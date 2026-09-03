// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only
#pragma warning disable ASPIREDENO001 // Type is for evaluation purposes only
#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddDenoAppTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var workingDirectory = AppContext.BaseDirectory;
        var denoApp = builder.AddDenoApp("denoapp", workingDirectory, "main.ts")
            .WithHttpEndpoint(port: 5033, env: "PORT");
        var manifest = await ManifestUtils.GetManifest(denoApp.Resource);

        await Verify(manifest.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestDoesNotActivateDenoWithoutATargetOtlpEndpoint(bool configurePublish)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        var denoApp = builder.AddDenoApp("denoapp", AppContext.BaseDirectory, "main.ts");

        if (configurePublish)
        {
            denoApp.WithDeno()
                .PublishAsPackageScript();
        }

        var exporter = Assert.Single(denoApp.Resource.Annotations.OfType<OtlpExporterAnnotation>());
        Assert.Equal(OtlpProtocol.HttpProtobuf, exporter.RequiredProtocol);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(exporter);

        var manifest = await ManifestUtils.GetManifest(denoApp.Resource);

        Assert.Null(manifest["env"]?["OTEL_DENO"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyDockerfile(bool includePackageJson)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        if (includePackageJson)
        {
            File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");
        }

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);
        await Verify(dockerfileContents);

        var dockerBuildAnnotation = denoApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        Assert.Empty(denoApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>());
    }

    [Fact]
    public async Task VerifyDockerfileWithCustomBaseImage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        var customBuildImage = "denoland/deno:2.1-alpine";
        var customRuntimeImage = "denoland/deno:2.1-distroless";
        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDockerfileBaseImage(customBuildImage, customRuntimeImage);

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        await Verify(dockerfileContents);
        Assert.Equal("COPY --from=build /app /app", GetDockerfileLine(dockerfileContents, "COPY --from=build /app"));
        Assert.Equal("COPY --from=build /deno-dir /deno-dir", GetDockerfileLine(dockerfileContents, "COPY --from=build /deno-dir"));
    }

    [Fact]
    public async Task VerifyDockerfileEmitsPerDockerfileDockerignore()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        // The default .dockerignore should be emitted alongside the published Dockerfile using
        // BuildKit's per-Dockerfile convention (<dockerfile-name>.dockerignore), not into the
        // user's source tree.
        var perDockerfileIgnorePath = Path.Combine(workspace.Path, "js.Dockerfile.dockerignore");
        Assert.True(File.Exists(perDockerfileIgnorePath), $"Expected per-Dockerfile dockerignore at {perDockerfileIgnorePath}");
        var ignoreContents = File.ReadAllText(perDockerfileIgnorePath);
        await Verify(ignoreContents);

        // The user's source tree must not be polluted with a generated .dockerignore.
        Assert.False(File.Exists(Path.Combine(appDir, ".dockerignore")), "Aspire should not write a .dockerignore into the user's source tree.");

        // The annotation should carry the default content so it can be inspected/overridden by users.
        // Deno can materialize node_modules for npm compatibility, so keep local dependency folders
        // out of the generated build context like the Bun/Node variants do.
        var dockerBuildAnnotation = denoApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.Equal(ignoreContents, dockerBuildAnnotation.BuildContextIgnoreContent);
    }

    [Fact]
    public async Task VerifyDockerfile_PreCachesDependenciesAndShipsDenoDir()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));

        // The build stage must pre-cache direct run/serve entrypoints into DENO_DIR. Without a deno.lock,
        // plain `deno cache` is used.
        Assert.Equal("RUN deno cache main.ts", GetDockerfileLine(dockerfileContents, "RUN deno cache"));
        // DENO_DIR must be pinned deterministically in both stages...
        Assert.Collection(
            dockerfileContents.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("ENV DENO_DIR", StringComparison.Ordinal)),
            line => Assert.Equal("ENV DENO_DIR=/deno-dir", line),
            line => Assert.Equal("ENV DENO_DIR=/deno-dir", line));
        // ...and the populated cache copied into the runtime stage.
        Assert.Equal(
            "COPY --from=build --chown=deno:deno /deno-dir /deno-dir",
            GetDockerfileLine(dockerfileContents, "COPY --from=build --chown=deno:deno /deno-dir"));
        // NODE_ENV must be set for Deno's npm-compatibility mode, mirroring the Bun publish block.
        Assert.Equal("ENV NODE_ENV=production", GetDockerfileLine(dockerfileContents, "ENV NODE_ENV"));
        // Runtime uses only the build-stage cache instead of re-fetching dependencies from the network.
        Assert.Equal(
            """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--cached-only","main.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_UsesFrozenCacheWhenDenoLockExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        // A committed lockfile means the build should fail fast on drift rather than silently re-resolve.
        File.WriteAllText(Path.Combine(appDir, "deno.lock"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));

        Assert.Equal("RUN deno cache --frozen main.ts", GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_CacheUsesConfiguredResolutionAndLockFlags()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "custom.lock"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoConfig("deno.json")
            .WithDenoImportMap("import_map.json")
            .WithDenoLock("custom.lock")
            .WithDenoNodeModulesDir(DenoNodeModulesDirMode.Auto);

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            "RUN deno cache --config deno.json --import-map import_map.json --lock custom.lock --node-modules-dir=auto --frozen main.ts",
            GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_TaskEntrypointSkipsOpaqueTaskCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "custom.lock"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoTask("start")
            .WithDenoConfig("deno.json")
            .WithDenoImportMap("import_map.json")
            .WithDenoLock("custom.lock")
            .WithDenoNodeModulesDir(DenoNodeModulesDirMode.Auto)
            .WithDenoUnstable("sloppy-imports");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile")));
    }

    [Fact]
    public async Task VerifyDockerfile_WithRunScriptUsesDenoTaskEntrypoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"start":"deno run -A main.ts"}}""");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithRunScript("start", ["--my-arg"]);

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("RUN mkdir -p /deno-dir", GetDockerfileLine(dockerfileContents, "RUN "));
        Assert.Equal("""ENTRYPOINT ["deno","task","start","--my-arg"]""", GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_WithRunScriptAndDenoFlagsUsesDenoTaskEntrypoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"start":"deno run -A main.ts"}}""");
        File.WriteAllText(Path.Combine(appDir, "deno.lock"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithRunScript("start", ["--my-arg"])
            .WithDenoConfig("deno.json")
            .WithDenoImportMap("import_map.json")
            .WithDenoLock("deno.lock")
            .WithDenoNodeModulesDir(DenoNodeModulesDirMode.Auto);

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("RUN mkdir -p /deno-dir", GetDockerfileLine(dockerfileContents, "RUN "));
        Assert.Equal(
            """ENTRYPOINT ["deno","task","--config","deno.json","--lock","deno.lock","--node-modules-dir=auto","start","--my-arg"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_PublishAsPackageScriptWithDenoDoesNotRequireProductionInstallArgs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"build":"deno run -A build.ts","start":"deno run -A main.ts"}}""");

        var app = builder.AddJavaScriptApp("js", appDir)
            .WithDeno()
            .WithBuildScript("build")
            .PublishAsPackageScript("start", "-- --port $PORT");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        await Verify(dockerfileContents);
        Assert.Equal("COPY --from=build --chown=deno:deno /app /app", GetDockerfileLine(dockerfileContents, "COPY --from=build --chown=deno:deno /app"));
        // Deno's dependency store must travel to the runtime stage or the container re-downloads on first run.
        Assert.Equal("COPY --from=build --chown=deno:deno /deno-dir /deno-dir", GetDockerfileLine(dockerfileContents, "COPY --from=build --chown=deno:deno /deno-dir"));
        Assert.Equal("USER deno", GetDockerfileLine(dockerfileContents, "USER"));
        Assert.Equal("""ENTRYPOINT ["sh","-c","exec deno task start -- --port $PORT"]""", GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_AddDenoAppPackageScriptRunsInstallAndBuildAndOverridesLocalMode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(
            Path.Combine(appDir, "deno.json"),
            """{"tasks":{"build prod":"deno run -A build.ts","start":"deno run -A main.ts"}}""");

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoTask("local")
            .WithBuildScript("build prod")
            .PublishAsPackageScript("start", "--my-arg");

        var exporter = Assert.Single(app.Resource.Annotations.OfType<OtlpExporterAnnotation>());
        Assert.Equal(OtlpProtocol.HttpProtobuf, exporter.RequiredProtocol);

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        await Verify(dockerfileContents);
        Assert.Equal("RUN deno install", GetDockerfileLine(dockerfileContents, "RUN deno install"));
        Assert.Equal("RUN deno task 'build prod'", GetDockerfileLine(dockerfileContents, "RUN deno task"));
        Assert.Equal(
            """ENTRYPOINT ["deno","task","start","--my-arg"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_AddDenoAppPublishAsStaticWebsiteThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .PublishAsStaticWebsite();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.GetManifest(app.Resource, workspace.Path));

        Assert.Equal(
            "Generated Deno Dockerfiles do not support PublishAsStaticWebsite. Use AddJavaScriptApp(...).WithDeno() or provide a custom Dockerfile.",
            exception.Message);
    }

    [Fact]
    public async Task VerifyDockerfile_AddDenoAppPublishAsNodeServerThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .PublishAsNodeServer("server.js");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.GetManifest(app.Resource, workspace.Path));

        Assert.Equal(
            "Generated Deno Dockerfiles do not support PublishAsNodeServer. Use AddJavaScriptApp(...).WithDeno() or provide a custom Dockerfile.",
            exception.Message);
    }

    [Fact]
    public async Task VerifyDockerfile_DenoPackageScriptCopiesReferencedMetadataBeforeInstall()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"importMap":"import_map.json","tasks":{"start":"deno run main.ts"}}""");
        File.WriteAllText(Path.Combine(appDir, "import_map.json"), """{"imports":{}}""");

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileLines = File.ReadAllLines(Path.Combine(workspace.Path, "js.Dockerfile"));
        var copySourceIndex = Array.IndexOf(dockerfileLines, "COPY . .");
        var installIndex = Array.IndexOf(dockerfileLines, "RUN deno install");

        Assert.True(copySourceIndex >= 0, string.Join(Environment.NewLine, dockerfileLines));
        Assert.True(installIndex > copySourceIndex, string.Join(Environment.NewLine, dockerfileLines));
    }

    [Fact]
    public async Task VerifyDockerfile_JavaScriptAppWithDenoCopiesReferencedMetadataBeforeInstall()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"importMap":"import_map.json","tasks":{"start":"deno run main.ts"}}""");
        File.WriteAllText(Path.Combine(appDir, "import_map.json"), """{"imports":{}}""");

        var app = builder.AddJavaScriptApp("js", appDir)
            .WithDeno()
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileLines = File.ReadAllLines(Path.Combine(workspace.Path, "js.Dockerfile"));
        var copySourceIndex = Array.IndexOf(dockerfileLines, "COPY . .");
        var installIndex = Array.IndexOf(dockerfileLines, "RUN deno install");

        Assert.True(copySourceIndex >= 0, string.Join(Environment.NewLine, dockerfileLines));
        Assert.True(installIndex > copySourceIndex, string.Join(Environment.NewLine, dockerfileLines));
    }

    [Fact]
    public async Task VerifyDockerfile_DefaultPublishPermissionsAreLeastPrivilege()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var app = builder.AddDenoApp("js", appDir, "main.ts");
        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--cached-only","main.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_DenyOnlyPermissionsNarrowPublishDefaults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoDeny(DenoPermissionKind.Read, @"secrets\private");
        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--deny-read=secrets/private","--cached-only","main.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_ExplicitPublishPermissionsArePreservedAndNormalized()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoAllow(DenoPermissionKind.Read, @"data\read")
            .WithDenoDeny(DenoPermissionKind.Write, @"data\write")
            .WithDenoAllow(DenoPermissionKind.Ffi, @"native\lib");
        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","run","--allow-read=data/read","--deny-write=data/write","--allow-ffi=native/lib","--cached-only","main.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Theory]
    [InlineData("--env-file")]
    [InlineData("--env-file=")]
    [InlineData("--env-file=.env.production")]
    [InlineData("--env-file=../.env")]
    [InlineData("--env-file=**")]
    [InlineData("--env-file=.env\n!.env")]
    public async Task VerifyDockerfile_EnvironmentFileIsRejected(string runtimeArg)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoRuntimeArgs(runtimeArg);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.GetManifest(app.Resource, workspace.Path));
        Assert.Equal(
            "Generated Deno Dockerfiles do not support '--env-file' because dotenv files can contain secrets that would be copied into the container image. Use Aspire environment variables or secret parameters, or provide a custom Dockerfile that handles the file securely.",
            exception.Message);
    }

    [Fact]
    public async Task VerifyDockerfile_EnvironmentFileIsAllowedWithCustomDockerfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "Dockerfile"), "FROM denoland/deno:2.9.0");

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoRuntimeArgs("--env-file=.env.production");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        Assert.False(File.Exists(Path.Combine(workspace.Path, "js.Dockerfile")));
    }

    [Fact]
    public async Task VerifyDockerfile_CachePropagatesResolutionGoverningRuntimeArgs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "main.ts"), "console.log(1);");

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            // --cert is required to fetch a private HTTPS module and --no-remote forbids network resolution.
            // Both are honoured by `deno cache`, so dropping them either breaks `docker build` or silently
            // ignores the caller's policy at build time.
            .WithDenoRuntimeArgs("--allow-net", "--cert", "ca.pem", "--no-remote", "--vendor=true");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        // --allow-net is a run-only permission that `deno cache` rejects outright, so it must not be forwarded.
        Assert.Equal(
            "RUN deno cache --cert ca.pem --no-remote --vendor=true main.ts",
            GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyDockerfile_CertificatePathsAreNormalizedForLinuxContainers(bool inlineValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "main.ts"), "console.log(1);");

        var app = builder.AddDenoApp("js", appDir, "main.ts");
        if (inlineValue)
        {
            app.WithDenoRuntimeArgs(@"--cert=certs\ca.pem");
        }
        else
        {
            app.WithDenoRuntimeArgs("--cert", @"certs\ca.pem");
        }

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        if (inlineValue)
        {
            Assert.Equal(
                "RUN deno cache --cert=certs/ca.pem main.ts",
                GetDockerfileLine(dockerfileContents, "RUN deno cache"));
            Assert.Equal(
                """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--cached-only","--cert=certs/ca.pem","main.ts"]""",
                GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
        }
        else
        {
            Assert.Equal(
                "RUN deno cache --cert certs/ca.pem main.ts",
                GetDockerfileLine(dockerfileContents, "RUN deno cache"));
            Assert.Equal(
                """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--cached-only","--cert","certs/ca.pem","main.ts"]""",
                GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
        }
    }

    [Fact]
    public async Task VerifyDockerfile_CacheOmitsRuntimeArgsDenoCacheRejects()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "main.ts"), "console.log(1);");

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoRuntimeArgs("-A", "--allow-read", "/data", "--cached-only");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        // Forwarding these would fail the build: `deno cache -A` reports "error: unexpected argument".
        // "/data" is a bare value belonging to --allow-read and must not leak through as a module to cache.
        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("RUN deno cache main.ts", GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_CacheOmitsTrailingValueFlagThatWouldSwallowTheEntrypoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "main.ts"), "console.log(1);");

        var app = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoRuntimeArgs("--lock");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        // `deno cache --lock main.ts` consumes main.ts as the lock file and then fails with
        // "the following required arguments were not provided: <file>...", so a bare trailing --lock is dropped.
        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("RUN deno cache main.ts", GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_BuildScriptWithSpacesIsQuotedForDeno()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"build prod":"deno run -A build.ts","start":"deno run -A main.ts"}}""");

        var app = builder.AddJavaScriptApp("js", appDir)
            .WithDeno()
            .WithBuildScript("build prod")
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        // Without quoting the shell word-splits this into `deno task build` plus a stray `prod` argument.
        Assert.Equal("RUN deno task 'build prod'", GetDockerfileLine(dockerfileContents, "RUN deno task"));
    }

    [Fact]
    public async Task VerifyDockerfile_BuildScriptWithSpacesIsQuotedForNpm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), """{"scripts":{"build prod":"echo build","start":"node main.js"}}""");

        var app = builder.AddJavaScriptApp("js", appDir)
            .WithNpm()
            .WithBuildScript("build prod")
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        // The build-script RUN line is shared by every package manager, so the quoting fix must hold off the Deno path too.
        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("RUN npm run 'build prod'", GetDockerfileLine(dockerfileContents, "RUN npm run"));
    }

    [Fact]
    public async Task VerifyDockerfile_PublishAsPackageScriptWithCustomDenoRuntimeImagePreservesImageUser()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"start":"deno run -A main.ts"}}""");

        var app = builder.AddJavaScriptApp("js", appDir)
            .WithDockerfileBaseImage("denoland/deno:2.9.0", "denoland/deno:2.1-distroless")
            .WithDeno()
            .PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("COPY --from=build /app /app", GetDockerfileLine(dockerfileContents, "COPY --from=build /app"));
        Assert.Equal("COPY --from=build /deno-dir /deno-dir", GetDockerfileLine(dockerfileContents, "COPY --from=build /deno-dir"));
        Assert.DoesNotContain(dockerfileContents.Split(Environment.NewLine), line => line.StartsWith("USER ", StringComparison.Ordinal));
        // Distroless images have no /bin/sh, so the entrypoint must be exec form.
        Assert.Equal(
            """ENTRYPOINT ["deno","task","start"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyDockerfile_RuntimeOnlyBaseImageOverrideStillBuildsWithDeno(bool denoBeforeOverride)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"start":"deno run -A main.ts"}}""");

        // WithDockerfileBaseImage replaces the annotation, so a runtime-only override leaves BuildImage null in
        // either ordering. The build stage must still resolve to a Deno image or `deno install` would not exist.
        var app = builder.AddJavaScriptApp("js", appDir);
        if (denoBeforeOverride)
        {
            app.WithDeno(install: true).WithDockerfileBaseImage(runtimeImage: "denoland/deno:2.1-distroless");
        }
        else
        {
            app.WithDockerfileBaseImage(runtimeImage: "denoland/deno:2.1-distroless").WithDeno(install: true);
        }

        app.PublishAsPackageScript("start");

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("FROM denoland/deno:2.9.0 AS build", GetDockerfileLine(dockerfileContents, "FROM denoland/deno:2.9.0"));
        Assert.Equal("FROM denoland/deno:2.1-distroless AS runtime", GetDockerfileLine(dockerfileContents, "FROM denoland/deno:2.1-distroless"));
    }

    [Fact]
    public async Task VerifyDockerfile_PublishAsPackageScriptWithPlainArgumentsUsesExecForm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"start":"deno run -A main.ts"}}""");

        var app = builder.AddJavaScriptApp("js", appDir)
            .WithDeno()
            .PublishAsPackageScript("start", """-- --port 8080 --name "my app" """);

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","task","start","--","--port","8080","--name","my app"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    // Every character the classifier treats as inert is baked into an exec-form ENTRYPOINT verbatim, so a
    // character that actually means something to `sh` would silently change the command. These two theories
    // pin the boundary; the shell-routed set was verified against a real /bin/sh.
    [Theory]
    [InlineData("--port 8080", new[] { "--port", "8080" })]
    [InlineData("--name 'my app'", new[] { "--name", "my app" })]
    [InlineData("""--path "/a b" """, new[] { "--path", "/a b" })]
    [InlineData("--flag=a,b", new[] { "--flag=a,b" })]
    [InlineData("--email a@b.co", new[] { "--email", "a@b.co" })]
    [InlineData("--pct 50%", new[] { "--pct", "50%" })]
    [InlineData("--not !x", new[] { "--not", "!x" })]
    [InlineData("--caret a^b", new[] { "--caret", "a^b" })]
    [InlineData("--plus a+b", new[] { "--plus", "a+b" })]
    public void BuildDenoPackageScriptEntrypoint_KeepsShellInertArgumentsInExecForm(string runScriptArguments, string[] expectedArgumentTokens)
    {
        var entrypoint = JavaScriptHostingExtensions.BuildDenoPackageScriptEntrypoint("deno", "task", "start", runScriptArguments);

        Assert.Equal(["deno", "task", "start", .. expectedArgumentTokens], entrypoint);
    }

    [Theory]
    // Expansion, substitution, operators, and redirection.
    [InlineData("--port $PORT")]
    [InlineData("--rev `git rev-parse HEAD`")]
    [InlineData("--a x|y")]
    [InlineData("--a x&y")]
    [InlineData("--a x;y")]
    [InlineData("--out >log")]
    [InlineData("--in <log")]
    [InlineData("--glob *.ts")]
    [InlineData("--glob ?.ts")]
    [InlineData("--home ~/app")]
    [InlineData("--sub (x)")]
    // Regressions the previous metacharacter denylist missed: bracket expressions and brace expansion glob,
    // '#' starts a comment that discards the rest of the line, and a newline separates commands.
    [InlineData("--glob [ab].ts")]
    [InlineData("--brace {a,b}.ts")]
    [InlineData("--tag #1")]
    [InlineData("--a x\ny")]
    public void BuildDenoPackageScriptEntrypoint_RoutesShellDependentArgumentsToShellForm(string runScriptArguments)
    {
        var entrypoint = JavaScriptHostingExtensions.BuildDenoPackageScriptEntrypoint("deno", "task", "start", runScriptArguments);

        Assert.Equal(["sh", "-c", $"exec deno task start {runScriptArguments}"], entrypoint);
    }

    [Theory]
    [InlineData("--name 'unterminated", "single")]
    [InlineData("--name \"unterminated", "double")]
    [InlineData("'", "single")]
    [InlineData("--flag=value --other 'still open", "single")]
    public void BuildDenoPackageScriptEntrypoint_ThrowsForUnterminatedQuote(string runScriptArguments, string quoteKind)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => JavaScriptHostingExtensions.BuildDenoPackageScriptEntrypoint("deno", "task", "start", runScriptArguments));

        Assert.Equal(
            $"The Deno run script arguments '{runScriptArguments}' contain an unterminated {quoteKind} quote. Close the quote so the arguments can be parsed the way a shell would parse them.",
            exception.Message);
    }

    [Theory]
    [InlineData("--name 'closed'", new[] { "deno", "task", "start", "--name", "closed" })]
    [InlineData("--name \"closed\"", new[] { "deno", "task", "start", "--name", "closed" })]
    [InlineData("''", new[] { "deno", "task", "start", "" })]
    public void BuildDenoPackageScriptEntrypoint_AcceptsBalancedQuotes(string runScriptArguments, string[] expected)
    {
        var entrypoint = JavaScriptHostingExtensions.BuildDenoPackageScriptEntrypoint("deno", "task", "start", runScriptArguments);

        Assert.Equal(expected, entrypoint);
    }

    [Fact]
    public void BuildDenoPackageScriptEntrypoint_QuotesCommandComponentsInShellForm()
    {
        // Only runScriptArguments is meant to be shell-evaluated. A task name with a space would otherwise
        // word-split into `deno task build prod`, running the "build" task with a stray "prod" argument.
        var entrypoint = JavaScriptHostingExtensions.BuildDenoPackageScriptEntrypoint("deno", "task", "build prod", "-- --port $PORT");

        Assert.Equal(["sh", "-c", "exec deno task 'build prod' -- --port $PORT"], entrypoint);
    }

    [Fact]
    public async Task VerifyDockerfile_PublishAsPackageScriptPreservesPosixDoubleQuoteEscapes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.json"), """{"tasks":{"start":"deno run -A main.ts"}}""");

        // Inside double quotes POSIX only treats a backslash as an escape before $ ` " \ and newline, so
        // "\d+" must survive intact while "C:\\tmp" collapses to a single backslash.
        var app = builder.AddJavaScriptApp("js", appDir)
            .WithDeno()
            .PublishAsPackageScript("start", """-- --pattern "\d+" --path "C:\\tmp" --quote "say \"hi\"" """);

        await ManifestUtils.GetManifest(app.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","task","start","--","--pattern","\\d+","--path","C:\\tmp","--quote","say \"hi\""]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_CacheUsesNoLockWhenConfigured()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.lock"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoNoLock();

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("RUN deno cache --no-lock main.ts", GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_CacheDoesNotCombineRawNoLockWithFrozen()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "deno.lock"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoRuntimeArgs("--no-lock");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("RUN deno cache --no-lock main.ts", GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_ManualNodeModulesDirThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithDenoNodeModulesDir(DenoNodeModulesDirMode.Manual);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestUtils.GetManifest(denoApp.Resource, workspace.Path));

        Assert.Equal("The 'manual' node_modules mode is not supported by generated Deno Dockerfiles because node_modules is excluded from the build context. Use the 'auto' mode or provide a custom Dockerfile.", exception.Message);
    }

    [Fact]
    public async Task VerifyDockerfile_AlternatePackageManagerThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithRunScript("start")
            .WithNpm();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestUtils.GetManifest(denoApp.Resource, workspace.Path));

        Assert.Equal("Generated Deno Dockerfiles do not support alternate package manager 'npm'. Use WithDeno() or provide a custom Dockerfile.", exception.Message);
    }

    [Fact]
    public async Task VerifyDockerfile_CacheUsesUnstableFlags()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoConfig("deno.json")
            .WithDenoUnstable("sloppy-imports");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            "RUN deno cache --config deno.json --unstable-sloppy-imports main.ts",
            GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_CacheQuotesShellArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "custom lock's.lock"), "{}");

        var denoApp = builder.AddDenoApp("js", appDir, "main file's.ts")
            .WithDenoConfig("deno config.json")
            .WithDenoImportMap("import map.json")
            .WithDenoLock("custom lock's.lock");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """RUN deno cache --config 'deno config.json' --import-map 'import map.json' --lock 'custom lock'"'"'s.lock' --frozen 'main file'"'"'s.ts'""",
            GetDockerfileLine(dockerfileContents, "RUN deno cache"));
    }

    [Fact]
    public async Task VerifyDockerfile_EntrypointDoesNotIncludeDevelopmentFlags()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoWatch()
            .WithDenoInspect(DenoInspectMode.InspectWait, "127.0.0.1:9229");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal("""ENTRYPOINT ["deno","run","--allow-net","--allow-env","--cached-only","main.ts"]""", GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    // --cached-only is an Aspire default, not a hard requirement. Deno accepts it alongside --reload without
    // error but --cached-only silently wins, so emitting both would turn the caller's explicit cache policy
    // into a no-op. Drop the default instead of overriding the caller.
    [Theory]
    [InlineData("--reload")]
    [InlineData("--reload=npm:chalk")]
    [InlineData("-r")]
    [InlineData("--cached-only")]
    public async Task VerifyDockerfile_EntrypointDropsManagedCachedOnlyWhenRuntimeArgsSelectACachePolicy(string cacheFlag)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoRuntimeArgs(cacheFlag);

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal($$"""ENTRYPOINT ["deno","run","--allow-net","--allow-env","{{cacheFlag}}","main.ts"]""", GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_ServeEntrypointBindsEndpointTargetPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "server.ts")
            .WithDenoServe()
            .WithHttpEndpoint(targetPort: 5173);

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","serve","--allow-net","--allow-env","--cached-only","--host","0.0.0.0","--port","5173","server.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_ServeEntrypointPreservesPreconfiguredEndpointTargetPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "server.ts")
            .WithHttpEndpoint(targetPort: 5173)
            .WithDenoServe();

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","serve","--allow-net","--allow-env","--cached-only","--host","0.0.0.0","--port","5173","server.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_ServeDefaultEndpointPinsDenoDefaultPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "server.ts")
            .WithDenoServe();

        var httpEndpoint = denoApp.Resource.GetEndpoint("http");
        Assert.Equal(8000, httpEndpoint.EndpointAnnotation.TargetPort);

        var manifest = await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);
        Assert.Equal(8000, manifest["bindings"]!["http"]!["targetPort"]!.GetValue<int>());

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","serve","--allow-net","--allow-env","--cached-only","--host","0.0.0.0","--port","8000","server.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public void WithDenoServe_DoesNotPinRunModeDefaultTargetPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "server.ts")
            .WithDenoServe();

        Assert.Null(denoApp.Resource.GetEndpoint("http").EndpointAnnotation.TargetPort);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("run")]
    public async Task WithDenoServe_WithdrawsHttpEndpointAndEnvironmentWhenAnotherModeWins(string finalMode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "server.ts");
        var initialEnvironmentCallbackCount = denoApp.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();

        denoApp.WithDenoServe();
        _ = finalMode == "task" ? denoApp.WithDenoTask("worker") : denoApp.WithDenoRun();

        // A non-serve mode does not bind HTTP, so leaving the endpoint behind would advertise a binding through
        // service discovery for a process that never listens, and would still inject PORT.
        Assert.Empty(denoApp.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(initialEnvironmentCallbackCount, denoApp.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());

        var environment = await EnvironmentVariableEvaluator
            .GetEnvironmentVariablesAsync(denoApp.Resource, DistributedApplicationOperation.Run)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(environment.GetValueOrDefault("PORT"));
    }

    [Fact]
    public async Task WithDenoServe_RestoresHttpEndpointAndEnvironmentWhenServeWinsAgain()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "server.ts")
            .WithDenoServe()
            .WithDenoTask("worker")
            .WithDenoServe();

        var endpoint = Assert.Single(denoApp.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);

        var environment = await EnvironmentVariableEvaluator
            .GetEnvironmentVariablesAsync(denoApp.Resource, DistributedApplicationOperation.Publish)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            [
                new("NODE_ENV", "production"),
                new("PORT", "{js.bindings.http.targetPort}"),
            ],
            environment.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void WithDenoTask_KeepsCallerConfiguredHttpEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        // The endpoint here is the caller's, not one WithDenoServe created, so switching modes must not remove it.
        var denoApp = builder.AddDenoApp("js", appDir, "server.ts")
            .WithHttpEndpoint(name: "api")
            .WithDenoTask("worker");

        var endpoint = Assert.Single(denoApp.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("api", endpoint.Name);
    }

    [Fact]
    public async Task WithDenoTask_RestoresCallerConfiguredSameNameHttpEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "server.ts")
            .WithHttpEndpoint(targetPort: 4321, env: "APP_PORT");
        var callerEndpoint = denoApp.Resource.GetEndpoint("http").EndpointAnnotation;
        var initialEnvironmentCallbackCount = denoApp.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();

        denoApp.WithDenoServe().WithDenoTask("worker");

        var endpoint = Assert.Single(denoApp.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Same(callerEndpoint, endpoint);
        Assert.Equal(4321, endpoint.TargetPort);
        Assert.Equal(initialEnvironmentCallbackCount, denoApp.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());

        var environment = await EnvironmentVariableEvaluator
            .GetEnvironmentVariablesAsync(denoApp.Resource, DistributedApplicationOperation.Run)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("4321", environment["APP_PORT"]);
        Assert.Null(environment.GetValueOrDefault("PORT"));
    }

    [Fact]
    public async Task VerifyDockerfile_ServeResourcesReuseProcessLocalDefaultTargetPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir1 = Path.Combine(workspace.Path, "js1");
        var appDir2 = Path.Combine(workspace.Path, "js2");
        Directory.CreateDirectory(appDir1);
        Directory.CreateDirectory(appDir2);

        _ = builder.AddDenoApp("js1", appDir1, "server.ts")
            .WithDenoServe();
        var denoApp2 = builder.AddDenoApp("js2", appDir2, "server.ts")
            .WithDenoServe();

        var httpEndpoint = denoApp2.Resource.GetEndpoint("http");
        Assert.Equal(8000, httpEndpoint.EndpointAnnotation.TargetPort);

        await ManifestUtils.GetManifest(denoApp2.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js2.Dockerfile"));
        Assert.Equal(
            """ENTRYPOINT ["deno","serve","--allow-net","--allow-env","--cached-only","--host","0.0.0.0","--port","8000","server.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public void AddDenoApp_DoesNotAddDenoPackageManagerWhenNoManifest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "main.ts"), "console.log('hi');");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var denoResource = Assert.Single(appModel.Resources.OfType<DenoAppResource>());

        // No package.json/deno.json: don't auto-configure Deno as a package manager and don't add an installer.
        Assert.False(denoResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out _));
        Assert.False(denoResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out _));
        Assert.Empty(appModel.Resources.OfType<JavaScriptInstallerResource>());
    }

    [Fact]
    public void AddDenoApp_AddsDenoPackageManagerWhenPackageJsonExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "package.json"), "{}");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var denoResource = Assert.Single(appModel.Resources.OfType<DenoAppResource>());

        Assert.True(denoResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("deno", packageManager.ExecutableName);
        Assert.Equal("task", packageManager.ScriptCommand);

        Assert.True(denoResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // Deno caches dependencies on first run, so no installer resource is created by default.
        Assert.Empty(appModel.Resources.OfType<JavaScriptInstallerResource>());
    }

    [Fact]
    public void AddDenoApp_AddsDenoPackageManagerWhenDenoJsonExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "deno.json"), "{}");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var denoResource = Assert.Single(appModel.Resources.OfType<DenoAppResource>());

        Assert.True(denoResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("deno", packageManager.ExecutableName);
        Assert.Equal("task", packageManager.ScriptCommand);
        Assert.Empty(appModel.Resources.OfType<JavaScriptInstallerResource>());
    }

    [Fact]
    public async Task AddDenoApp_DirectFile_ProducesRunDashAArgs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "main.ts"), "console.log('hi');");

        var builder = DistributedApplication.CreateBuilder();
        builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var denoResource = Assert.Single(appModel.Resources.OfType<DenoAppResource>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(denoResource);

        // Deno requires the `run` subcommand and, unlike Node/Bun, a permission grant (`-A`) to read
        // env vars and open sockets.
        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("-A", arg),
            arg => Assert.Equal("main.ts", arg));
    }

    [Fact]
    public async Task WithRunScript_SetsCustomTaskCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", ".", "main.ts")
            .WithDeno()
            .WithRunScript("start", ["--my-arg1"]);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var denoResource = Assert.Single(appModel.Resources.OfType<DenoAppResource>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(denoResource);

        // Deno runs package scripts through its task runner (`deno task <name>`).
        Assert.Collection(args,
            arg => Assert.Equal("task", arg),
            arg => Assert.Equal("start", arg),
            arg => Assert.Equal("--my-arg1", arg));
    }

    [Fact]
    public async Task WithRunScript_ComposesWithDenoFlagsAsTaskCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", ".", "main.ts")
            .WithDeno()
            .WithRunScript("start", ["--my-arg1"])
            .WithDenoConfig("deno.json")
            .WithDenoImportMap("import_map.json")
            .WithDenoLock("deno.lock")
            .WithDenoAllow(DenoPermissionKind.Net, "localhost");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var denoResource = Assert.Single(appModel.Resources.OfType<DenoAppResource>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(denoResource);

        // WithRunScript is still a Deno task launch when additional WithDeno* flags are added.
        // Task mode keeps valid task-level resolution flags and leaves permissions/import maps to the task body.
        Assert.Collection(args,
            arg => Assert.Equal("task", arg),
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("deno.json", arg),
            arg => Assert.Equal("--lock", arg),
            arg => Assert.Equal("deno.lock", arg),
            arg => Assert.Equal("start", arg),
            arg => Assert.Equal("--my-arg1", arg));
    }

    [Fact]
    public async Task WithRunScript_ExplicitDenoRunUsesEntrypoint()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDeno()
            .WithRunScript("start", ["--my-arg1"])
            .WithDenoRun()
            .WithDenoAllowAll(false)
            .WithDenoConfig("deno.json"));

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("deno.json", arg),
            arg => Assert.Equal("main.ts", arg));
    }

    [Fact]
    public void WithDenoAllow_NullBuilderThrowsArgumentNullException()
    {
        IResourceBuilder<DenoAppResource> builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => builder.WithDenoAllow(DenoPermissionKind.Net));

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithDenoPermission_RejectsUndefinedKind(bool deny)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var denoApp = builder.AddDenoApp("denoapp", AppContext.BaseDirectory, "main.ts");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (deny)
            {
                denoApp.WithDenoDeny((DenoPermissionKind)42);
            }
            else
            {
                denoApp.WithDenoAllow((DenoPermissionKind)42);
            }
        });

        Assert.Equal("kind", exception.ParamName);
        Assert.Equal(
            "The permission kind must be a defined DenoPermissionKind value. (Parameter 'kind')\nActual value was 42.",
            exception.Message.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "")]
    public void WithDenoPermission_RejectsNullOrEmptyValues(bool deny, string? value)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var denoApp = builder.AddDenoApp("denoapp", AppContext.BaseDirectory, "main.ts");

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            if (deny)
            {
                denoApp.WithDenoDeny(DenoPermissionKind.Read, value!);
            }
            else
            {
                denoApp.WithDenoAllow(DenoPermissionKind.Read, value!);
            }
        });

        Assert.Equal(
            "Deno permission values cannot be null or empty. (Parameter 'values')",
            exception.Message);
    }

    [Fact]
    public async Task WithDenoPermission_PreservesWhitespaceValue()
    {
        var args = await GetDenoArgsAsync(d => d.WithDenoAllow(DenoPermissionKind.Read, " "));

        Assert.Equal(["run", "--allow-read= ", "main.ts"], args);
    }

    [Fact]
    public void WithDenoInstallFalseDoesNotCreateInstallerWhenNoneExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithDeno(install: false);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Empty(appModel.Resources.OfType<JavaScriptInstallerResource>());
    }

    [Fact]
    public void WithDenoInstallTrueCreatesInstaller()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithDeno(install: true);

        using var distributedApplication = builder.Build();
        var appModel = distributedApplication.Services.GetRequiredService<DistributedApplicationModel>();
        var installer = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());

        Assert.False(installer.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
        Assert.Contains(app.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == installer);
    }

    [Fact]
    public void WithDenoInstallFalseDisablesExistingInstaller()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddJavaScriptApp("js", workspace.Path)
            .WithDeno(install: false);

        using var distributedApplication = builder.Build();
        var appModel = distributedApplication.Services.GetRequiredService<DistributedApplicationModel>();
        var installer = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());

        Assert.True(installer.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
        Assert.DoesNotContain(app.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == installer);
    }

    [Fact]
    public void WithDenoInstallTrueReEnablesDisabledInstaller()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddJavaScriptApp("js", workspace.Path)
            .WithDeno(install: false)
            .WithDeno(install: true);

        using var distributedApplication = builder.Build();
        var appModel = distributedApplication.Services.GetRequiredService<DistributedApplicationModel>();
        var installer = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());

        Assert.False(installer.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
        Assert.Single(app.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == installer);
    }

    [Fact]
    public async Task DenoOptionsWithAlternatePackageManagerThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithRunScript("start")
            .WithNpm()
            .WithDenoWatch();

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal(
            "Deno command-line options configured with the WithDeno* methods cannot be combined with package manager 'npm' on resource 'denoapp'. Remove the WithDeno* options or use WithDeno().",
            exception.Message);
    }

    [Fact]
    public async Task VerifyDockerfile_NormalizesHostPathSeparatorsForContainerPaths()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        // Windows AppHosts configure nested paths with backslashes; the Linux build/runtime stages need POSIX form.
        var denoApp = builder.AddDenoApp("js", appDir, @"src\main.ts")
            .WithDenoConfig(@"config\deno.json");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            "RUN deno cache --config config/deno.json src/main.ts",
            GetDockerfileLine(dockerfileContents, "RUN deno cache"));
        Assert.Equal(
            """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--config","config/deno.json","--cached-only","src/main.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_NormalizesPathsThatStayWithinBuildContext()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, @"src\..\main.ts")
            .WithDenoConfig("config/../deno.json")
            .WithDenoImportMap(@"maps\..\import_map.json")
            .WithDenoLock("locks/../deno.lock");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            "RUN deno cache --config deno.json --import-map import_map.json --lock deno.lock main.ts",
            GetDockerfileLine(dockerfileContents, "RUN deno cache"));
        Assert.Equal(
            """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--config","deno.json","--import-map","import_map.json","--lock","deno.lock","--cached-only","main.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Theory]
    [InlineData("-c", "config/../deno.json", "RUN deno cache -c deno.json main.ts", """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--cached-only","-c","deno.json","main.ts"]""")]
    [InlineData("-c=config/../deno.json", null, "RUN deno cache -c=deno.json main.ts", """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--cached-only","-c=deno.json","main.ts"]""")]
    public async Task VerifyDockerfile_NormalizesConfigAliasPath(string flag, string? value, string expectedCacheCommand, string expectedEntrypoint)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);
        string[] runtimeArgs = value is null ? [flag] : [flag, value];

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoRuntimeArgs(runtimeArgs);

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(expectedCacheCommand, GetDockerfileLine(dockerfileContents, "RUN deno cache"));
        Assert.Equal(expectedEntrypoint, GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Fact]
    public async Task VerifyDockerfile_PreservesRemoteImportMapUrl()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoImportMap("https://example.com/import_map.json");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(workspace.Path, "js.Dockerfile"));
        Assert.Equal(
            "RUN deno cache --import-map https://example.com/import_map.json main.ts",
            GetDockerfileLine(dockerfileContents, "RUN deno cache"));
        Assert.Equal(
            """ENTRYPOINT ["deno","run","--allow-net","--allow-env","--import-map","https://example.com/import_map.json","--cached-only","main.ts"]""",
            GetDockerfileLine(dockerfileContents, "ENTRYPOINT"));
    }

    [Theory]
    [InlineData("../shared/deno.json")]
    [InlineData("/etc/deno.json")]
    [InlineData("..")]
    // Traversal embedded mid-path still resolves outside the build context.
    [InlineData("config/../../outside.json")]
    // ToDenoContainerPath rewrites backslashes, so this becomes the absolute container path /tmp/deno.json
    // even though Path.IsPathRooted reports false for it on Linux and macOS.
    [InlineData("\\tmp\\deno.json")]
    [InlineData("\\\\server\\share\\deno.json")]
    [InlineData("C:\\temp\\deno.json")]
    [InlineData("C:/temp/deno.json")]
    [InlineData("C:temp\\deno.json")]
    public async Task VerifyDockerfile_RejectsConfigPathOutsideBuildContext(string configFile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "js");
        Directory.CreateDirectory(appDir);

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDenoConfig(configFile);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.GetManifest(denoApp.Resource, workspace.Path));
        Assert.Equal(
            $"The path '{configFile}' configured with WithDenoConfig is outside the Deno application directory, so it is not part of the generated Dockerfile's build context. Move the file inside the application directory or provide a custom Dockerfile.",
            exception.Message);
    }

    // Helper: build a Deno resource, apply the given flag configuration, and evaluate the emitted argument list.
    private static async Task<IReadOnlyList<string>> GetDenoArgsAsync(Action<IResourceBuilder<DenoAppResource>> configure, string entrypoint = "main.ts")
    {
        var builder = DistributedApplication.CreateBuilder();
        var deno = builder.AddDenoApp("denoapp", ".", entrypoint);
        configure(deno);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var denoResource = Assert.Single(appModel.Resources.OfType<DenoAppResource>());

        return await ArgumentEvaluator.GetArgumentListAsync(denoResource);
    }

    [Theory]
    [InlineData("--port", "3000")]
    [InlineData("--port=3000", null)]
    [InlineData("--host", "127.0.0.1")]
    [InlineData("--host=127.0.0.1", null)]
    public async Task WithDenoServe_RuntimeArgsOverridingHostOrPort_Throws(string flag, string? value)
    {
        // Deno rejects a repeated --host/--port ("cannot be used multiple times"), and serve mode always emits
        // the managed pair, so the resource would fail to start with a raw Deno parser error.
        string[] runtimeArgs = value is null ? [flag] : [flag, value];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetDenoArgsAsync(d => d.WithDenoServe().WithDenoRuntimeArgs(runtimeArgs)));

        Assert.Equal(
            $"The argument '{flag}' cannot be configured with WithDenoRuntimeArgs because WithDenoServe already emits --host and --port from the resource's endpoint, and Deno rejects those arguments when they are combined. Configure the endpoint instead, for example WithHttpEndpoint(port: 5005).",
            ex.Message);
    }

    // Deno hard-errors when a single-occurrence option is repeated ("cannot be used multiple times") or when
    // mutually exclusive flags are combined ("cannot be used with"), verified on 2.9.0. Both are clap errors
    // that never mention Aspire, so every managed flag must be guarded, not just the serve endpoint pair.
    [Theory]
    [InlineData("--config", "other.json")]
    [InlineData("--config=other.json", null)]
    [InlineData("-c", "other.json")]
    [InlineData("-c=other.json", null)]
    [InlineData("--no-config", null)]
    [InlineData("--import-map", "other.json")]
    [InlineData("--import-map=other.json", null)]
    [InlineData("--lock", "other.lock")]
    [InlineData("--lock=other.lock", null)]
    [InlineData("--no-lock", null)]
    [InlineData("--node-modules-dir", "none")]
    [InlineData("--node-modules-dir=none", null)]
    [InlineData("--watch", null)]
    [InlineData("--watch-hmr", null)]
    [InlineData("--inspect", null)]
    [InlineData("--inspect=127.0.0.1:9230", null)]
    public async Task WithDenoRuntimeArgs_DuplicatingAManagedFlag_Throws(string flag, string? value)
    {
        string[] runtimeArgs = value is null ? [flag] : [flag, value];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetDenoArgsAsync(d => d
                .WithDenoConfig("deno.json")
                .WithDenoImportMap("import_map.json")
                .WithDenoLock("deno.lock")
                .WithDenoNodeModulesDir()
                .WithDenoWatch()
                .WithDenoInspect()
                .WithDenoRuntimeArgs(runtimeArgs)));

        Assert.StartsWith($"The argument '{flag}' cannot be configured with WithDenoRuntimeArgs because ", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" instead.", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--config", "other.json")]
    [InlineData("--config=other.json", null)]
    [InlineData("-c", "other.json")]
    [InlineData("-c=other.json", null)]
    [InlineData("--no-config", null)]
    public async Task WithDenoRuntimeArgs_ConflictingConfigSpellingsThrowWithActionableGuidance(string flag, string? value)
    {
        string[] runtimeArgs = value is null ? [flag] : [flag, value];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetDenoArgsAsync(d => d
                .WithDenoConfig("deno.json")
                .WithDenoRuntimeArgs(runtimeArgs)));

        Assert.Equal(
            $"The argument '{flag}' cannot be configured with WithDenoRuntimeArgs because WithDenoConfig already emits --config, and Deno rejects those arguments when they are combined. Pass the configuration file to WithDenoConfig instead.",
            ex.Message);
    }

    [Fact]
    public async Task WithDenoRuntimeArgs_LockAndNoLockAreMutuallyExclusiveWithTheManagedFlag()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetDenoArgsAsync(d => d
                .WithDenoNoLock()
                .WithDenoRuntimeArgs("--lock", "other.lock")));

        Assert.Equal(
            "The argument '--lock' cannot be configured with WithDenoRuntimeArgs because WithDenoNoLock already emits --no-lock, and Deno rejects those arguments when they are combined. Configure locking with WithDenoNoLock instead.",
            ex.Message);
    }

    [Theory]
    // Repeatable options merge in Deno, so layering extra grants over the managed ones must keep working.
    [InlineData("--allow-read", "/var")]
    [InlineData("--allow-net", "example.com")]
    [InlineData("--parallel", null)]
    [InlineData("--v8-flags=--max-old-space-size=4096", null)]
    public async Task WithDenoRuntimeArgs_NonConflictingArgumentsArePreserved(string flag, string? value)
    {
        string[] runtimeArgs = value is null ? [flag] : [flag, value];

        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllow(DenoPermissionKind.Read, "/etc/app")
            .WithDenoConfig("deno.json")
            .WithDenoRuntimeArgs(runtimeArgs));

        Assert.Equal(["run", "--allow-read=/etc/app", "--config", "deno.json", .. runtimeArgs, "main.ts"], args);
    }

    // Task mode resolves configuration from deno.json and never emits --import-map or the development-only
    // watch/inspect flags, so those must stay available through the escape hatch.
    [Fact]
    public async Task WithDenoTask_RuntimeArgsForFlagsTaskModeDoesNotEmitArePreserved()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoTask("start")
            .WithDenoWatch()
            .WithDenoRuntimeArgs("--import-map", "import_map.json"));

        Assert.Equal(["task", "--import-map", "import_map.json", "start"], args);
    }

    [Fact]
    public async Task WithDenoTask_RuntimeArgsDuplicatingAManagedTaskFlag_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetDenoArgsAsync(d => d
                .WithDenoTask("start")
                .WithDenoConfig("deno.json")
                .WithDenoRuntimeArgs("--config", "other.json")));

        Assert.Equal(
            "The argument '--config' cannot be configured with WithDenoRuntimeArgs because WithDenoConfig already emits --config, and Deno rejects those arguments when they are combined. Pass the configuration file to WithDenoConfig instead.",
            ex.Message);
    }

    [Fact]
    public async Task WithDenoServe_UnrelatedRuntimeArgsArePreserved()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoServe()
            .WithHttpEndpoint(targetPort: 5173)
            .WithDenoRuntimeArgs("--parallel"), entrypoint: "server.ts");

        // Runtime args are appended after the managed --host/--port pair, before the entrypoint.
        Assert.Collection(args,
            a => Assert.Equal("serve", a),
            a => Assert.Equal("-A", a),
            a => Assert.Equal("--host", a),
            a => Assert.Equal("localhost", a),
            a => Assert.Equal("--port", a),
            a => Assert.Equal("5173", a),
            a => Assert.Equal("--parallel", a),
            a => Assert.Equal("server.ts", a));
    }

    [Fact]
    public async Task WithDenoWatch_HmrAndPlainAreMutuallyExclusive()
    {
        // Deno rejects "--watch-hmr" combined with "--watch", so only the last selection may be emitted.
        var hmrLast = await GetDenoArgsAsync(d => d.WithDenoWatch().WithDenoWatch(hmr: true));

        Assert.Collection(hmrLast,
            a => Assert.Equal("run", a),
            a => Assert.Equal("-A", a),
            a => Assert.Equal("--watch-hmr", a),
            a => Assert.Equal("main.ts", a));

        var plainLast = await GetDenoArgsAsync(d => d.WithDenoWatch(hmr: true).WithDenoWatch());

        Assert.Collection(plainLast,
            a => Assert.Equal("run", a),
            a => Assert.Equal("-A", a),
            a => Assert.Equal("--watch", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoPermission_MutatingTheCallerArrayAfterTheCallDoesNotChangeTheCommandLine()
    {
        // Permission values are only read when the command line is materialized, so holding the caller's
        // params array by reference would let a later mutation silently rewrite the launch arguments.
        var hosts = new[] { "localhost", "api.internal" };

        var args = await GetDenoArgsAsync(d =>
        {
            d.WithDenoAllow(DenoPermissionKind.Net, hosts);
            hosts[0] = "evil.example";
            hosts[1] = "attacker.example";
        });

        Assert.Equal(["run", "--allow-net=localhost,api.internal", "main.ts"], args);
    }

    [Fact]
    public async Task WithDenoAllowAll_False_DropsBlanketGrant()
    {
        // Least-privilege: explicitly opting out of -A must not emit any allow-all flag; only `run <script>`.
        var args = await GetDenoArgsAsync(d => d.WithDenoAllowAll(false));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoDenyOnly_DefaultRunKeepsBlanketGrant()
    {
        var args = await GetDenoArgsAsync(d => d.WithDenoDeny(DenoPermissionKind.Read, "/secrets"));

        Assert.Equal(["run", "-A", "--deny-read=/secrets", "main.ts"], args);
    }

    [Fact]
    public async Task WithDenoGranularPermissions_EmitInCanonicalOrderWithValues()
    {
        // Configured out of canonical order and across allow/deny to prove deterministic ordering
        // (net, read, write, run, env, import, sys, ffi; allow before deny) independent of call order.
        // Every granular permission API is exercised here: each one is a distinct one-line call into
        // AddDenoPermission, so a swapped deny flag or mistyped kind would otherwise ship unnoticed.
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllow(DenoPermissionKind.Env, "PORT", "HOME")
            .WithDenoDeny(DenoPermissionKind.Env, "SECRET")
            .WithDenoDeny(DenoPermissionKind.Import, "evil.example")
            .WithDenoDeny(DenoPermissionKind.Net, "evil.example")
            .WithDenoAllow(DenoPermissionKind.Net, "localhost:8080", "api.internal")
            .WithDenoAllow(DenoPermissionKind.Read, "/etc/app")
            .WithDenoDeny(DenoPermissionKind.Read, "/etc/shadow")
            .WithDenoAllow(DenoPermissionKind.Write, "/var/app")
            .WithDenoDeny(DenoPermissionKind.Write)
            .WithDenoAllow(DenoPermissionKind.Run, "git")
            .WithDenoDeny(DenoPermissionKind.Run, "curl")
            .WithDenoAllow(DenoPermissionKind.Import, "cdn.example")
            .WithDenoAllow(DenoPermissionKind.Sys)
            .WithDenoDeny(DenoPermissionKind.Sys, "hostname")
            .WithDenoAllow(DenoPermissionKind.Ffi, "./native.so")
            .WithDenoDeny(DenoPermissionKind.Ffi, "./blocked.so"));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--allow-net=localhost:8080,api.internal", a),
            a => Assert.Equal("--deny-net=evil.example", a),
            a => Assert.Equal("--allow-read=/etc/app", a),
            a => Assert.Equal("--deny-read=/etc/shadow", a),
            a => Assert.Equal("--allow-write=/var/app", a),
            a => Assert.Equal("--deny-write", a),
            a => Assert.Equal("--allow-run=git", a),
            a => Assert.Equal("--deny-run=curl", a),
            a => Assert.Equal("--allow-env=PORT,HOME", a),
            a => Assert.Equal("--deny-env=SECRET", a),
            a => Assert.Equal("--allow-import=cdn.example", a),
            a => Assert.Equal("--deny-import=evil.example", a),
            a => Assert.Equal("--allow-sys", a),
            a => Assert.Equal("--deny-sys=hostname", a),
            a => Assert.Equal("--allow-ffi=./native.so", a),
            a => Assert.Equal("--deny-ffi=./blocked.so", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoAllowAll_True_KeepsDenyFlagsButDropsRedundantAllows()
    {
        // -A subsumes granular allows; a deny flag still narrows it and must be preserved.
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll()
            .WithDenoAllow(DenoPermissionKind.Net, "localhost")
            .WithDenoDeny(DenoPermissionKind.Write, "/etc"));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("-A", a),
            a => Assert.Equal("--deny-write=/etc", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoResolutionFlags_EmitConfigImportMapLockNodeModulesDir()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoConfig("deno.json")
            .WithDenoImportMap("import_map.json")
            .WithDenoLock("deno.lock")
            .WithDenoNodeModulesDir(DenoNodeModulesDirMode.Auto));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--config", a),
            a => Assert.Equal("deno.json", a),
            a => Assert.Equal("--import-map", a),
            a => Assert.Equal("import_map.json", a),
            a => Assert.Equal("--lock", a),
            a => Assert.Equal("deno.lock", a),
            a => Assert.Equal("--node-modules-dir=auto", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public void WithDenoNodeModulesDir_RejectsUndefinedMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var denoApp = builder.AddDenoApp("denoapp", AppContext.BaseDirectory, "main.ts");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => denoApp.WithDenoNodeModulesDir((DenoNodeModulesDirMode)42));

        Assert.Equal("mode", exception.ParamName);
        Assert.Equal(
            "The node_modules mode must be a defined DenoNodeModulesDirMode value. (Parameter 'mode')\nActual value was 42.",
            exception.Message.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task WithDenoNoLock_OverridesLockAndEmitsNoLock()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoLock("deno.lock")
            .WithDenoNoLock()
            .WithDenoNodeModulesDir());

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--no-lock", a),
            a => Assert.Equal("--node-modules-dir", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoUnstable_NormalizesBareAndQualifiedFeatures()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoUnstable("kv", "worker-options")
            .WithDenoUnstable("--unstable-sloppy-imports"));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--unstable-kv", a),
            a => Assert.Equal("--unstable-worker-options", a),
            a => Assert.Equal("--unstable-sloppy-imports", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public void WithDenoUnstable_RejectsQualifiedNonUnstableFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var denoApp = builder.AddDenoApp("denoapp", AppContext.BaseDirectory, "main.ts");

        var exception = Assert.Throws<ArgumentException>(() => denoApp.WithDenoUnstable("--allow-all"));

        Assert.Equal("features", exception.ParamName);
    }

    [Fact]
    public async Task WithDenoWatchAndInspect_EmitInRuntimeFlagPosition()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoWatch()
            .WithDenoInspect(DenoInspectMode.InspectBrk, "127.0.0.1:9229"));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--watch", a),
            a => Assert.Equal("--inspect-brk=127.0.0.1:9229", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoInspect_EmitsPlainInspectFlag()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoInspect(hostPort: "127.0.0.1:9229"));

        Assert.Equal(["run", "--inspect=127.0.0.1:9229", "main.ts"], args);
    }

    [Fact]
    public void WithDenoInspect_RejectsUndefinedMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var denoApp = builder.AddDenoApp("denoapp", AppContext.BaseDirectory, "main.ts");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => denoApp.WithDenoInspect((DenoInspectMode)42));

        Assert.Equal("mode", exception.ParamName);
        Assert.Equal(
            "The inspect mode must be a defined DenoInspectMode value. (Parameter 'mode')\nActual value was 42.",
            exception.Message.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task WithDenoWatchHmr_EmitsWatchHmrFlag()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoWatch(hmr: true)
            .WithDenoInspect(DenoInspectMode.InspectWait));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--watch-hmr", a),
            a => Assert.Equal("--inspect-wait", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoWatch_RepeatedCallsEmitOnlyLastWatchMode()
    {
        var watchArgs = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoWatch(hmr: true)
            .WithDenoWatch());

        Assert.Collection(watchArgs,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--watch", a),
            a => Assert.Equal("main.ts", a));

        var hmrArgs = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoWatch()
            .WithDenoWatch(hmr: true));

        Assert.Collection(hmrArgs,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--watch-hmr", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoScriptArgs_AreEmittedAfterEntrypoint()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoScriptArgs("--port", "5000", "serve"));

        // Default -A grant preserved; script args follow the entrypoint.
        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("-A", a),
            a => Assert.Equal("main.ts", a),
            a => Assert.Equal("--port", a),
            a => Assert.Equal("5000", a),
            a => Assert.Equal("serve", a));
    }

    [Fact]
    public async Task WithDenoServe_EmitsServeMode()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoServe()
            .WithHttpEndpoint(targetPort: 5173)
            .WithDenoAllow(DenoPermissionKind.Net, "0.0.0.0:8000")
            .WithDenoScriptArgs("--config-arg"), entrypoint: "server.ts");

        Assert.Collection(args,
            a => Assert.Equal("serve", a),
            a => Assert.Equal("--allow-net=0.0.0.0:8000", a),
            a => Assert.Equal("--host", a),
            a => Assert.Equal("localhost", a),
            a => Assert.Equal("--port", a),
            a => Assert.Equal("5173", a),
            a => Assert.Equal("server.ts", a),
            a => Assert.Equal("--config-arg", a));
    }

    [Fact]
    public async Task WithDenoServe_PreservesPreconfiguredEndpointTargetPort()
    {
        var args = await GetDenoArgsAsync(d =>
        {
            d.WithHttpEndpoint(targetPort: 5173);
            d.WithDenoServe();
        }, entrypoint: "server.ts");

        Assert.Collection(args,
            a => Assert.Equal("serve", a),
            a => Assert.Equal("-A", a),
            a => Assert.Equal("--host", a),
            a => Assert.Equal("localhost", a),
            a => Assert.Equal("--port", a),
            a => Assert.Equal("5173", a),
            a => Assert.Equal("server.ts", a));
    }

    [Fact]
    public async Task WithDenoTask_EmitsTaskModeAndIgnoresPermissionFlags()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoTask("dev")
            .WithDenoAllow(DenoPermissionKind.Net, "localhost") // permissions belong to the task; must not be emitted
            .WithDenoConfig("deno.json")
            .WithDenoImportMap("import_map.json") // Deno 2.5.6 rejects --import-map on `deno task`
            .WithDenoLock("deno.lock")
            .WithDenoNodeModulesDir(DenoNodeModulesDirMode.Auto)
            .WithDenoScriptArgs("--flag"));

        Assert.Collection(args,
            a => Assert.Equal("task", a),
            a => Assert.Equal("--config", a),
            a => Assert.Equal("deno.json", a),
            a => Assert.Equal("--lock", a),
            a => Assert.Equal("deno.lock", a),
            a => Assert.Equal("--node-modules-dir=auto", a),
            a => Assert.Equal("dev", a),
            a => Assert.Equal("--flag", a));
    }

    [Fact]
    public async Task WithDenoRuntimeArgs_EscapeHatch_InjectsBeforeEntrypoint()
    {
        // AddExecutable-replacement escape hatch: any flag not covered by a dedicated method can be injected raw
        // before the script, giving parity with AddExecutable("name", "deno", workdir, args...).
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoRuntimeArgs("--v8-flags=--max-old-space-size=4096", "--seed", "42"));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--v8-flags=--max-old-space-size=4096", a),
            a => Assert.Equal("--seed", a),
            a => Assert.Equal("42", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task AddDenoApp_ReplacesAddExecutable_FullPolyglotConfiguration()
    {
        // A NetScript-style configuration that previously required dropping back to
        // AddExecutable("gateway", "deno", workdir, "run", "--allow-net", ... , "main.ts", "--serve")
        // is now fully expressible through AddDenoApp + fluent flags, and yields an equivalent arg vector.
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllow(DenoPermissionKind.Net, "0.0.0.0:8000", "db:5432")
            .WithDenoAllow(DenoPermissionKind.Env, "PORT", "DATABASE_URL")
            .WithDenoAllow(DenoPermissionKind.Read, "./config")
            .WithDenoConfig("deno.json")
            .WithDenoUnstable("kv")
            .WithDenoScriptArgs("--serve"), entrypoint: "main.ts");

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--allow-net=0.0.0.0:8000,db:5432", a),
            a => Assert.Equal("--allow-read=./config", a),
            a => Assert.Equal("--allow-env=PORT,DATABASE_URL", a),
            a => Assert.Equal("--config", a),
            a => Assert.Equal("deno.json", a),
            a => Assert.Equal("--unstable-kv", a),
            a => Assert.Equal("main.ts", a),
            a => Assert.Equal("--serve", a));
    }

    [Fact]
    public void AddDenoApp_PermissionValueContainingComma_Throws()
    {
        // Deno splits permission values on commas with no escape syntax, so a comma-containing value would
        // silently become several permissions - denying the requested one and granting unrelated ones.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var builder = DistributedApplication.CreateBuilder();
        var deno = builder.AddDenoApp("deno", workspace.WorkspaceRoot.FullName, "main.ts");

        var ex = Assert.Throws<ArgumentException>(() => deno.WithDenoAllow(DenoPermissionKind.Read, "data,secret"));
        Assert.Equal(
            "The value 'data,secret' cannot contain a comma. Deno separates --allow-read values with commas and provides no way to escape them, so this value would be interpreted as multiple permissions. Pass each value as a separate argument. (Parameter 'values')",
            ex.Message);

        var denyEx = Assert.Throws<ArgumentException>(() => deno.WithDenoDeny(DenoPermissionKind.Net, "a.example,b.example"));
        Assert.Equal(
            "The value 'a.example,b.example' cannot contain a comma. Deno separates --deny-net values with commas and provides no way to escape them, so this value would be interpreted as multiple permissions. Pass each value as a separate argument. (Parameter 'values')",
            denyEx.Message);

        // Separate arguments remain the supported way to express multiple values.
        deno.WithDenoAllow(DenoPermissionKind.Read, "data", "secret");
    }

    [Fact]
    public void AddDenoApp_UsesDenoCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var builder = DistributedApplication.CreateBuilder();
        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        Assert.Equal("deno", denoApp.Resource.Command);
    }

    [Fact]
    public void AddDenoApp_ThrowsForNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            JavaScriptHostingExtensions.AddDenoApp(null!, "denoapp", ".", "main.ts"));
    }

    [Fact]
    public void AddDenoApp_ThrowsForEmptyName()
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddDenoApp("", ".", "main.ts"));
    }

    [Fact]
    public void AddDenoApp_ThrowsForEmptyScriptPath()
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddDenoApp("denoapp", ".", ""));
    }

    [Theory]
    [InlineData("/tmp/main.ts")]
    [InlineData("../main.ts")]
    [InlineData("sub/../../main.ts")]
    [InlineData("\\tmp\\main.ts")]
    [InlineData("\\\\server\\share\\main.ts")]
    [InlineData("C:\\temp\\main.ts")]
    [InlineData("C:/temp/main.ts")]
    [InlineData("C:temp\\main.ts")]
    public void AddDenoApp_ThrowsForScriptPathOutsideAppDirectory(string scriptPath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = DistributedApplication.CreateBuilder();

        var exception = Assert.Throws<ArgumentException>(() => builder.AddDenoApp("denoapp", workspace.Path, scriptPath));

        Assert.Equal("scriptPath", exception.ParamName);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.ContainerImageBuild)]
    [OuterloopTest("Builds and runs a Docker image to verify the generated Deno Dockerfile serves HTTP")]
    public async Task VerifyDenoDockerfileBuildsAndRunsHttpEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: workspace.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(workspace.Path, "deno-app");
        Directory.CreateDirectory(appDir);
        await File.WriteAllTextAsync(Path.Combine(appDir, "main.ts"), """
            const port = Number(Deno.env.get("PORT") ?? "8000");
            Deno.serve({ hostname: "0.0.0.0", port }, () => new Response("deno runtime ok"));
            """, TestContext.Current.CancellationToken);

        var denoApp = builder.AddDenoApp("deno-app", appDir, "main.ts");

        await ManifestUtils.GetManifest(denoApp.Resource, workspace.Path);

        var dockerfilePath = Path.Combine(workspace.Path, "deno-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");

        var imageName = $"aspire-deno-runtime-test-{Guid.NewGuid():N}";
        string? containerId = null;

        try
        {
            var buildResult = await RunDockerCommandAsync($"build --network=host -t {imageName} -f \"{dockerfilePath}\" .", appDir);
            Assert.True(buildResult.ExitCode == 0, $"Docker build failed with exit code {buildResult.ExitCode}.\nStdout: {buildResult.Stdout}\nStderr: {buildResult.Stderr}");

            var runResult = await RunDockerCommandAsync($"run --rm -d -e PORT=8000 -p 127.0.0.1::8000 {imageName}", appDir);
            Assert.True(runResult.ExitCode == 0, $"Docker run failed with exit code {runResult.ExitCode}.\nStdout: {runResult.Stdout}\nStderr: {runResult.Stderr}");
            containerId = runResult.Stdout.Trim();

            var portResult = await RunDockerCommandAsync($"port {containerId} 8000/tcp", appDir);
            Assert.True(portResult.ExitCode == 0, $"Docker port failed with exit code {portResult.ExitCode}.\nStdout: {portResult.Stdout}\nStderr: {portResult.Stderr}");

            await WaitForHttpTextAsync($"http://{portResult.Stdout.Trim()}", "deno runtime ok");
        }
        finally
        {
            if (!string.IsNullOrEmpty(containerId))
            {
                await RunDockerCommandAsync($"rm -f {containerId}", appDir);
            }

            await RunDockerCommandAsync($"rmi {imageName}", appDir);
        }
    }

    [Fact]
    public async Task AddDenoApp_ConfiguresCertificateTrustForAppendScope()
    {
        var builder = DistributedApplication.CreateBuilder();
        var denoApp = builder.AddDenoApp("denoapp", ".", "main.ts");

        Assert.True(denoApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        var envVars = new Dictionary<string, object>();
        var bundle = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.crt");
        var dirs = ReferenceExpression.Create($"/etc/ssl/aspire/certs");
        var ctx = new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = CreateRunExecutionContext(builder.Services),
            Resource = denoApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = bundle,
            CertificateDirectoriesPath = dirs,
            Scope = CertificateTrustScope.Append,
            CancellationToken = default,
        };

        await annotation.Callback(ctx);

        // Deno loads an additional PEM certificate via DENO_CERT on top of its bundled Mozilla store.
        // Its native OTLP exporter is implemented in Rust and reads the OpenTelemetry certificate variable.
        Assert.Same(bundle, envVars["DENO_CERT"]);
        Assert.Same(bundle, envVars["OTEL_EXPORTER_OTLP_CERTIFICATE"]);
    }

    [Fact]
    public async Task AddDenoApp_ConfiguresCertificateTrustForOverrideScope()
    {
        var builder = DistributedApplication.CreateBuilder();
        var denoApp = builder.AddDenoApp("denoapp", ".", "main.ts");

        Assert.True(denoApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        var envVars = new Dictionary<string, object>();
        var ctx = new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = CreateRunExecutionContext(builder.Services),
            Resource = denoApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.crt"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/etc/ssl/aspire/certs"),
            Scope = CertificateTrustScope.Override,
            CancellationToken = default,
        };

        await annotation.Callback(ctx);

        Assert.Same(ctx.CertificateBundlePath, envVars["DENO_CERT"]);
        Assert.Same(ctx.CertificateBundlePath, envVars["OTEL_EXPORTER_OTLP_CERTIFICATE"]);
        Assert.Equal("", envVars["DENO_TLS_CA_STORE"]);
    }

    [Fact]
    public async Task AddDenoApp_ConfiguresCertificateTrustForSystemScope()
    {
        var builder = DistributedApplication.CreateBuilder();
        var denoApp = builder.AddDenoApp("denoapp", ".", "main.ts");

        Assert.True(denoApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        var envVars = new Dictionary<string, object>();
        var ctx = new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = CreateRunExecutionContext(builder.Services),
            Resource = denoApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.crt"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/etc/ssl/aspire/certs"),
            Scope = CertificateTrustScope.System,
            CancellationToken = default,
        };

        await annotation.Callback(ctx);

        Assert.Equal("system", envVars["DENO_TLS_CA_STORE"]);
        Assert.Same(ctx.CertificateBundlePath, envVars["DENO_CERT"]);
        Assert.Same(ctx.CertificateBundlePath, envVars["OTEL_EXPORTER_OTLP_CERTIFICATE"]);
    }

    [Fact]
    public async Task AddDenoApp_EnablesNativeOpenTelemetry()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:4318";
        var denoApp = builder.AddDenoApp("denoapp", ".", "main.ts");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(denoApp.Resource, DistributedApplicationOperation.Run);

        // Deno's built-in OpenTelemetry integration is enabled with a single environment variable.
        Assert.Equal("true", env["OTEL_DENO"]);
        Assert.Equal("http://localhost:4318", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal("http/protobuf", env["OTEL_EXPORTER_OTLP_PROTOCOL"]);
    }

    [Fact]
    public async Task AddDenoApp_RemainsRunnableWithoutDashboardOrExternalOtlpEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true,
            outputHelper);
        var denoApp = builder.AddDenoApp("denoapp", ".", "main.ts");

        var environment = await EnvironmentVariableEvaluator
            .GetEnvironmentVariablesAsync(denoApp.Resource, DistributedApplicationOperation.Run);

        Assert.Equal(
            [new("NODE_ENV", "production")],
            environment.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task AddDenoApp_DoesNotEnableNativeOpenTelemetryInPlainPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var denoApp = builder.AddDenoApp("denoapp", ".", "main.ts");

        var environment = await EnvironmentVariableEvaluator
            .GetEnvironmentVariablesAsync(denoApp.Resource, DistributedApplicationOperation.Publish);

        Assert.False(environment.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT"));
        Assert.False(environment.ContainsKey("OTEL_DENO"));
    }

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only

    [Fact]
    public void DenoApp_WithVSCodeDebugging_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        var annotation = denoApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("deno", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void DenoApp_WithVSCodeDebugging_DoesNotAddAnnotationInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        var annotation = denoApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }

    [Fact]
    public void DenoApp_WithRunScript_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithRunScript("dev");

        var annotation = denoApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("deno", annotation.LaunchConfigurationType);
    }

    [Fact]
    public async Task DenoApp_DirectFile_ProducesDenoRuntimeExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        var launchConfig = await CreateLaunchConfigurationAsync(denoApp.Resource);

        Assert.Equal("deno", launchConfig.Type);
        Assert.Equal("deno", launchConfig.RuntimeExecutable);
        Assert.Equal("direct", launchConfig.LaunchMethod);
        Assert.Equal(Path.GetFullPath("main.ts", workspace.Path), launchConfig.ScriptPath);
    }

    [Fact]
    public async Task DenoApp_WithRunScriptAndPackageManager_ProducesDenoRuntimeExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // AddDenoApp automatically calls WithDeno() when a deno.json exists, which makes the run-script a
        // package-manager invocation (deno task dev).
        File.WriteAllText(Path.Combine(workspace.Path, "deno.json"), "{}");

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithRunScript("dev");

        var launchConfig = await CreateLaunchConfigurationAsync(denoApp.Resource);

        Assert.Equal("deno", launchConfig.Type);
        Assert.Equal("deno", launchConfig.RuntimeExecutable);
        Assert.Equal("package-manager", launchConfig.LaunchMethod);
    }

    [Fact]
    public async Task DenoApp_WithRunScriptAndExplicitDenoRun_ProducesDirectLaunchMethod()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "deno.json"), "{}");

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithRunScript("dev")
            .WithDenoRun();

        var launchConfig = await CreateLaunchConfigurationAsync(denoApp.Resource);

        Assert.Equal("deno", launchConfig.Type);
        Assert.Equal("deno", launchConfig.RuntimeExecutable);
        Assert.Equal("direct", launchConfig.LaunchMethod);
    }

    [Fact]
    public async Task DenoApp_WithRunScriptAndExplicitDenoServe_ProducesDirectLaunchMethod()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "deno.json"), "{}");

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithRunScript("dev")
            .WithDenoServe();

        var launchConfig = await CreateLaunchConfigurationAsync(denoApp.Resource);

        Assert.Equal("deno", launchConfig.Type);
        Assert.Equal("deno", launchConfig.RuntimeExecutable);
        Assert.Equal("direct", launchConfig.LaunchMethod);
    }

    [Fact]
    public async Task DenoApp_WithDenoTask_ProducesPackageManagerLaunchMethod()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithDenoTask("dev");

        var launchConfig = await CreateLaunchConfigurationAsync(denoApp.Resource);

        Assert.Equal("deno", launchConfig.Type);
        Assert.Equal("deno", launchConfig.RuntimeExecutable);
        Assert.Equal("package-manager", launchConfig.LaunchMethod);
    }

    [Fact]
    public async Task DenoApp_WithRunScriptAndNpm_ProducesNodeLaunchConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts")
            .WithRunScript("dev")
            .WithNpm();

        var launchConfig = await CreateLaunchConfigurationAsync(denoApp.Resource);

        Assert.Equal("node", launchConfig.Type);
        Assert.Equal("npm", launchConfig.RuntimeExecutable);
        Assert.Equal("package-manager", launchConfig.LaunchMethod);
    }

    [Fact]
    public async Task DenoApp_LaunchConfigurationHonorsCancellation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var denoApp = builder.AddDenoApp("denoapp", workspace.Path, "main.ts");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateLaunchConfigurationAsync(denoApp.Resource, cancellationTokenSource.Token));
    }

    private static async Task<JavaScriptLaunchConfiguration> CreateLaunchConfigurationAsync(
        IResource resource,
        CancellationToken cancellationToken = default)
    {
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            resource,
            cancellationToken: cancellationToken);
        return Assert.IsType<JavaScriptLaunchConfiguration>(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(resource, callbackContext));
    }

    private static DistributedApplicationExecutionContext CreateRunExecutionContext(IServiceCollection services) =>
        new(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
        {
            Services = services.BuildServiceProvider()
        });

    private static string GetDockerfileLine(string dockerfileContents, string prefix)
        => dockerfileContents.Split('\n').Select(line => line.TrimEnd('\r')).Single(line => line.StartsWith(prefix, StringComparison.Ordinal));

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

        // Read both streams concurrently so a full pipe cannot deadlock the Docker process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between HasExited and Kill.
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task WaitForHttpTextAsync(string url, string expectedText)
    {
        using var httpClient = new HttpClient();
        string? lastError = null;

        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                var response = await httpClient.GetStringAsync(url, TestContext.Current.CancellationToken);
                if (response.Contains(expectedText, StringComparison.Ordinal))
                {
                    return;
                }

                lastError = $"Response did not contain '{expectedText}': {response}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex.Message;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {url} to return '{expectedText}'. Last error: {lastError ?? "<none>"}");
    }

#pragma warning restore ASPIREEXTENSION001 // Type is for evaluation purposes only
}
