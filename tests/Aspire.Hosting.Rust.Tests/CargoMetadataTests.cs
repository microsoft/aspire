// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Rust.Tests;

public class CargoMetadataTests
{
    [Fact]
    public void ParsesPackagesAndBinTargets()
    {
        var metadata = CargoMetadata.Parse(CargoMetadataFactory.SinglePackage("my-service", extraBins: ["worker"]));

        var package = Assert.Single(metadata.Packages);
        Assert.Equal("my-service", package.Name);
        Assert.Equal(["my-service", "worker"], package.BinTargetNames);
        Assert.Null(package.DefaultRun);
    }

    [Fact]
    public void IgnoresNonBinTargets()
    {
        // A target's kind is an array because one target can be several crate types at once. Only targets
        // whose kind array contains "bin" produce an executable.
        const string Json = """
            {
              "packages": [
                {
                  "name": "my-service",
                  "id": "path+file:///app#my-service@0.1.0",
                  "targets": [
                    { "kind": ["lib", "cdylib"], "crate_types": ["lib", "cdylib"], "name": "my_service" },
                    { "kind": ["custom-build"], "crate_types": ["bin"], "name": "build-script-build" },
                    { "kind": ["test"], "crate_types": ["bin"], "name": "integration" },
                    { "kind": ["bin"], "crate_types": ["bin"], "name": "my-service" }
                  ]
                }
              ],
              "workspace_members": ["path+file:///app#my-service@0.1.0"],
              "workspace_default_members": ["path+file:///app#my-service@0.1.0"]
            }
            """;

        var metadata = CargoMetadata.Parse(Json);

        Assert.Equal(["my-service"], Assert.Single(metadata.Packages).BinTargetNames);
    }

    [Fact]
    public void FallsBackToWorkspaceMembersOnOlderCargo()
    {
        // workspace_default_members only exists from cargo 1.71.
        const string Json = """
            {
              "packages": [
                {
                  "name": "my-service",
                  "id": "my-service 0.1.0 (path+file:///app)",
                  "targets": [{ "kind": ["bin"], "crate_types": ["bin"], "name": "my-service" }]
                }
              ],
              "workspace_members": ["my-service 0.1.0 (path+file:///app)"]
            }
            """;

        var metadata = CargoMetadata.Parse(Json);

        Assert.Equal(["my-service 0.1.0 (path+file:///app)"], metadata.DefaultMemberIds);
    }

    [Fact]
    public void ParsesDefaultRun()
    {
        var metadata = CargoMetadata.Parse(CargoMetadataFactory.SinglePackage("my-service", defaultRun: "server", extraBins: ["server"]));

        Assert.Equal("server", Assert.Single(metadata.Packages).DefaultRun);
    }

    [Fact]
    public void CargoIsOnlyEverAskedForMetadata()
    {
        // The container build is the real build. If this vector ever gains a compiling subcommand, publish
        // would build the crate twice: once on the host and once inside the container.
        Assert.Equal(["metadata", "--format-version", "1", "--no-deps"], CargoMetadataReader.BuildArguments(manifestPath: null));

        Assert.Equal(
            ["metadata", "--format-version", "1", "--no-deps", "--manifest-path", "/app/Cargo.toml"],
            CargoMetadataReader.BuildArguments("/app/Cargo.toml"));
    }

    [Fact]
    [RequiresTools(["cargo"])]
    public async Task ReadingMetadataDoesNotCompileTheCrate()
    {
        using var crate = new TempCrateDirectory();
        crate.Write("Cargo.toml", """
            [package]
            name = "metadata-probe"
            version = "0.1.0"
            edition = "2021"
            """);
        Directory.CreateDirectory(Path.Combine(crate.Path, "src"));
        crate.Write(Path.Combine("src", "main.rs"), "fn main() { println!(\"hello\"); }");

        var metadata = await new CargoMetadataReader().ReadAsync(crate.Path, manifestPath: null, "api", environment: ReadOnlyDictionary<string, string>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal("metadata-probe", Assert.Single(metadata.Packages).Name);

        // Resolve against real cargo output, not a hand-written fixture, so the parser stays honest
        // about the shape the installed toolchain actually emits.
        var target = RustCargoTargetResolver.Resolve(
            metadata,
            new RustCargoOptionsAnnotation(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            "api");
        Assert.Equal("metadata-probe", target.Name);
        Assert.Equal("release/metadata-probe", target.RelativePathWithoutTarget);

        // The target directory cargo reports is absolute, which is what lets the debugger point at the
        // executable without reimplementing CARGO_TARGET_DIR / build.target-dir / workspace resolution.
        Assert.Equal(Path.Combine(crate.Path, "target"), metadata.TargetDirectory);

        // Compiling would have created target/. Its absence is the proof that the host did no build work.
        Assert.False(Directory.Exists(Path.Combine(crate.Path, "target")));
    }

    [Fact]
    [RequiresTools(["cargo"])]
    public async Task MissingManifestSurfacesCargosOwnError()
    {
        using var crate = new TempCrateDirectory();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => new CargoMetadataReader().ReadAsync(crate.Path, manifestPath: null, "api", environment: ReadOnlyDictionary<string, string>.Empty, TestContext.Current.CancellationToken));

        Assert.Contains("Cargo.toml", exception.Message);
    }
}
