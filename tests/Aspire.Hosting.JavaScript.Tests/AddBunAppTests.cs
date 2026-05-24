// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddBunAppTests
{
    [Fact]
    public async Task VerifyManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var workingDirectory = AppContext.BaseDirectory;
        var bunApp = builder.AddBunApp("bunapp", workingDirectory, "server.ts")
            .WithHttpEndpoint(port: 5033, env: "PORT");
        var manifest = await ManifestUtils.GetManifest(bunApp.Resource);

        var expectedManifest = $$"""
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "bun",
              "args": [
                "server.ts"
              ],
              "env": {
                "NODE_ENV": "{{builder.Environment.EnvironmentName.ToLowerInvariant()}}",
                "PORT": "{bunapp.bindings.http.targetPort}"
              },
              "bindings": {
                "http": {
                  "scheme": "http",
                  "protocol": "tcp",
                  "transport": "http",
                  "port": 5033,
                  "targetPort": 8000
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyDockerfile(bool includePackageJson)
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);

        if (includePackageJson)
        {
            File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");
        }

        var bunApp = builder.AddBunApp("js", appDir, "server.ts");

        await ManifestUtils.GetManifest(bunApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);
        var expectedDockerfile = includePackageJson ?
            """
            FROM oven/bun:1 AS build

            WORKDIR /app
            COPY package.json ./
            RUN --mount=type=cache,target=/root/.bun/install/cache bun install
            COPY . .

            FROM oven/bun:1 AS prod-deps

            WORKDIR /app
            COPY package.json ./
            RUN --mount=type=cache,target=/root/.bun/install/cache bun install --production

            FROM oven/bun:1 AS runtime

            WORKDIR /app
            COPY --from=prod-deps /app/node_modules ./node_modules
            COPY . .

            ENV NODE_ENV=production

            USER bun

            ENTRYPOINT ["bun","server.ts"]

            """.Replace("\r\n", "\n") :
            """
            FROM oven/bun:1 AS build

            WORKDIR /app
            COPY . .

            FROM oven/bun:1 AS runtime

            WORKDIR /app
            COPY --from=build /app /app

            ENV NODE_ENV=production

            USER bun

            ENTRYPOINT ["bun","server.ts"]

            """.Replace("\r\n", "\n");
        Assert.Equal(expectedDockerfile, dockerfileContents);

        var dockerBuildAnnotation = bunApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        Assert.Empty(bunApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>());
    }

    [Fact]
    public async Task VerifyDockerfileWithCustomBaseImage()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        var customBuildImage = "oven/bun:1.1-debian";
        var customRuntimeImage = "oven/bun:1.1-distroless";
        var bunApp = builder.AddBunApp("js", appDir, "server.ts")
            .WithDockerfileBaseImage(customBuildImage, customRuntimeImage);

        await ManifestUtils.GetManifest(bunApp.Resource, tempDir.Path);

        var dockerfileContents = File.ReadAllText(Path.Combine(tempDir.Path, "js.Dockerfile"));
        Assert.Contains($"FROM {customBuildImage}", dockerfileContents);
        Assert.Contains($"FROM {customRuntimeImage}", dockerfileContents);
    }

    [Fact]
    public async Task VerifyDockerfileEmitsPerDockerfileDockerignore()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        var bunApp = builder.AddBunApp("js", appDir, "server.ts");

        await ManifestUtils.GetManifest(bunApp.Resource, tempDir.Path);

        // The default .dockerignore should be emitted alongside the published Dockerfile using
        // BuildKit's per-Dockerfile convention (<dockerfile-name>.dockerignore), not into the
        // user's source tree.
        var perDockerfileIgnorePath = Path.Combine(tempDir.Path, "js.Dockerfile.dockerignore");
        Assert.True(File.Exists(perDockerfileIgnorePath), $"Expected per-Dockerfile dockerignore at {perDockerfileIgnorePath}");
        var ignoreContents = File.ReadAllText(perDockerfileIgnorePath);
        Assert.Contains("node_modules", ignoreContents);
        Assert.Contains(".aspire", ignoreContents);

        // The user's source tree must not be polluted with a generated .dockerignore.
        Assert.False(File.Exists(Path.Combine(appDir, ".dockerignore")), "Aspire should not write a .dockerignore into the user's source tree.");

        // The annotation should carry the default content so it can be inspected/overridden by users.
        var dockerBuildAnnotation = bunApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.NotNull(dockerBuildAnnotation.BuildContextIgnoreContent);
        Assert.Contains("node_modules", dockerBuildAnnotation.BuildContextIgnoreContent!);
    }

    [Fact]
    public async Task VerifyDockerfileSkipsDockerignoreWhenUserAuthoredOneExists()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        // User has authored their own .dockerignore at the context root. BuildKit gives
        // per-Dockerfile ignore files precedence, so Aspire must skip emitting its sibling to
        // honor the user's file.
        var userIgnorePath = Path.Combine(appDir, ".dockerignore");
        var userIgnoreContents = "# user-authored\nsecrets/\n";
        File.WriteAllText(userIgnorePath, userIgnoreContents);

        var bunApp = builder.AddBunApp("js", appDir, "server.ts");

        await ManifestUtils.GetManifest(bunApp.Resource, tempDir.Path);

        var perDockerfileIgnorePath = Path.Combine(tempDir.Path, "js.Dockerfile.dockerignore");
        Assert.False(File.Exists(perDockerfileIgnorePath), "Aspire should not shadow a user-authored context-root .dockerignore.");

        // User file is untouched.
        Assert.Equal(userIgnoreContents, File.ReadAllText(userIgnorePath));
    }

    [Fact]
    public void AddBunApp_DoesNotAddBunPackageManagerWhenNoPackageJson()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "server.ts"), "console.log('hi');");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddBunApp("bunapp", tempDir.Path, "server.ts");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var bunResource = Assert.Single(appModel.Resources.OfType<BunAppResource>());

        // No package.json: don't auto-configure Bun as a package manager and don't add an installer.
        Assert.False(bunResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out _));
        Assert.False(bunResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out _));
        Assert.Empty(appModel.Resources.OfType<JavaScriptInstallerResource>());
    }

    [Fact]
    public void AddBunApp_AddsBunPackageManagerWhenPackageJsonExists()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{}");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddBunApp("bunapp", tempDir.Path, "server.ts");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var bunResource = Assert.Single(appModel.Resources.OfType<BunAppResource>());

        Assert.True(bunResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("bun", packageManager.ExecutableName);
        Assert.Equal("run", packageManager.ScriptCommand);

        Assert.True(bunResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.NotNull(installerResource);
    }

    [Fact]
    public async Task WithRunScript_SetsCustomRunCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddBunApp("bunapp", ".", "server.ts")
            .WithBun()
            .WithRunScript("start", ["--my-arg1"]);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var bunResource = Assert.Single(appModel.Resources.OfType<BunAppResource>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(bunResource);

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("start", arg),
            arg => Assert.Equal("--my-arg1", arg));
    }

    [Fact]
    public void AddBunApp_UsesBunCommand()
    {
        using var tempDir = new TestTempDirectory();

        var builder = DistributedApplication.CreateBuilder();
        var bunApp = builder.AddBunApp("bunapp", tempDir.Path, "server.ts");

        Assert.Equal("bun", bunApp.Resource.Command);
    }

    [Fact]
    public void AddBunApp_ThrowsForNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            JavaScriptHostingExtensions.AddBunApp(null!, "bunapp", ".", "server.ts"));
    }

    [Fact]
    public void AddBunApp_ThrowsForEmptyName()
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddBunApp("", ".", "server.ts"));
    }

    [Fact]
    public void AddBunApp_ThrowsForEmptyScriptPath()
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddBunApp("bunapp", ".", ""));
    }

    [Fact]
    public async Task AddBunApp_ConfiguresCertificateTrustForAppendScope()
    {
        var builder = DistributedApplication.CreateBuilder();
        var bunApp = builder.AddBunApp("bunapp", ".", "server.ts");

        Assert.True(bunApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        var envVars = new Dictionary<string, object>();
        var bundle = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.crt");
        var dirs = ReferenceExpression.Create($"/etc/ssl/aspire/certs");
        var ctx = new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            Resource = bunApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = bundle,
            CertificateDirectoriesPath = dirs,
            Scope = CertificateTrustScope.Append,
            CancellationToken = default,
        };

        await annotation.Callback(ctx);

        // Bun 1.3+ honors NODE_EXTRA_CA_CERTS for both fetch() and node:https/node:tls
        // so the Append scope simply points Bun at the Aspire-provided bundle.
        Assert.Same(bundle, envVars["NODE_EXTRA_CA_CERTS"]);
    }

    [Fact]
    public async Task AddBunApp_ConfiguresCertificateTrustForOverrideScope()
    {
        var builder = DistributedApplication.CreateBuilder();
        var bunApp = builder.AddBunApp("bunapp", ".", "server.ts");

        Assert.True(bunApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        var envVars = new Dictionary<string, object>();
        var ctx = new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            Resource = bunApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.crt"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/etc/ssl/aspire/certs"),
            Scope = CertificateTrustScope.Override,
            CancellationToken = default,
        };

        await annotation.Callback(ctx);

        // Override/System scopes route TLS verification through the OS trust store via the
        // --use-openssl-ca flag, which Bun reads from NODE_OPTIONS for Node compatibility.
        Assert.Equal("--use-openssl-ca", envVars["NODE_OPTIONS"]);
    }
}
