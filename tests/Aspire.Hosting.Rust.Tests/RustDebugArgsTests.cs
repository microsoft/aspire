// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // SupportsDebuggingAnnotation is experimental

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Rust.Tests;

public class RustDebugArgsTests
{
    /// <summary>
    /// Builds a Rust app in run mode with an IDE attached, then returns the resource and its app-model arguments.
    /// </summary>
    private static async Task<(List<string> Args, RustAppResource Resource)> GetArgsAsync(
        Action<IResourceBuilder<RustAppResource>> configure,
        string[]? supportedLaunchConfigurations = null)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = supportedLaunchConfigurations ?? ["rust"]
        };

        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var rust = builder.AddRustApp("api", AppContext.BaseDirectory);
        configure(rust);

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(rust.Resource, app.Services);

        return (args, rust.Resource);
    }

    [Fact]
    public async Task DebugSessionKeepsCargoToolArgumentsInTheAppModel()
    {
        // The `cargo run ... --` prefix stays in the app model and dashboard. DCP withholds it from the
        // debugged binary because the "rust" launch configuration owns the tool invocation.
        var (args, resource) = await GetArgsAsync(
            rust => rust.WithArgs("--login", "user", "--output", "out.yaml"));

        Assert.Equal(["run", "--", "--login", "user", "--output", "out.yaml"], args);

        var debugAnnotation = resource.Annotations.OfType<SupportsDebuggingAnnotation>().Last();
        Assert.True(resource.HasLaunchToolArgsOwnedBy(debugAnnotation));
    }

    [Fact]
    public async Task ProgramArgumentsCanStartWithSeparator()
    {
        // The user's leading separator is a program argument and stays after the separator the integration owns.
        var (args, _) = await GetArgsAsync(rust => rust.WithArgs("--", "--login", "user"));

        Assert.Equal(["run", "--", "--", "--login", "user"], args);
    }

    [Fact]
    public async Task CargoBuildOptionsLeadProgramArguments()
    {
        var (args, _) = await GetArgsAsync(rust => rust
            .WithCargoReleaseBuild()
            .WithCargoFeatures("tls-ring")
            .WithCargoArgs("--bin", "server")
            .WithCargoArgs("--locked")
            .WithArgs("--port", "8080"));

        Assert.Equal(
            ["run", "--features", "tls-ring", "--release", "--bin", "server", "--locked", "--", "--port", "8080"],
            args);
    }

    [Fact]
    public async Task NoProgramArgumentsStillIncludeCargoSeparator()
    {
        var (args, _) = await GetArgsAsync(static _ => { });

        Assert.Equal(["run", "--"], args);
    }

    [Fact]
    public async Task CargoArgumentsRegisteredAfterProgramArgumentsAreStillApplied()
    {
        // Cargo arguments are held in annotations enumerated when arguments are evaluated rather
        // than when AddRustApp runs, so registration position does not matter for them. Options-derived
        // arguments such as --release are emitted before explicit WithCargoArgs values because the
        // callback that reads those options is registered by AddRustApp.
        var (args, _) = await GetArgsAsync(
            rust => rust.WithArgs("--port", "8080").WithCargoArgs("--locked").WithCargoReleaseBuild(),
            supportedLaunchConfigurations: ["project"]);

        Assert.Equal(["run", "--release", "--locked", "--", "--port", "8080"], args);
    }

    [Fact]
    public async Task CargoToolArgumentsRemainOwnedWhenRegisteredAfterProgramArguments()
    {
        var (args, resource) = await GetArgsAsync(
            rust => rust.WithArgs("--port", "8080").WithCargoArgs("--locked").WithCargoReleaseBuild());

        Assert.Equal(["run", "--release", "--locked", "--", "--port", "8080"], args);

        var debugAnnotation = resource.Annotations.OfType<SupportsDebuggingAnnotation>().Last();
        Assert.True(resource.HasLaunchToolArgsOwnedBy(debugAnnotation));
    }

    [Fact]
    public async Task RunArgsRetainCargoArgumentsWhenIdeCannotDebugRust()
    {
        // Without an IDE that supports the "rust" launch configuration the resource runs as
        // `cargo run ... -- <program args>`, so the cargo prefix must survive.
        var (args, _) = await GetArgsAsync(
            rust => rust.WithCargoReleaseBuild().WithArgs("--port", "8080"),
            supportedLaunchConfigurations: ["project"]);

        Assert.Equal(["run", "--release", "--", "--port", "8080"], args);
    }
}
