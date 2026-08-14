// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Aspire.Hosting.Utils;
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
    public void RejectsMetadataFromCargoOlderThan171()
    {
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

        var exception = Assert.Throws<DistributedApplicationException>(() => CargoMetadata.Parse(Json));

        Assert.Equal(
            "Aspire.Hosting.Rust requires Cargo 1.71 or later because this 'cargo metadata' output does not " +
            "include 'workspace_default_members'. Update the Rust toolchain and try again.",
            exception.Message);
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
        public void MetadataReaderAsyncStateMachineDoesNotReferenceDcpProcessTypes()
        {
          // Guest AppHosts discover integration types under restricted reflection. A generated state-machine
          // field that closes over an internal Aspire.Hosting type makes the entire integration assembly fail
          // type discovery before the Rust launch configuration can be produced.
          var readMethod = typeof(CargoMetadataReader).GetMethod(nameof(CargoMetadataReader.ReadAsync));
          var stateMachineType = Assert.IsType<AsyncStateMachineAttribute>(
            Assert.Single(readMethod!.GetCustomAttributes(typeof(AsyncStateMachineAttribute), inherit: false))).StateMachineType;

          Assert.DoesNotContain(
            stateMachineType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public),
            field => field.FieldType.ToString().Contains("Aspire.Hosting.Dcp.Process", StringComparison.Ordinal));
        }

    [Fact]
    [RequiresTools(["cargo"])]
    public async Task ReadingMetadataDoesNotCompileTheCrate()
    {
        CargoTestHelpers.SkipIfUnavailable();

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
        Assert.True(Path.IsPathFullyQualified(metadata.TargetDirectory));
        Assert.Equal(
            PathNormalizer.ResolveSymlinks(Path.Combine(crate.Path, "target")),
            PathNormalizer.ResolveSymlinks(metadata.TargetDirectory));

        // Compiling would have created target/. Its absence is the proof that the host did no build work.
        Assert.False(Directory.Exists(Path.Combine(crate.Path, "target")));
    }

    [Fact]
    [RequiresTools(["cargo"])]
    public async Task MissingManifestSurfacesCargosOwnError()
    {
        CargoTestHelpers.SkipIfUnavailable();

        using var crate = new TempCrateDirectory();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => new CargoMetadataReader().ReadAsync(crate.Path, manifestPath: null, "api", environment: ReadOnlyDictionary<string, string>.Empty, TestContext.Current.CancellationToken));

        Assert.Contains("Cargo.toml", exception.Message);
    }
}
