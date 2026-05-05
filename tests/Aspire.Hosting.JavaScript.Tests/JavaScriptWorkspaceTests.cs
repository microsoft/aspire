// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREJAVASCRIPT001

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.JavaScript.Tests;

public class JavaScriptWorkspaceTests
{
    [Fact]
    public async Task AddNodeApp_WithWorkspaceRoot_NpmWorkspace_GeneratesWorkspaceDockerfile()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web", "packages/api", "packages/shared"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        var expected = """
            FROM node:22-alpine AS build

            WORKDIR /app
            COPY package.json ./package.json
            COPY package-lock.json ./package-lock.json
            COPY packages/api/package.json ./packages/api/package.json
            COPY packages/shared/package.json ./packages/shared/package.json
            COPY packages/web/package.json ./packages/web/package.json
            RUN --mount=type=cache,target=/root/.npm npm ci
            COPY . .

            FROM node:22-alpine AS runtime

            WORKDIR /app/packages/web
            COPY --from=build /app /app

            ENV NODE_ENV=production

            USER node

            ENTRYPOINT ["node","app.js"]

            """.Replace("\r\n", "\n");

        Assert.Equal(expected, dockerfile);
    }

    [Fact]
    public async Task AddNodeApp_WithWorkspaceRoot_PnpmWorkspace_GeneratesWorkspaceDockerfile()
    {
        using var tempDir = new TestTempDirectory();
        WritePnpmWorkspace(tempDir.Path, ["packages/web", "packages/api"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var nodeApp = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path)
            .WithPnpm();

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        var expected = """
            FROM node:22-alpine AS build

            WORKDIR /app
            RUN corepack enable pnpm
            COPY package.json ./package.json
            COPY pnpm-lock.yaml ./pnpm-lock.yaml
            COPY pnpm-workspace.yaml ./pnpm-workspace.yaml
            COPY packages/api/package.json ./packages/api/package.json
            COPY packages/web/package.json ./packages/web/package.json
            RUN --mount=type=cache,target=/pnpm/store pnpm install --frozen-lockfile
            COPY . .

            FROM node:22-alpine AS runtime

            WORKDIR /app/packages/web
            COPY --from=build /app /app

            ENV NODE_ENV=production

            USER node

            ENTRYPOINT ["node","app.js"]

            """.Replace("\r\n", "\n");

        Assert.Equal(expected, dockerfile);
    }

    [Fact]
    public async Task AddNodeApp_WithWorkspaceRoot_YarnWorkspace_GeneratesWorkspaceDockerfile()
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
            .WithWorkspaceRoot(tempDir.Path)
            .WithYarn();

        await ManifestUtils.GetManifest(nodeApp.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "web");

        var expected = """
            FROM node:22-alpine AS build

            WORKDIR /app
            COPY package.json ./package.json
            COPY yarn.lock ./yarn.lock
            COPY packages/web/package.json ./packages/web/package.json
            RUN --mount=type=cache,target=/root/.cache/yarn yarn install --frozen-lockfile
            COPY . .

            FROM node:22-alpine AS runtime

            WORKDIR /app/packages/web
            COPY --from=build /app /app

            ENV NODE_ENV=production

            USER node

            ENTRYPOINT ["node","app.js"]

            """.Replace("\r\n", "\n");

        Assert.Equal(expected, dockerfile);
    }

    [Fact]
    public async Task AddNodeApp_WithWorkspaceRoot_BunWorkspace_GeneratesWorkspaceDockerfile()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web"]);
        File.WriteAllText(Path.Combine(tempDir.Path, "bun.lockb"), "");
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

        Assert.Contains("FROM oven/bun", dockerfile);
        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY package.json ./package.json", dockerfile);
        Assert.Contains("COPY bun.lockb ./bun.lockb", dockerfile);
        Assert.Contains("COPY packages/web/package.json ./packages/web/package.json", dockerfile);
        Assert.Contains("bun install --frozen-lockfile", dockerfile);
        Assert.Contains("WORKDIR /app/packages/web", dockerfile);
    }

    [Fact]
    public async Task AddJavaScriptApp_WithWorkspaceRoot_PublishAsNodeServer_PrefixesOutputPath()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/api"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "api");
        WriteAppPackageJson(appDir, "@example/api", scripts: """{"build":"echo build","start":"node dist/index.js"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path)
            .WithResourceCleanUp(true);

        var app = builder.AddJavaScriptApp("api", appDir, runScriptName: "start")
            .WithWorkspaceRoot(tempDir.Path)
            .PublishAsNodeServer(entryPoint: "dist/index.js", outputPath: "dist");

        await ManifestUtils.GetManifest(app.Resource, tempDir.Path);

        var dockerfile = ReadDockerfile(tempDir.Path, "api");

        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY package.json ./package.json", dockerfile);
        Assert.Contains("COPY packages/api/package.json ./packages/api/package.json", dockerfile);
        // Build/run uses workspace filter command
        Assert.Contains("npm run build --workspace=@example/api", dockerfile);
        // Runtime stage scoped to the app's directory
        Assert.Contains("COPY --from=build /app/packages/api/dist /app/packages/api/dist", dockerfile);
        Assert.Contains("ENTRYPOINT [\"node\",\"packages/api/dist/index.js\"]", dockerfile);
    }

    [Fact]
    public void WithWorkspaceRoot_NonExistentRoot_Throws()
    {
        using var tempDir = new TestTempDirectory();
        var appDir = Path.Combine(tempDir.Path, "app");
        Directory.CreateDirectory(appDir);
        WriteAppPackageJson(appDir, "@example/app");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("app", appDir, "app.js")
                .WithWorkspaceRoot(Path.Combine(tempDir.Path, "does-not-exist")));

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void WithWorkspaceRoot_AppNotDescendantOfRoot_Throws()
    {
        using var rootDir = new TestTempDirectory();
        using var appOutsideDir = new TestTempDirectory();
        WriteNpmWorkspace(rootDir.Path, ["packages/*"]);
        WriteAppPackageJson(appOutsideDir.Path, "@example/app");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("app", appOutsideDir.Path, "app.js")
                .WithWorkspaceRoot(rootDir.Path));

        Assert.Contains("not a descendant", ex.Message);
    }

    [Fact]
    public void WithWorkspaceRoot_AppMissingPackageName_Throws()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/web"]);
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}"); // no "name"

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("web", appDir, "app.js")
                .WithWorkspaceRoot(tempDir.Path));

        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithWorkspaceRoot_AppNotMemberOfWorkspace_Throws()
    {
        using var tempDir = new TestTempDirectory();
        WriteNpmWorkspace(tempDir.Path, ["packages/api"]);
        var appDir = Path.Combine(tempDir.Path, "apps", "web"); // outside any workspace pattern
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("web", appDir, "app.js")
                .WithWorkspaceRoot(tempDir.Path));

        Assert.Contains("not a declared workspace member", ex.Message);
    }

    [Fact]
    public void WithWorkspaceRoot_NegatedPnpmPattern_Throws()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-workspace.yaml"),
            "packages:\n  - 'packages/*'\n  - '!packages/excluded'\n");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{\"name\":\"root\",\"private\":true}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("web", appDir, "app.js")
                .WithWorkspaceRoot(tempDir.Path));

        Assert.Contains("Negated workspace pattern", ex.Message);
    }

    [Fact]
    public void WithWorkspaceRoot_RecursiveWorkspacePattern_Throws()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            "{\"name\":\"root\",\"private\":true,\"workspaces\":[\"**/packages/*\"]}");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("web", appDir, "app.js")
                .WithWorkspaceRoot(tempDir.Path));

        Assert.Contains("Recursive workspace pattern", ex.Message);
    }

    [Fact]
    public void WithWorkspaceRoot_NoLockfile_Throws()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            "{\"name\":\"root\",\"private\":true,\"workspaces\":[\"packages/*\"]}");
        // no lockfile
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("web", appDir, "app.js")
                .WithWorkspaceRoot(tempDir.Path));

        Assert.Contains("lockfile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithWorkspaceRoot_RootHasNoWorkspaceField_Throws()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            "{\"name\":\"single\",\"private\":true}");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddNodeApp("web", appDir, "app.js")
                .WithWorkspaceRoot(tempDir.Path));

        Assert.Contains("workspace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteNpmWorkspace(string rootPath, string[] patterns)
    {
        Directory.CreateDirectory(rootPath);
        var patternsJson = string.Join(",", patterns.Select(p => "\"" + p + "\""));
        File.WriteAllText(
            Path.Combine(rootPath, "package.json"),
            $"{{\"name\":\"workspace-root\",\"private\":true,\"workspaces\":[{patternsJson}]}}");
        File.WriteAllText(Path.Combine(rootPath, "package-lock.json"), "{}");

        // Create stub package.json files for each declared workspace member so the workspace
        // expander includes them in the manifest layer. Tests that need a richer member
        // package.json (e.g. with scripts) can overwrite the file afterwards via WriteAppPackageJson.
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
