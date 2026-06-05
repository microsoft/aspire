// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
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

        await Verify(manifest.ToString());
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
        await Verify(dockerfileContents);

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

        await Verify(File.ReadAllText(Path.Combine(tempDir.Path, "js.Dockerfile")));
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
        await Verify(ignoreContents);

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

        var bunApp = builder.AddBunApp("js", appDir, "server.ts");

        await ManifestUtils.GetManifest(bunApp.Resource, tempDir.Path);

        var perDockerfileIgnorePath = Path.Combine(tempDir.Path, "js.Dockerfile.dockerignore");
        Assert.True(File.Exists(perDockerfileIgnorePath));

        // User has authored their own .dockerignore at the context root after a previous publish.
        // BuildKit gives per-Dockerfile ignore files precedence, so Aspire must remove the stale
        // generated sibling to honor the user's file on the next publish to the same output path.
        var userIgnorePath = Path.Combine(appDir, ".dockerignore");
        var userIgnoreContents = "# user-authored\nsecrets/\n";
        File.WriteAllText(userIgnorePath, userIgnoreContents);

        await ManifestUtils.GetManifest(bunApp.Resource, tempDir.Path);

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

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only

    [Fact]
    public void BunApp_WithVSCodeDebugging_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var bunApp = builder.AddBunApp("bunapp", tempDir.Path, "server.ts");

        var annotation = bunApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("bun", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void BunApp_WithVSCodeDebugging_DoesNotAddAnnotationInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TestTempDirectory();

        var bunApp = builder.AddBunApp("bunapp", tempDir.Path, "server.ts");

        var annotation = bunApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }

    [Fact]
    public void BunApp_WithRunScript_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var bunApp = builder.AddBunApp("bunapp", tempDir.Path, "server.ts")
            .WithRunScript("dev");

        var annotation = bunApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("bun", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void BunApp_WithPackageJson_HasPackageManagerAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        // AddBunApp automatically calls WithBun() when a package.json exists.
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{}");

        var bunApp = builder.AddBunApp("bunapp", tempDir.Path, "server.ts")
            .WithRunScript("dev");

        Assert.True(bunApp.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pmAnnotation));
        Assert.Equal("bun", pmAnnotation.ExecutableName);
    }

    [Fact]
    public void BunApp_DirectFile_ProducesBunRuntimeExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var bunApp = builder.AddBunApp("bunapp", tempDir.Path, "server.ts");

        var launchConfig = InvokeLaunchConfigurationAnnotator(bunApp.Resource);

        Assert.Equal("bun", launchConfig.Type);
        Assert.Equal("bun", launchConfig.RuntimeExecutable);
        Assert.Equal("direct", launchConfig.LaunchMethod);
        Assert.Equal(Path.GetFullPath("server.ts", tempDir.Path), launchConfig.ScriptPath);
    }

    [Fact]
    public void BunApp_WithRunScriptAndPackageManager_ProducesBunRuntimeExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        // AddBunApp automatically calls WithBun() when a package.json exists, which makes the run-script a
        // package-manager invocation (bun run dev).
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{}");

        var bunApp = builder.AddBunApp("bunapp", tempDir.Path, "server.ts")
            .WithRunScript("dev");

        var launchConfig = InvokeLaunchConfigurationAnnotator(bunApp.Resource);

        Assert.Equal("bun", launchConfig.Type);
        Assert.Equal("bun", launchConfig.RuntimeExecutable);
        Assert.Equal("package-manager", launchConfig.LaunchMethod);
    }

    private static JavaScriptLaunchConfiguration InvokeLaunchConfigurationAnnotator(IResource resource)
    {
        Assert.True(resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));

        var exe = Executable.Create("test", "bun");
        supportsDebugging.LaunchConfigurationAnnotator(exe, ExecutableLaunchMode.Debug);

        Assert.True(exe.TryGetAnnotationAsObjectList<JavaScriptLaunchConfiguration>(
            Executable.LaunchConfigurationsAnnotation,
            out var launchConfigs));
        return Assert.Single(launchConfigs);
    }

#pragma warning restore ASPIREEXTENSION001 // Type is for evaluation purposes only
}
