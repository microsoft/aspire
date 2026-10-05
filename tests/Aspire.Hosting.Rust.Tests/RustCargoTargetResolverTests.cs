// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Rust.Tests;

public class RustCargoTargetResolverTests
{
    [Fact]
    public void ResolvesTheSingleBinTargetOfADefaultPackage()
    {
        var target = Resolve(CargoMetadataFactory.SinglePackage("my-service"), new RustCargoOptionsAnnotation());

        Assert.Equal("my-service", target.Name);
        Assert.Equal("release/my-service", target.RelativePathWithoutTarget);
    }

    [Fact]
    public void BinTargetNamesKeepHyphensVerbatim()
    {
        // Library target names have hyphens replaced with underscores; binary target names do not, so the
        // COPY path must use the name exactly as cargo reports it.
        var target = Resolve(CargoMetadataFactory.SinglePackage("aspire-sample-rust-app"), new RustCargoOptionsAnnotation());

        Assert.Equal("aspire-sample-rust-app", target.Name);
    }

    [Fact]
    public void WithCargoBinTargetSelectsTheBinary()
    {
        var metadata = CargoMetadataFactory.SinglePackage("my-service", extraBins: ["worker"]);

        var target = Resolve(metadata, new RustCargoOptionsAnnotation { BinTarget = "worker" });

        Assert.Equal("worker", target.Name);
        Assert.Equal("release/worker", target.RelativePathWithoutTarget);
    }

    [Fact]
    public void WithCargoExampleSelectsTheExampleAndItsOwnDirectory()
    {
        // Examples are written to target/<profile>/examples/ rather than alongside binaries, and cargo does
        // not report them as bin targets, so the name is taken from the option rather than the metadata.
        var target = Resolve(CargoMetadataFactory.SinglePackage("my-service"), new RustCargoOptionsAnnotation { Example = "demo" });

        Assert.Equal("demo", target.Name);
        Assert.Equal("release/examples/demo", target.RelativePathWithoutTarget);
    }

    [Fact]
    public void WithCargoPackageSelectsTheWorkspaceMember()
    {
        var metadata = CargoMetadataFactory.Workspace(
            new CargoPackageSpec("api", ["api"]),
            new CargoPackageSpec("worker", ["worker"]));

        var target = Resolve(metadata, new RustCargoOptionsAnnotation { Package = "worker" });

        Assert.Equal("worker", target.Name);
    }

    [Fact]
    public void DefaultRunWinsOverMultipleBinTargets()
    {
        // `cargo run` honours default-run, so publish must produce the same binary rather than reporting the
        // package as ambiguous.
        var metadata = CargoMetadataFactory.SinglePackage("my-service", defaultRun: "server", extraBins: ["server", "worker"]);

        var target = Resolve(metadata, new RustCargoOptionsAnnotation());

        Assert.Equal("server", target.Name);
    }

    [Fact]
    public void ExplicitBinTargetWinsOverDefaultRun()
    {
        var metadata = CargoMetadataFactory.SinglePackage("my-service", defaultRun: "server", extraBins: ["server", "worker"]);

        var target = Resolve(metadata, new RustCargoOptionsAnnotation { BinTarget = "worker" });

        Assert.Equal("worker", target.Name);
    }

    [Theory]
    [InlineData(null, null, "release/my-service")]
    [InlineData(null, true, "release/my-service")]
    [InlineData(null, false, "debug/my-service")]
    [InlineData("release", null, "release/my-service")]
    [InlineData("dev", null, "debug/my-service")]
    [InlineData("test", null, "debug/my-service")]
    [InlineData("bench", null, "release/my-service")]
    [InlineData("dist", null, "dist/my-service")]
    public void PublishProfileDeterminesTheOutputDirectory(string? profile, bool? releaseBuild, string expectedPath)
    {
        var options = new RustCargoOptionsAnnotation { Profile = profile, ReleaseBuild = releaseBuild };

        var target = Resolve(CargoMetadataFactory.SinglePackage("my-service"), options);

        Assert.Equal(expectedPath, target.RelativePathWithoutTarget);
    }

    [Theory]
    [InlineData(null, null, "debug/my-service")]
    [InlineData(null, true, "release/my-service")]
    [InlineData("dev", null, "debug/my-service")]
    [InlineData("dist", null, "dist/my-service")]
    public void DebugProfileDefaultsToDebugUnlikePublish(string? profile, bool? releaseBuild, string expectedPath)
    {
        // A debug build uses cargo's own default (dev) so it reuses the artifacts a plain `cargo run`
        // already produced, while publishing opts into an optimized build.
        var options = new RustCargoOptionsAnnotation { Profile = profile, ReleaseBuild = releaseBuild };

        var target = RustCargoTargetResolver.Resolve(
            CargoMetadata.Parse(CargoMetadataFactory.SinglePackage("my-service")),
            options,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            "api");

        Assert.Equal(expectedPath, target.RelativePathWithoutTarget);
    }

    [Fact]
    public void TargetAddsATripleDirectoryToThePath()
    {
        var options = new RustCargoOptionsAnnotation { Target = "aarch64-unknown-linux-musl" };

        var target = Resolve(CargoMetadataFactory.SinglePackage("my-service"), options);

        // The triple is part of cargo's output path but deliberately absent from the container search path,
        // which globs the segment because a target can also be selected without the app host seeing it.
        var expected = Path.Combine("/crates/target", "aarch64-unknown-linux-musl", "release", OperatingSystem.IsWindows() ? "my-service.exe" : "my-service");
        Assert.Equal(expected, target.GetExecutablePath("/crates/target"));
        Assert.Equal("release/my-service", target.RelativePathWithoutTarget);
    }

    [Fact]
    public void ExecutablePathIsRootedAtTheTargetDirectoryCargoReported()
    {
        // cargo reports target_directory as an absolute path, so CARGO_TARGET_DIR, build.target-dir and a
        // workspace member sharing the workspace root's target directory all come out correct without the
        // app host having to guess.
        var target = Resolve(CargoMetadataFactory.SinglePackage("my-service"), new RustCargoOptionsAnnotation());

        var expected = Path.Combine("/crates/target", "release", OperatingSystem.IsWindows() ? "my-service.exe" : "my-service");

        Assert.Equal(expected, target.GetExecutablePath("/crates/target"));
    }

    [Fact]
    public void MultipleBinTargetsWithoutSelectionFail()
    {
        // `cargo run` can still succeed here (with a raw --bin that Aspire deliberately does not interpret),
        // so this is one of the few cases that has to be reported rather than passed through.
        var metadata = CargoMetadataFactory.SinglePackage("my-service", extraBins: ["worker"]);

        var exception = Assert.Throws<DistributedApplicationException>(() => Resolve(metadata, new RustCargoOptionsAnnotation()));

        Assert.Equal(
            "Unable to work out which binary the Rust app 'api' produces: the package 'my-service' declares 2 binary targets. " +
            "Call WithCargoBinTarget(\"<name>\") to select one.",
            exception.Message);
    }

    [Fact]
    public void MultipleWorkspaceDefaultMembersWithoutSelectionFail()
    {
        var metadata = CargoMetadataFactory.Workspace(
            new CargoPackageSpec("api", ["api"]),
            new CargoPackageSpec("worker", ["worker"]));

        var exception = Assert.Throws<DistributedApplicationException>(() => Resolve(metadata, new RustCargoOptionsAnnotation()));

        Assert.Equal(
            "Unable to work out which binary the Rust app 'api' produces: 'cargo metadata' reported 2 default workspace members " +
            "with a binary target. Call WithCargoPackage(\"<name>\") to select one. Available packages: 'api', 'worker'.",
            exception.Message);
    }

    [Fact]
    public void AnUnknownPackageIsReportedWithTheAvailableNames()
    {
        // Resolution happens before any build, so a typo would otherwise surface as LINQ's
        // "Sequence contains no matching element" with nothing tying it back to the AppHost.
        var metadata = CargoMetadataFactory.Workspace(
            new CargoPackageSpec("api", ["api"]),
            new CargoPackageSpec("worker", ["worker"]));

        var exception = Assert.Throws<DistributedApplicationException>(
            () => Resolve(metadata, new RustCargoOptionsAnnotation { Package = "wroker" }));

        Assert.Equal(
            "The Rust app 'api' requested the cargo package 'wroker' with WithCargoPackage, but 'cargo metadata' reported no such " +
            "package. Available packages: 'api', 'worker'.",
            exception.Message);
    }

    [Fact]
    public void APackageWithNoBinaryIsReportedAsSuch()
    {
        // Suggesting WithCargoBinTarget here would send the user looking for a target that cannot exist.
        var metadata = CargoMetadataFactory.Workspace(new CargoPackageSpec("shared", []));

        var exception = Assert.Throws<DistributedApplicationException>(() => Resolve(metadata, new RustCargoOptionsAnnotation()));

        Assert.Equal(
            "Unable to work out which binary the Rust app 'api' produces: the package 'shared' declares no binary targets. " +
            "Point the app directory at a package with a binary, or select one with WithCargoPackage(\"<name>\").",
            exception.Message);
    }

    [Fact]
    public void ALibraryCrateBesideAnAppCrateIsNotAmbiguous()
    {
        // `cargo run` picks the workspace's only runnable member, so the very common "app crate plus shared
        // library crate" workspace must resolve without the caller naming a package.
        var metadata = CargoMetadataFactory.Workspace(
            new CargoPackageSpec("api", ["api"]),
            new CargoPackageSpec("shared", []));

        var target = Resolve(metadata, new RustCargoOptionsAnnotation());

        Assert.Equal("api", target.Name);
    }

    [Fact]
    public void WorkspaceDefaultMembersNarrowTheAmbiguity()
    {
        // [workspace] default-members = ["api"] makes a bare `cargo run` unambiguous even though the
        // workspace has several members.
        var metadata = CargoMetadataFactory.Workspace(
            [new CargoPackageSpec("api", ["api"]), new CargoPackageSpec("worker", ["worker"])],
            defaultMembers: ["api"]);

        var target = Resolve(metadata, new RustCargoOptionsAnnotation());

        Assert.Equal("api", target.Name);
    }

    [Fact]
    public void ABinTargetCargoDoesNotReportIsPassedThrough()
    {
        // Resolution runs after the app already ran, so cargo has already had its say on whether the
        // selection is valid. Re-validating here would only turn a working app into a publish-time failure
        // when metadata and the selection disagree for a reason cargo accepts.
        var metadata = CargoMetadataFactory.SinglePackage("my-service");

        var target = Resolve(metadata, new RustCargoOptionsAnnotation { BinTarget = "worker" });

        Assert.Equal("worker", target.Name);
    }

    private static RustCargoTarget Resolve(string metadataJson, RustCargoOptionsAnnotation options)
        => RustCargoTargetResolver.Resolve(
            CargoMetadata.Parse(metadataJson),
            options,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            "api");
}
