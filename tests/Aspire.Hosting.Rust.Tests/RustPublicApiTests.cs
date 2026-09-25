// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Rust.Tests;

public class RustPublicApiTests
{
    [Fact]
    public void CargoArgsCallbackAnnotationIsAnImplementationDetail()
    {
        var annotationType = typeof(RustAppResource).Assembly.GetType(
            "Aspire.Hosting.Rust.RustCargoArgsCallbackAnnotation",
            throwOnError: true)!;

        Assert.False(annotationType.IsPublic);
    }

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
    public async Task WithCargoFeaturesAccumulatesAcrossCalls()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoFeatures("tokio")
            .WithCargoFeatures("serde", "tracing");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "--features", "tokio,serde,tracing", "--"], args);
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
    public async Task WithCargoArgsCallbackReceivesTheRustResourceInRunMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        var observed = new List<RustAppResource>();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoArgs(context => observed.Add(context.Resource));

        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.NotEmpty(observed);
        Assert.All(observed, resource => Assert.Same(app.Resource, resource));
    }

    [Fact]
    public async Task WithCargoArgsAsyncCallbackReceivesTheRustResourceInRunMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        var observed = new List<RustAppResource>();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithCargoArgs(context =>
            {
                observed.Add(context.Resource);
                return Task.CompletedTask;
            });

        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.NotEmpty(observed);
        Assert.All(observed, resource => Assert.Same(app.Resource, resource));
    }

    [Fact]
    public void RustCargoArgsCallbackContextShouldThrowWhenResourceIsNull()
    {
        RustAppResource resource = null!;

        var action = () => new RustCargoArgsCallbackContext(resource, []);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(resource), exception.ParamName);
    }

    [Fact]
    public void RustCargoArgsCallbackContextShouldThrowWhenArgsIsNull()
    {
        IList<string> args = null!;

        var action = () => new RustCargoArgsCallbackContext(new RustAppResource("api", "/src/rust-app"), args);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(args), exception.ParamName);
    }

    [Fact]
    public void RustCargoArgsCallbackContextCarriesTheSuppliedCancellationToken()
    {
        using var cts = new CancellationTokenSource();

        var context = new RustCargoArgsCallbackContext(new RustAppResource("api", "/src/rust-app"), [], cts.Token);

        Assert.Equal(cts.Token, context.CancellationToken);
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

        var launchConfig = await InvokeLaunchConfigurationProducerAsync(builder, app.Resource);

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

        var launchConfig = await InvokeLaunchConfigurationProducerAsync(builder, app.Resource);

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

        var launchConfig = await InvokeLaunchConfigurationProducerAsync(builder, app.Resource);

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

        var launchConfig = await InvokeLaunchConfigurationProducerAsync(builder, app.Resource);

        var cargo = Assert.IsType<RustCargoLaunchTarget>(launchConfig.Cargo);
        var expected = Path.Combine("/app/target", "debug", OperatingSystem.IsWindows() ? "my-service.exe" : "my-service");

        Assert.Equal(expected, cargo.ExecutablePath);
    }

    [Fact]
    public async Task LaunchConfigurationUsesResolvedCargoEnvironmentWithoutReevaluatingCallbacks()
    {
        // cargo metadata is queried with the resource's resolved environment, so CARGO_TARGET_DIR moves the
        // reported target directory, and CARGO_BUILD_TARGET adds the triple directory that cargo does not
        // report at all. Without both, the debugger would be pointed at a file the build never wrote.
        var environmentCallbackInvocations = 0;
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory)
            .WithEnvironment(context =>
            {
                environmentCallbackInvocations++;
                context.EnvironmentVariables["CARGO_TARGET_DIR"] = "/wrong";
                context.EnvironmentVariables["CARGO_BUILD_TARGET"] = "wrong-target";
            });

        var launchConfig = await InvokeLaunchConfigurationProducerAsync(
            builder,
            app.Resource,
            new Dictionary<string, string>
            {
                ["CARGO_TARGET_DIR"] = "/elsewhere",
                ["CARGO_BUILD_TARGET"] = "aarch64-unknown-linux-musl"
            });

        var cargo = Assert.IsType<RustCargoLaunchTarget>(launchConfig.Cargo);
        var expected = Path.Combine(
            Path.GetFullPath("/elsewhere", builder.AppHostDirectory),
            "aarch64-unknown-linux-musl",
            "debug",
            OperatingSystem.IsWindows() ? "my-service.exe" : "my-service");

        Assert.Equal(expected, cargo.ExecutablePath);
        Assert.Equal(0, environmentCallbackInvocations);
    }

    [Fact]
    public async Task LaunchConfigurationThrowsWhenCargoArgumentsHaveNotBeenResolved()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(app.Resource);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(app.Resource, callbackContext));

        Assert.Contains("have not been resolved", exception.Message);
    }

    [Fact]
    [RequiresTools(["cargo"])]
    public async Task LaunchConfigurationReadsCurrentRealCargoMetadataForEachRequest()
    {
        CargoTestHelpers.SkipIfUnavailable();

        using var crate = new TempCrateDirectory();
        crate.Write("Cargo.toml", CreateManifest("first"));
        Directory.CreateDirectory(Path.Combine(crate.Path, "src"));
        crate.Write(Path.Combine("src", "main.rs"), "fn main() {}");

        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", crate.Path);

        await using var built = builder.Build();
        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var first = Assert.IsType<RustLaunchConfiguration>(
            await app.Resource.CreateLaunchConfigurationAsync(
                ExecutableLaunchMode.Debug,
                TestContext.Current.CancellationToken));

        crate.Write("Cargo.toml", CreateManifest("second"));

        var second = Assert.IsType<RustLaunchConfiguration>(
            await app.Resource.CreateLaunchConfigurationAsync(
                ExecutableLaunchMode.Debug,
                TestContext.Current.CancellationToken));

        Assert.EndsWith(
            Path.Combine("debug", OperatingSystem.IsWindows() ? "first.exe" : "first"),
            Assert.IsType<RustCargoLaunchTarget>(first.Cargo).ExecutablePath);
        Assert.EndsWith(
            Path.Combine("debug", OperatingSystem.IsWindows() ? "second.exe" : "second"),
            Assert.IsType<RustCargoLaunchTarget>(second.Cargo).ExecutablePath);

        static string CreateManifest(string packageName) => $"""
            [package]
            name = "{packageName}"
            version = "0.1.0"
            edition = "2021"
            """;
    }

    [Fact]
    public async Task LaunchConfigurationUsesTheResolvedEnvironmentForEachExecutableCreation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var reader = new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service"));
        builder.Services.AddSingleton<ICargoMetadataReader>(reader);

        await using var built = builder.Build();
        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var firstContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            app.Resource,
            environmentVariables: new Dictionary<string, string> { ["CARGO_TARGET_DIR"] = "/first-target" });
        var secondContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            app.Resource,
            environmentVariables: new Dictionary<string, string> { ["CARGO_TARGET_DIR"] = "/second-target" });

        var first = Assert.IsType<RustLaunchConfiguration>(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(app.Resource, firstContext));
        var second = Assert.IsType<RustLaunchConfiguration>(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(app.Resource, secondContext));

        Assert.StartsWith(Path.GetFullPath("/first-target", builder.AppHostDirectory), Assert.IsType<RustCargoLaunchTarget>(first.Cargo).ExecutablePath);
        Assert.StartsWith(Path.GetFullPath("/second-target", builder.AppHostDirectory), Assert.IsType<RustCargoLaunchTarget>(second.Cargo).ExecutablePath);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task LaunchConfigurationQueriesCurrentCargoMetadataOncePerRequest()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var reader = new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("first"))
        {
            MetadataJsonFactory = readCount => CargoMetadataFactory.SinglePackage(readCount == 1 ? "first" : "second")
        };
        builder.Services.AddSingleton<ICargoMetadataReader>(reader);

        await using var built = builder.Build();
        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var first = Assert.IsType<RustLaunchConfiguration>(
            await app.Resource.CreateLaunchConfigurationAsync(
                ExecutableLaunchMode.Debug,
                TestContext.Current.CancellationToken));
        var second = Assert.IsType<RustLaunchConfiguration>(
            await app.Resource.CreateLaunchConfigurationAsync(
                ExecutableLaunchMode.Debug,
                TestContext.Current.CancellationToken));

        Assert.EndsWith(Path.Combine("debug", OperatingSystem.IsWindows() ? "first.exe" : "first"), Assert.IsType<RustCargoLaunchTarget>(first.Cargo).ExecutablePath);
        Assert.EndsWith(Path.Combine("debug", OperatingSystem.IsWindows() ? "second.exe" : "second"), Assert.IsType<RustCargoLaunchTarget>(second.Cargo).ExecutablePath);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task LaunchConfigurationRetriesCargoMetadataOnTheNextRequest()
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.AddRustApp("api", builder.AppHostDirectory);

        var attempts = 0;
        var reader = new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service"))
        {
            OnRead = _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("Transient cargo metadata failure.");
                }

                return Task.CompletedTask;
            }
        };

        builder.Services.AddSingleton<ICargoMetadataReader>(reader);

        await using var built = builder.Build();
        await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.Resource.CreateLaunchConfigurationAsync(
                ExecutableLaunchMode.Debug,
                TestContext.Current.CancellationToken));

        await app.Resource.CreateLaunchConfigurationAsync(
            ExecutableLaunchMode.Debug,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, reader.ReadCount);
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

        using var cts = new CancellationTokenSource();
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(app.Resource, cancellationToken: cts.Token);
        var launchTask = LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(app.Resource, callbackContext);

        await cargoStarted.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launchTask);
    }

    private static async Task<RustLaunchConfiguration> InvokeLaunchConfigurationProducerAsync(
        IDistributedApplicationBuilder builder,
        RustAppResource resource,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        // The debug launch configuration resolves the executable cargo will produce from the crate's
        // metadata, so answer that from a canned document rather than requiring a Rust toolchain. The app
        // has to be built for the resolved reader to be reachable through the execution context.
        builder.Services.AddSingleton<ICargoMetadataReader>(
            new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));

        await using var app = builder.Build();

        // DCP resolves the resource's arguments before it asks for the launch configuration, and the launch
        // configuration reuses them, so evaluate them first.
        await ArgumentEvaluator.GetArgumentListAsync(resource);

        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            resource,
            environmentVariables: environmentVariables);
        var launchConfiguration = await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(
            resource,
            callbackContext);

        return Assert.IsType<RustLaunchConfiguration>(launchConfiguration);
    }
}
