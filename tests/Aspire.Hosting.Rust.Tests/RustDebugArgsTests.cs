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
    /// Builds a Rust app in run mode with an IDE attached that advertises support for the "rust"
    /// launch configuration, then returns the argument list DCP would hand to the debugged binary.
    /// </summary>
    private static async Task<List<string>> GetDebugArgsAsync(
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

        return await ArgumentEvaluator.GetArgumentListAsync(rust.Resource, app.Services);
    }

    [Fact]
    public async Task DebugArgsExcludeCargoArgumentsWhenUserSuppliesArgs()
    {
        // The debugged binary is launched directly, so it must receive only the program arguments.
        // Leaking "run" (and the cargo/program "--" separator) into that list made the binary fail
        // to parse its own command line.
        var args = await GetDebugArgsAsync(rust => rust.WithArgs("--login", "user", "--output", "out.yaml"));

        Assert.Equal(["--login", "user", "--output", "out.yaml"], args);
    }

    [Fact]
    public async Task DebugArgsExcludeCargoArgumentsWhenUserSuppliesLeadingSeparator()
    {
        // Passing an explicit "--" is a natural thing to try when arguments are being mangled.
        // It must not be mistaken for the separator the integration itself appends.
        var args = await GetDebugArgsAsync(rust => rust.WithArgs("--", "--login", "user"));

        Assert.Equal(["--", "--login", "user"], args);
    }

    [Fact]
    public async Task DebugArgsExcludeCargoBuildOptions()
    {
        var args = await GetDebugArgsAsync(rust => rust
            .WithCargoReleaseBuild()
            .WithCargoFeatures("tls-ring")
            .WithCargoArgs("--bin", "server")
            .WithCargoArgs("--locked")
            .WithArgs("--port", "8080"));

        Assert.Equal(["--port", "8080"], args);
    }

    [Fact]
    public async Task DebugArgsAreEmptyWhenNoProgramArgumentsSupplied()
    {
        var args = await GetDebugArgsAsync(static _ => { });

        Assert.Empty(args);
    }

    [Fact]
    public async Task CargoArgumentsRegisteredAfterProgramArgumentsAreStillApplied()
    {
        // Cargo arguments are held in annotations enumerated when arguments are evaluated rather
        // than when AddRustApp runs, so registration position does not matter for them — unlike the
        // two command-line arg callbacks, which mutate one shared list in sequence. Options-derived
        // arguments such as --release are emitted before explicit WithCargoArgs values because the
        // callback that reads those options is registered by AddRustApp.
        var args = await GetDebugArgsAsync(
            rust => rust.WithArgs("--port", "8080").WithCargoArgs("--locked").WithCargoReleaseBuild(),
            supportedLaunchConfigurations: ["project"]);

        Assert.Equal(["run", "--release", "--locked", "--", "--port", "8080"], args);
    }

    [Fact]
    public async Task CargoArgumentsRegisteredAfterProgramArgumentsAreStillStrippedWhenDebugging()
    {
        var args = await GetDebugArgsAsync(
            rust => rust.WithArgs("--port", "8080").WithCargoArgs("--locked").WithCargoReleaseBuild());

        Assert.Equal(["--port", "8080"], args);
    }

    [Fact]
    public async Task RunArgsRetainCargoArgumentsWhenIdeCannotDebugRust()
    {
        // Without an IDE that supports the "rust" launch configuration the resource runs as
        // `cargo run ... -- <program args>`, so the cargo prefix must survive.
        var args = await GetDebugArgsAsync(
            rust => rust.WithCargoReleaseBuild().WithArgs("--port", "8080"),
            supportedLaunchConfigurations: ["project"]);

        Assert.Equal(["run", "--release", "--", "--port", "8080"], args);
    }
}
