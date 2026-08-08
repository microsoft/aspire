// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Rust.Tests;

public class RustPublicApiTests
{
    [Fact]
    public void AddRustAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddRustApp("api", "/src/rust-app");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public async Task AddRustAppDefaultArgsAreRunWithSeparator()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--"], args);
    }

    [Fact]
    public async Task AddRustAppWithCargoAndAppArgsPreservesSeparatorOrdering()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoArgs("--release")
            .WithArgs("--port", "8080");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--release", "--", "--port", "8080"], args);
    }

    [Fact]
    public async Task WithCargoFeaturesAndTargetSelectionMapToCargoArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoFeatures("tokio", "serde")
            .WithCargoArgs("--bin", "worker");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--features", "tokio,serde", "--bin", "worker", "--"], args);
    }

    [Fact]
    public async Task WithCargoTargetSelectionMethodsMapToCargoArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoFeatures("tokio")
            .WithCargoBinTarget("worker")
            .WithCargoPackage("services")
            .WithCargoManifestPath("crates/worker/Cargo.toml")
            .WithCargoTarget("aarch64-unknown-linux-musl")
            .WithCargoProfile("dist");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(
            ["run", "--features", "tokio", "--bin", "worker", "--package", "services", "--manifest-path", "crates/worker/Cargo.toml", "--target", "aarch64-unknown-linux-musl", "--profile", "dist", "--"],
            args);
    }

    [Fact]
    public async Task WithCargoExampleMapsToCargoArgs()
    {
        // --bin and --example are mutually exclusive in cargo, so the example gets its own test rather
        // than being folded into the target selection case above.
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoExample("demo");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--example", "demo", "--"], args);
    }

    [Fact]
    public async Task WithCargoProfileWinsOverWithCargoReleaseBuild()
    {
        // Cargo rejects --profile and --release together.
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoReleaseBuild()
            .WithCargoProfile("dist");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--profile", "dist", "--"], args);
    }

    [Fact]
    public async Task WithCargoLockedMapsToCargoArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoLocked();

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--locked", "--"], args);
    }

    [Fact]
    public async Task RunModeDoesNotOptIntoLockedOrRelease()
    {
        // Both default to cargo's own behaviour locally; only publishing turns them on by default, so a
        // `cargo run` that works from the terminal keeps working through the app host.
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--"], args);
    }

    [Fact]
    public void AddRustAppEnablesDebuggingSupport()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        Assert.True(app.Resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out _));
    }

    [Fact]
    public async Task LaunchConfigurationCarriesCargoBuildArguments()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoFeatures("tls-ring")
            .WithCargoReleaseBuild();

        var launchConfig = await InvokeLaunchConfigurationAnnotatorAsync(builder, app.Resource);

        Assert.Equal("rust", launchConfig.Type);
        Assert.Equal(Path.GetFullPath(builder.AppHostDirectory), launchConfig.WorkingDirectory);

        var cargo = Assert.IsType<RustCargoLaunchTarget>(launchConfig.Cargo);
        Assert.Equal(["build", "--features", "tls-ring", "--release"], cargo.Args);
    }

    [Fact]
    public async Task LaunchConfigurationCarriesCargoTargetSelectionArguments()
    {
        // `cargo build` builds every binary target unless the arguments narrow it, so the target
        // selection that makes `cargo run` unambiguous has to reach the debug build too.
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoArgs("--bin", "worker");

        var launchConfig = await InvokeLaunchConfigurationAnnotatorAsync(builder, app.Resource);

        var cargo = Assert.IsType<RustCargoLaunchTarget>(launchConfig.Cargo);
        Assert.Equal(["build", "--bin", "worker"], cargo.Args);
    }

    [Fact]
    public async Task LaunchConfigurationReusesResolvedCargoArgumentsInsteadOfRunningCallbacksAgain()
    {
        // A cargo argument callback may be one-shot or nondeterministic, so building the launch
        // configuration must reuse what argument evaluation already produced rather than re-running it.
        var invocations = 0;

        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoArgs(context =>
            {
                invocations++;
                context.Args.Add($"--config=build.jobs={invocations}");
            });

        var launchConfig = await InvokeLaunchConfigurationAnnotatorAsync(builder, app.Resource);

        Assert.Equal(1, invocations);
        var cargo = Assert.IsType<RustCargoLaunchTarget>(launchConfig.Cargo);
        Assert.Equal(["build", "--config=build.jobs=1"], cargo.Args);
    }

    [Fact]
    public async Task LaunchConfigurationCarriesTheExecutableTheBuildWillProduce()
    {
        // The extension runs a plain `cargo build` and debugs this path, rather than parsing cargo's JSON
        // artifact stream, so the debugged process is the same binary publishing containerizes.
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var launchConfig = await InvokeLaunchConfigurationAnnotatorAsync(builder, app.Resource);

        var cargo = Assert.IsType<RustCargoLaunchTarget>(launchConfig.Cargo);
        var expected = Path.Combine("/app/target", "debug", OperatingSystem.IsWindows() ? "my-service.exe" : "my-service");

        Assert.Equal(expected, cargo.ExecutablePath);
    }

    [Fact]
    public async Task LaunchConfigurationHonoursCargoEnvironmentVariables()
    {
        // cargo metadata is queried with the resource's resolved environment, so CARGO_TARGET_DIR moves the
        // reported target directory, and CARGO_BUILD_TARGET adds the triple directory that cargo does not
        // report at all. Without both, the debugger would be pointed at a file the build never wrote.
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithEnvironment("CARGO_TARGET_DIR", "/elsewhere")
            .WithEnvironment("CARGO_BUILD_TARGET", "aarch64-unknown-linux-musl");

        var launchConfig = await InvokeLaunchConfigurationAnnotatorAsync(builder, app.Resource);

        var cargo = Assert.IsType<RustCargoLaunchTarget>(launchConfig.Cargo);
        var expected = Path.Combine(
            Path.GetFullPath("/elsewhere", builder.AppHostDirectory),
            "aarch64-unknown-linux-musl",
            "debug",
            OperatingSystem.IsWindows() ? "my-service.exe" : "my-service");

        Assert.Equal(expected, cargo.ExecutablePath);
    }

    [Fact]
    public async Task LaunchConfigurationThrowsWhenCargoArgumentsHaveNotBeenResolved()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        Assert.True(app.Resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supportsDebugging.LaunchConfigurationAnnotator(Executable.Create("test", "cargo"), ExecutableLaunchMode.Debug, CancellationToken.None));

        Assert.Contains("have not been resolved", exception.Message);
    }

    [Fact]
    public async Task LaunchConfigurationQueriesCargoMetadataOnlyOncePerResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var reader = new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service"));
        builder.Services.AddSingleton<ICargoMetadataReader>(reader);

        await using var built = builder.Build();
        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.True(app.Resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));

        // Aspire asks for the launch configuration more than once per resource, and cargo metadata is slow
        // enough on a cold machine to matter, so the resolved path has to be reused.
        await supportsDebugging.LaunchConfigurationAnnotator(Executable.Create("test", "cargo"), ExecutableLaunchMode.Debug, CancellationToken.None);
        await supportsDebugging.LaunchConfigurationAnnotator(Executable.Create("test", "cargo"), ExecutableLaunchMode.Debug, CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task LaunchConfigurationCancelsCargoMetadataWhenCancellationIsRequested()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        using var cargoStarted = new SemaphoreSlim(0, 1);
        var reader = new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service"))
        {
            // Stands in for a cold `cargo metadata` that outlives the app host: it only completes when the
            // caller's token is cancelled.
            OnRead = async cancellationToken =>
            {
                cargoStarted.Release();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        };

        builder.Services.AddSingleton<ICargoMetadataReader>(reader);

        await using var built = builder.Build();
        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.True(app.Resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));

        using var cts = new CancellationTokenSource();
        var launchTask = supportsDebugging.LaunchConfigurationAnnotator(Executable.Create("test", "cargo"), ExecutableLaunchMode.Debug, cts.Token);

        await cargoStarted.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launchTask);
    }

    private static async Task<RustLaunchConfiguration> InvokeLaunchConfigurationAnnotatorAsync(IDistributedApplicationBuilder builder, RustAppResource resource)
    {
        Assert.True(resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));

        // The debug launch configuration resolves the executable cargo will produce from the crate's
        // metadata, so answer that from a canned document rather than requiring a Rust toolchain. The app
        // has to be built for the resolved reader to be reachable through the execution context.
        builder.Services.AddSingleton<ICargoMetadataReader>(
            new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));

        await using var app = builder.Build();

        // DCP resolves the resource's arguments before it asks for the launch configuration, and the launch
        // configuration reuses them, so evaluate them first.
        await ArgumentEvaluator.GetArgumentListAsync(resource);

        var exe = Executable.Create("test", "cargo");
        await supportsDebugging.LaunchConfigurationAnnotator(exe, ExecutableLaunchMode.Debug, CancellationToken.None);

        Assert.True(exe.TryGetAnnotationAsObjectList<RustLaunchConfiguration>(
            Executable.LaunchConfigurationsAnnotation,
            out var launchConfigs));

        return Assert.Single(launchConfigs);
    }
}
