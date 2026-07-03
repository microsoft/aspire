// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddDenoAppTests
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

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts");

        await ManifestUtils.GetManifest(denoApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "js.Dockerfile");
        var dockerfileContents = File.ReadAllText(dockerfilePath);
        await Verify(dockerfileContents);

        var dockerBuildAnnotation = denoApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.True(dockerBuildAnnotation.HasEntrypoint);

        Assert.Empty(denoApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>());
    }

    [Fact]
    public async Task VerifyDockerfileWithCustomBaseImage()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var appDir = Path.Combine(tempDir.Path, "js");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"), "{}");

        var customBuildImage = "denoland/deno:2.1-alpine";
        var customRuntimeImage = "denoland/deno:2.1-distroless";
        var denoApp = builder.AddDenoApp("js", appDir, "main.ts")
            .WithDockerfileBaseImage(customBuildImage, customRuntimeImage);

        await ManifestUtils.GetManifest(denoApp.Resource, tempDir.Path);

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

        var denoApp = builder.AddDenoApp("js", appDir, "main.ts");

        await ManifestUtils.GetManifest(denoApp.Resource, tempDir.Path);

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
        // Unlike the Bun/Node variants, the Deno ignore intentionally does not list node_modules
        // because Deno caches dependencies under DENO_DIR rather than a project-local folder.
        var dockerBuildAnnotation = denoApp.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        Assert.NotNull(dockerBuildAnnotation.BuildContextIgnoreContent);
        Assert.DoesNotContain("node_modules", dockerBuildAnnotation.BuildContextIgnoreContent!);
    }

    [Fact]
    public void AddDenoApp_DoesNotAddDenoPackageManagerWhenNoManifest()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "main.ts"), "console.log('hi');");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

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
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{}");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

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
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "deno.json"), "{}");

        var builder = DistributedApplication.CreateBuilder();

        builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

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
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "main.ts"), "console.log('hi');");

        var builder = DistributedApplication.CreateBuilder();
        builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

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
    public async Task WithDenoGranularPermissions_EmitInCanonicalOrderWithValues()
    {
        // Configured out of canonical order and across allow/deny to prove deterministic ordering
        // (net, read, write, run, env, sys, ffi; allow before deny) independent of call order.
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowEnv("PORT", "HOME")
            .WithDenoDenyNet("evil.example")
            .WithDenoAllowNet("localhost:8080", "api.internal")
            .WithDenoAllowRead("/etc/app")
            .WithDenoDenyWrite()
            .WithDenoAllowRun("git")
            .WithDenoAllowSys()
            .WithDenoAllowFfi("./native.so"));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--allow-net=localhost:8080,api.internal", a),
            a => Assert.Equal("--deny-net=evil.example", a),
            a => Assert.Equal("--allow-read=/etc/app", a),
            a => Assert.Equal("--deny-write", a),
            a => Assert.Equal("--allow-run=git", a),
            a => Assert.Equal("--allow-env=PORT,HOME", a),
            a => Assert.Equal("--allow-sys", a),
            a => Assert.Equal("--allow-ffi=./native.so", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoAllowAll_True_KeepsDenyFlagsButDropsRedundantAllows()
    {
        // -A subsumes granular allows; a deny flag still narrows it and must be preserved.
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll()
            .WithDenoAllowNet("localhost")
            .WithDenoDenyWrite("/etc"));

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
            .WithDenoNodeModulesDir("auto"));

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
    public async Task WithDenoWatchAndInspect_EmitInRuntimeFlagPosition()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoWatch()
            .WithDenoInspectBrk("127.0.0.1:9229"));

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--watch", a),
            a => Assert.Equal("--inspect-brk=127.0.0.1:9229", a),
            a => Assert.Equal("main.ts", a));
    }

    [Fact]
    public async Task WithDenoWatchHmr_EmitsWatchHmrFlag()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoAllowAll(false)
            .WithDenoWatch(hmr: true)
            .WithDenoInspectWait());

        Assert.Collection(args,
            a => Assert.Equal("run", a),
            a => Assert.Equal("--watch-hmr", a),
            a => Assert.Equal("--inspect-wait", a),
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
            .WithDenoAllowNet("0.0.0.0:8000")
            .WithDenoScriptArgs("--config-arg"), entrypoint: "server.ts");

        Assert.Collection(args,
            a => Assert.Equal("serve", a),
            a => Assert.Equal("--allow-net=0.0.0.0:8000", a),
            a => Assert.Equal("server.ts", a),
            a => Assert.Equal("--config-arg", a));
    }

    [Fact]
    public async Task WithDenoTask_EmitsTaskModeAndIgnoresPermissionFlags()
    {
        var args = await GetDenoArgsAsync(d => d
            .WithDenoTask("dev")
            .WithDenoAllowNet("localhost") // permissions belong to the task; must not be emitted
            .WithDenoConfig("deno.json")   // resolution flags are still valid for `deno task`
            .WithDenoScriptArgs("--flag"));

        Assert.Collection(args,
            a => Assert.Equal("task", a),
            a => Assert.Equal("--config", a),
            a => Assert.Equal("deno.json", a),
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
            .WithDenoAllowNet("0.0.0.0:8000", "db:5432")
            .WithDenoAllowEnv("PORT", "DATABASE_URL")
            .WithDenoAllowRead("./config")
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
    public void AddDenoApp_UsesDenoCommand()
    {
        using var tempDir = new TestTempDirectory();

        var builder = DistributedApplication.CreateBuilder();
        var denoApp = builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

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
            ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
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
        Assert.Same(bundle, envVars["DENO_CERT"]);
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
            ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            Resource = denoApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.crt"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/etc/ssl/aspire/certs"),
            Scope = CertificateTrustScope.Override,
            CancellationToken = default,
        };

        await annotation.Callback(ctx);

        // Override/System scopes route TLS verification through the OS trust store via DENO_TLS_CA_STORE=system.
        Assert.Equal("system", envVars["DENO_TLS_CA_STORE"]);
    }

    [Fact]
    public async Task AddDenoApp_EnablesNativeOpenTelemetry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var denoApp = builder.AddDenoApp("denoapp", ".", "main.ts");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(denoApp.Resource, DistributedApplicationOperation.Run);

        // Deno's built-in OpenTelemetry integration is enabled with a single environment variable.
        Assert.Equal("true", env["OTEL_DENO"]);
    }

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only

    [Fact]
    public void DenoApp_WithVSCodeDebugging_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var denoApp = builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

        var annotation = denoApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("deno", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void DenoApp_WithVSCodeDebugging_DoesNotAddAnnotationInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TestTempDirectory();

        var denoApp = builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

        var annotation = denoApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }

    [Fact]
    public void DenoApp_WithRunScript_AddsSupportsDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var denoApp = builder.AddDenoApp("denoapp", tempDir.Path, "main.ts")
            .WithRunScript("dev");

        var annotation = denoApp.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("deno", annotation.LaunchConfigurationType);
    }

    [Fact]
    public void DenoApp_DirectFile_ProducesDenoRuntimeExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var denoApp = builder.AddDenoApp("denoapp", tempDir.Path, "main.ts");

        var launchConfig = InvokeLaunchConfigurationAnnotator(denoApp.Resource);

        Assert.Equal("deno", launchConfig.Type);
        Assert.Equal("deno", launchConfig.RuntimeExecutable);
        Assert.Equal("direct", launchConfig.LaunchMethod);
        Assert.Equal(Path.GetFullPath("main.ts", tempDir.Path), launchConfig.ScriptPath);
    }

    [Fact]
    public void DenoApp_WithRunScriptAndPackageManager_ProducesDenoRuntimeExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        // AddDenoApp automatically calls WithDeno() when a deno.json exists, which makes the run-script a
        // package-manager invocation (deno task dev).
        File.WriteAllText(Path.Combine(tempDir.Path, "deno.json"), "{}");

        var denoApp = builder.AddDenoApp("denoapp", tempDir.Path, "main.ts")
            .WithRunScript("dev");

        var launchConfig = InvokeLaunchConfigurationAnnotator(denoApp.Resource);

        Assert.Equal("deno", launchConfig.Type);
        Assert.Equal("deno", launchConfig.RuntimeExecutable);
        Assert.Equal("package-manager", launchConfig.LaunchMethod);
    }

    private static JavaScriptLaunchConfiguration InvokeLaunchConfigurationAnnotator(IResource resource)
    {
        Assert.True(resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));

        var exe = Executable.Create("test", "deno");
        supportsDebugging.LaunchConfigurationAnnotator(exe, ExecutableLaunchMode.Debug);

        Assert.True(exe.TryGetAnnotationAsObjectList<JavaScriptLaunchConfiguration>(
            Executable.LaunchConfigurationsAnnotation,
            out var launchConfigs));
        return Assert.Single(launchConfigs);
    }

#pragma warning restore ASPIREEXTENSION001 // Type is for evaluation purposes only
}
