// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Integration tests for monorepo Dockerfile generation. These exercise the
// filesystem-touching parts of the workspace pipeline (manifest discovery,
// pattern expansion, package-manager re-resolution) end-to-end through the
// existing Aspire publish pipeline. Pure parser/matcher/validator tests live
// in tests/Aspire.Hosting.JavaScript.Tests/Internal/WorkspaceParserTests.cs.

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREJAVASCRIPT001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class JavaScriptWorkspaceIntegrationTests
{
    // ----- Item #1 / #9: install args preservation + PM-order matrix -----

    [Fact]
    public async Task WithNpm_InstallArgsBeforeWithWorkspaceRoot_PreservedInDockerfile()
    {
        // The pre-A1 bug: WithNpm(installArgs: [...]) followed by WithWorkspaceRoot
        // dropped the user's args because the reconfigure path built a fresh
        // install annotation from scratch. Verify they survive.
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithNpm(installArgs: ["--audit=false", "--fund=false"])
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        // Lockfile-derived verb is recomputed against the workspace root (ci, since lockfile present).
        // User's --audit=false / --fund=false survive.
        Assert.Contains("npm ci --audit=false --fund=false", dockerfile);
    }

    [Fact]
    public async Task WithYarn_InstallArgsBeforeWithWorkspaceRoot_PreservedInDockerfile()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web"]);
        File.WriteAllText(Path.Combine(tempDir.Path, "yarn.lock"), "");
        File.Delete(Path.Combine(tempDir.Path, "package-lock.json"));
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithYarn(installArgs: ["--ignore-scripts"])
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        // Workspace yarn.lock present (yarn classic — no .yarnrc.yml or .yarn/) => --frozen-lockfile;
        // user's --ignore-scripts survives.
        Assert.Contains("yarn install --frozen-lockfile --ignore-scripts", dockerfile);
    }

    [Fact]
    public async Task WithNpm_InstallArgsContainingFlagsAndDuplicateLockfileFlag_AreNotDuplicated()
    {
        // If the user passes --frozen-lockfile (yarn) explicitly, our merge
        // strips it from extras and re-adds it once, avoiding "yarn install
        // --frozen-lockfile --frozen-lockfile" in the Dockerfile.
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web"]);
        File.WriteAllText(Path.Combine(tempDir.Path, "yarn.lock"), "");
        File.Delete(Path.Combine(tempDir.Path, "package-lock.json"));
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithYarn(installArgs: ["--frozen-lockfile", "--silent"])
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        Assert.Contains("yarn install --frozen-lockfile --silent", dockerfile);
        Assert.DoesNotContain("--frozen-lockfile --frozen-lockfile", dockerfile);
    }

    [Fact]
    public void WithPnpm_RunMode_AddInstaller_AttachesInstallerResource()
    {
        // Run-mode AddInstaller in workspace mode: verify the installer resource is created
        // alongside the parent. The installer's effective WorkingDirectory is set lazily
        // in OnBeforeStart (so it can read the JavaScriptWorkspaceAnnotation that
        // WithWorkspaceRoot attached); validating the actual cwd value requires running
        // OnBeforeStart hooks which is out of scope for unit tests here.
        using var tempDir = new TestTempDirectory();
        WritePnpmWorkspace(tempDir.Path, ["packages/web"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Run)
            .WithResourceCleanUp(true);

        builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path)
            .WithPnpm(install: true);

        using var app = builder.Build();
        var installerResources = app.Services
            .GetRequiredService<DistributedApplicationModel>()
            .Resources
            .OfType<JavaScriptInstallerResource>()
            .ToList();

        Assert.Single(installerResources);
        Assert.Equal("web-installer", installerResources[0].Name);
    }

    // ----- Item #4: PublishAsStaticWebsite + workspace -----

    [Fact]
    public async Task PublishAsStaticWebsite_WithWorkspaceRoot_RewritesYarpSourcePathToWorkspaceLayout()
    {
        // Vite app published as static website inside a workspace: the Yarp
        // wrapper container needs the static asset path under the workspace
        // app subdirectory ("/app/<member>/dist"), not "/app/dist".
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["apps/web"]);
        var appDir = Path.Combine(tempDir.Path, "apps", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var viteApp = builder.AddViteApp("web", appDir)
            .WithWorkspaceRoot(tempDir.Path)
            .PublishAsStaticWebsite();

        await ManifestUtils.GetManifest(viteApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY apps/web/package.json ./apps/web/package.json", dockerfile);
        Assert.Contains("npm run build --workspace=@example/web", dockerfile);

        // ContainerFilesSource path is rewritten under apps/web for the Yarp host.
        var source = viteApp.Resource.Annotations
            .OfType<ContainerFilesSourceAnnotation>()
            .Single();
        Assert.Equal("/app/apps/web/dist", source.SourcePath);
    }

    [Fact]
    public async Task PublishAsStaticWebsite_BeforeWithWorkspaceRoot_StillRewritesYarpSourcePath()
    {
        // RewriteContainerFilesSourcesForWorkspace branch: PublishAsStaticWebsite
        // captures the source path eagerly (as "/app/dist"), then WithWorkspaceRoot
        // must rewrite it to "/app/apps/web/dist".
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["apps/web"]);
        var appDir = Path.Combine(tempDir.Path, "apps", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var viteApp = builder.AddViteApp("web", appDir)
            .PublishAsStaticWebsite()
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(viteApp.Resource, tempDir.Path);

        var source = viteApp.Resource.Annotations
            .OfType<ContainerFilesSourceAnnotation>()
            .Single();
        Assert.Equal("/app/apps/web/dist", source.SourcePath);
    }

    // ----- Item #5: PublishAsNextStandalone + workspace -----

    [Fact]
    public async Task PublishAsNextStandalone_WithWorkspaceRoot_PrefixesEntrypointAndCopyPaths()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["apps/web"]);
        var appDir = Path.Combine(tempDir.Path, "apps", "web");
        WriteAppPackageJson(appDir, "@example/web", scripts: """{"build":"next build","start":"next start"}""");
        File.WriteAllText(Path.Combine(appDir, "next.config.js"), "module.exports = { output: 'standalone' };\n");
        Directory.CreateDirectory(Path.Combine(appDir, "public"));

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nextApp = builder.AddNextJsApp("web", appDir)
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(nextApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY apps/web/package.json ./apps/web/package.json", dockerfile);
        Assert.Contains("npm run build --workspace=@example/web", dockerfile);
        // Standalone copies live under the app subdir (next emits .next/standalone, .next/static, public).
        Assert.Contains("/app/apps/web/.next/standalone", dockerfile);
        Assert.Contains("/app/apps/web/.next/static", dockerfile);
        Assert.Contains("/app/apps/web/public", dockerfile);
    }

    // ----- Item #6: PublishAsNodeServer + pnpm + workspace (existing test only covers npm) -----

    [Fact]
    public async Task PublishAsNodeServer_PnpmWorkspace_PrefixesOutputPathUnderDeployBundle()
    {
        using var tempDir = new TestTempDirectory();
        WritePnpmWorkspace(tempDir.Path, ["packages/api"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "api");
        WriteAppPackageJson(appDir, "@example/api", scripts: """{"build":"echo build"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var app = builder.AddJavaScriptApp("api", appDir, runScriptName: "start")
            .WithWorkspaceRoot(tempDir.Path)
            .WithPnpm()
            .PublishAsNodeServer(entryPoint: "dist/index.js", outputPath: "dist");

        await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "api");

        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY packages/api/package.json ./packages/api/package.json", dockerfile);
        Assert.Contains("pnpm --filter @example/api... run build", dockerfile);
        Assert.Contains("COPY --from=build /app/packages/api/dist /app/packages/api/dist", dockerfile);
        Assert.Contains("ENTRYPOINT [\"node\",\"packages/api/dist/index.js\"]", dockerfile);
    }

    // ----- Item #7: WithBuildScript shape for npm/yarn/bun in workspace mode -----

    [Fact]
    public async Task WithBuildScript_NpmWorkspace_UsesNpmRunWorkspaceFilter()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/api"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "api");
        WriteAppPackageJson(appDir, "@example/api", scripts: """{"build":"echo build"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var app = builder.AddJavaScriptApp("api", appDir)
            .WithWorkspaceRoot(tempDir.Path)
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "api");
        Assert.Contains("npm run build --workspace=@example/api", dockerfile);
    }

    [Fact]
    public async Task WithBuildScript_YarnWorkspace_UsesYarnWorkspaceCommand()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/api"]);
        File.WriteAllText(Path.Combine(tempDir.Path, "yarn.lock"), "");
        File.Delete(Path.Combine(tempDir.Path, "package-lock.json"));
        var appDir = Path.Combine(tempDir.Path, "packages", "api");
        WriteAppPackageJson(appDir, "@example/api", scripts: """{"build":"echo build"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var app = builder.AddJavaScriptApp("api", appDir)
            .WithWorkspaceRoot(tempDir.Path)
            .WithYarn()
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "api");
        Assert.Contains("yarn workspace @example/api run build", dockerfile);
    }

    [Fact]
    public async Task WithBuildScript_BunWorkspace_UsesBunFilterCommand()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/api"]);
        File.WriteAllText(Path.Combine(tempDir.Path, "bun.lock"), "");
        File.Delete(Path.Combine(tempDir.Path, "package-lock.json"));
        var appDir = Path.Combine(tempDir.Path, "packages", "api");
        WriteAppPackageJson(appDir, "@example/api", scripts: """{"build":"echo build"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var app = builder.AddJavaScriptApp("api", appDir)
            .WithWorkspaceRoot(tempDir.Path)
            .WithBun()
            .WithBuildScript("build");

        await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "api");
        Assert.Contains("bun run --filter @example/api build", dockerfile);
    }

    // ----- Item #10: alternate manifest forms -----

    [Fact]
    public async Task WithWorkspaceRoot_NpmShrinkwrap_RecognizedAsLockfile()
    {
        // npm 7+ honors npm-shrinkwrap.json over package-lock.json. Verify it
        // also satisfies our "must have a recognized lockfile" requirement.
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web"]);
        File.Delete(Path.Combine(tempDir.Path, "package-lock.json"));
        File.WriteAllText(Path.Combine(tempDir.Path, "npm-shrinkwrap.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        Assert.Contains("COPY npm-shrinkwrap.json ./npm-shrinkwrap.json", dockerfile);
    }

    [Fact]
    public async Task WithWorkspaceRoot_BunTextLockfile_RecognizedAsLockfile()
    {
        // Bun 1.1+ ships a text-format bun.lock alongside the legacy bun.lockb.
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web"]);
        File.WriteAllText(Path.Combine(tempDir.Path, "bun.lock"), "");
        File.Delete(Path.Combine(tempDir.Path, "package-lock.json"));
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path)
            .WithBun();

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        Assert.Contains("COPY bun.lock ./bun.lock", dockerfile);
        Assert.Contains("bun install --frozen-lockfile", dockerfile);
    }

    [Fact]
    public async Task WithWorkspaceRoot_PackageJsonObjectFormWorkspaces_ResolvesMembers()
    {
        // Yarn classic's { workspaces: { packages: [...] } } form resolves
        // identically to the array form.
        using var tempDir = new TestTempDirectory();
        Directory.CreateDirectory(tempDir.Path);
        File.WriteAllText(
            Path.Combine(tempDir.Path, "package.json"),
            """{ "name":"root", "private":true, "workspaces":{ "packages":["packages/*"], "nohoist":["**/react"] } }""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        Assert.Contains("COPY packages/web/package.json ./packages/web/package.json", dockerfile);
        Assert.Contains("npm ci", dockerfile);
    }

    // ----- helpers (mirror JavaScriptWorkspaceTests) -----

    private static void WriteNpmWorkspace(string rootPath, string[] patterns)
    {
        Directory.CreateDirectory(rootPath);
        var patternsJson = string.Join(",", patterns.Select(p => "\"" + p + "\""));
        File.WriteAllText(
            Path.Combine(rootPath, "package.json"),
            $$"""{"name":"workspace-root","private":true,"workspaces":[{{patternsJson}}]}""");
        File.WriteAllText(Path.Combine(rootPath, "package-lock.json"), "{}");

        foreach (var pattern in patterns)
        {
            if (pattern.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }
            var memberDir = Path.Combine(rootPath, pattern.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(memberDir);
            var stubName = "@example/" + Path.GetFileName(pattern);
            File.WriteAllText(Path.Combine(memberDir, "package.json"), "{\"name\":\"" + stubName + "\"}");
        }
    }

    private static void WritePnpmWorkspace(string rootPath, string[] packages)
    {
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(
            Path.Combine(rootPath, "package.json"),
            "{\"name\":\"workspace-root\",\"private\":true}");
        var lines = string.Join("\n", packages.Select(p => "  - '" + p + "'"));
        File.WriteAllText(
            Path.Combine(rootPath, "pnpm-workspace.yaml"),
            "packages:\n" + lines + "\n");
        File.WriteAllText(Path.Combine(rootPath, "pnpm-lock.yaml"), "");

        foreach (var pattern in packages)
        {
            if (pattern.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }
            var memberDir = Path.Combine(rootPath, pattern.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(memberDir);
            var stubName = "@example/" + Path.GetFileName(pattern);
            File.WriteAllText(Path.Combine(memberDir, "package.json"), "{\"name\":\"" + stubName + "\"}");
        }
    }

    private static void WriteAppPackageJson(string appDir, string name, string? scripts = null)
    {
        Directory.CreateDirectory(appDir);
        var payload = scripts is null
            ? "{\"name\":\"" + name + "\"}"
            : "{\"name\":\"" + name + "\",\"scripts\":" + scripts + "}";
        File.WriteAllText(Path.Combine(appDir, "package.json"), payload);
    }

    private static string ReadDockerfile(string outputPath, string resourceName)
    {
        var path = Path.Combine(outputPath, resourceName + ".Dockerfile");
        return File.ReadAllText(path);
    }
}
