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
    public void CargoFailureDiagnosticRedactsEnvironmentValuesLongestFirst()
    {
        var environment = new Dictionary<string, string>
        {
            ["REGISTRY_TOKEN"] = "token-value",
            ["TOKEN_PREFIX"] = "token",
            ["EMPTY"] = string.Empty
        };

        var diagnostic = CargoMetadataReader.FormatStandardError(
            "registry rejected token-value; wrapper repeated token",
            environment);

        Assert.Equal("registry rejected ***; wrapper repeated ***", diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticRedactsValuesBeforeTrimmingWhitespace()
    {
        var environment = new Dictionary<string, string> { ["REGISTRY_TOKEN"] = " secret " };

        var diagnostic = CargoMetadataReader.FormatStandardError(" secret \n", environment);

        Assert.Equal("***", diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticRedactsBeforeBoundingOutput()
    {
        const string Secret = "secret-value";
        const string TruncatedDiagnosticSuffix = "... (truncated)";
        var environment = new Dictionary<string, string> { ["REGISTRY_TOKEN"] = Secret };
        var standardError = $"{new string('x', CargoMetadataReader.MaximumStandardErrorLength - 6)}{Secret}tail";

        var diagnostic = CargoMetadataReader.FormatStandardError(standardError, environment);

        Assert.Equal(
            $"{new string('x', CargoMetadataReader.MaximumStandardErrorLength - TruncatedDiagnosticSuffix.Length)}{TruncatedDiagnosticSuffix}",
            diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticRedactsSensitiveInheritedEnvironmentValues()
    {
        var resourceEnvironment = new Dictionary<string, string>();
        var inheritedEnvironment = new Dictionary<string, string?>
        {
            ["GITHUB_TOKEN"] = "ambient-secret",
            ["PATH"] = "/usr/local/bin"
        };

        var diagnostic = CargoMetadataReader.FormatStandardError(
            "wrapper echoed ambient-secret but retained /usr/local/bin",
            resourceEnvironment,
            inheritedEnvironment);

        Assert.Equal("wrapper echoed *** but retained /usr/local/bin", diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticRedactsCredentialUrlsRegardlessOfEnvironmentVariableName()
    {
        var inheritedEnvironment = new Dictionary<string, string?>
        {
            ["CARGO_REGISTRIES_PRIVATE_INDEX"] = "https://user:secret@example.com/index"
        };

        var diagnostic = CargoMetadataReader.FormatStandardError(
            "failed to fetch https://user:secret@example.com/index",
            new Dictionary<string, string>(),
            inheritedEnvironment);

        Assert.Equal("failed to fetch ***", diagnostic);
    }

    [Theory]
    [InlineData("DATABASE_URL")]
    [InlineData("REDIS_URL")]
    [InlineData("url")]
    [InlineData("SERVICE_URI")]
    public void CargoFailureDiagnosticRedactsInheritedUrlEnvironmentValues(string variableName)
    {
        // An ambient DATABASE_URL routinely carries a password even when the URL itself has no user info, so
        // the name alone makes it sensitive. This matches the extension-side policy in
        // extension/src/debugger/languages/rust.ts.
        var inheritedEnvironment = new Dictionary<string, string?>
        {
            [variableName] = "postgres://app@db.example.com/orders?sslmode=require"
        };

        var diagnostic = CargoMetadataReader.FormatStandardError(
            "build script printed postgres://app@db.example.com/orders?sslmode=require",
            new Dictionary<string, string>(),
            inheritedEnvironment);

        Assert.Equal("build script printed ***", diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticRetainsInheritedValuesWhoseNamesMerelyEndInUrlLetters()
    {
        // `CURL_CA_BUNDLE` ends in the letters of a URL without naming one. Redacting it would delete a
        // useful path from the diagnostic for no benefit.
        var inheritedEnvironment = new Dictionary<string, string?>
        {
            ["CURL_CA_BUNDLE"] = "/etc/ssl/certs/ca-bundle.crt"
        };

        var diagnostic = CargoMetadataReader.FormatStandardError(
            "failed to verify /etc/ssl/certs/ca-bundle.crt",
            new Dictionary<string, string>(),
            inheritedEnvironment);

        Assert.Equal("failed to verify /etc/ssl/certs/ca-bundle.crt", diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticOmitsOutputWhenSensitiveValueIsTooShortToRedactSafely()
    {
        var environment = new Dictionary<string, string> { ["API_TOKEN"] = "1" };

        var diagnostic = CargoMetadataReader.FormatStandardError("cargo failed with exit code 1", environment);

        Assert.Equal("Cargo stderr omitted because a sensitive environment value was too short to redact safely.", diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticRetainsOutputForUnrelatedShortEnvironmentValues()
    {
        var environment = new Dictionary<string, string>
        {
            ["PORT"] = "80",
            ["DEBUG"] = "1"
        };

        var diagnostic = CargoMetadataReader.FormatStandardError("cargo failed with exit code 1", environment);

        Assert.Equal("cargo failed with exit code 1", diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticReturnsEmptyForEmptyStandardError()
    {
        var environment = new Dictionary<string, string> { ["API_TOKEN"] = "1" };

        var diagnostic = CargoMetadataReader.FormatStandardError(string.Empty, environment);

        Assert.Empty(diagnostic);
    }

    [Fact]
    public void CargoFailureDiagnosticDoesNotSplitSurrogatePairsWhenTruncated()
    {
        const string TruncatedDiagnosticSuffix = "... (truncated)";
        var retainedLength = CargoMetadataReader.MaximumStandardErrorLength - TruncatedDiagnosticSuffix.Length;
        var standardError = $"{new string('x', retainedLength - 1)}😀tail{new string('y', 20)}";

        var diagnostic = CargoMetadataReader.FormatStandardError(
            standardError,
            new Dictionary<string, string>());

        Assert.Equal($"{new string('x', retainedLength - 1)}{TruncatedDiagnosticSuffix}", diagnostic);
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
            TestPathNormalizer.ResolveSymlinks(Path.Combine(crate.Path, "target")),
            TestPathNormalizer.ResolveSymlinks(metadata.TargetDirectory));

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
