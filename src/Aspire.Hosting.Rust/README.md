# Rust hosting integration

Use this integration to model, configure, and orchestrate a Rust application resource in an Aspire solution.

## Getting started

### Prerequisites

The **Rust toolchain** (`cargo` 1.71 or later) must be available on the PATH of the machine running
the AppHost. Install it with [rustup](https://www.rust-lang.org/tools/install).

For VS Code debugging, install the platform's native debugger extension:
[C/C++](https://marketplace.visualstudio.com/items?itemName=ms-vscode.cpptools) on Windows, or
[CodeLLDB](https://marketplace.visualstudio.com/items?itemName=vadimcn.vscode-lldb) on Linux and macOS.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Rust` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Rust
aspire add Aspire.Hosting.Dotnet
```

## Usage example

Then, in the AppHost, add a Rust application resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var rustApi = builder.AddRustApp("api", "../rust-api")
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("frontend", "../Frontend/Frontend.csproj")
    .WithReference(rustApi)
    .WaitFor(rustApi);

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const rustApi = await builder.addRustApp("api", "../rust-api")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

await builder.addNodeApp("frontend", "../frontend", "server.js")
    .withReference(rustApi)
    .waitFor(rustApi);

await builder.build().run();
```

`appDirectory` is cargo's working directory and the publish build context. Cargo discovers the manifest
from that directory by default; use `.WithCargoManifestPath(...)` to select another manifest. For
publishing, the selected manifest and everything it needs must be inside `appDirectory`. Arguments for
your program are passed with `.WithArgs(...)`; arguments for cargo itself are passed with
`.WithCargoArgs(...)`.

### Cargo options

```csharp
builder.AddRustApp("api", "../rust-api")
    .WithCargoBinTarget("worker")
    .WithCargoFeatures("grpc-tonic", "tls-ring")
    .WithCargoArgs("--no-default-features");
```

| Method | Effect |
| --- | --- |
| `WithCargoArgs(params string[] args)` | Appends raw arguments to the cargo command line. Use the methods below to select a target, since debugging and publishing read those to work out which binary cargo produces |
| `WithCargoArgs(Action<RustCargoArgsCallbackContext> callback)` | Computes cargo arguments when the resource starts. The context carries the `RustAppResource` being configured, and an async `Func<RustCargoArgsCallbackContext, Task>` overload is also available |
| `WithCargoReleaseBuild(bool releaseBuild = true)` | Adds `--release`. Publishing adds it by default, so pass `false` to publish an unoptimized image |
| `WithCargoLocked(bool locked = true)` | Adds `--locked`, which fails rather than updating `Cargo.lock`. Publishing adds it by default whenever the crate has a lock file, so pass `false` to opt out |
| `WithCargoFeatures(params string[] features)` | Adds the supplied features to `--features`. Repeated calls accumulate features in call order |
| `WithCargoBinTarget(string binName)` | Adds `--bin` to select one of several `[[bin]]` targets |
| `WithCargoExample(string exampleName)` | Adds `--example` to run an example instead of a binary |
| `WithCargoPackage(string packageName)` | Adds `--package` to select a workspace member |
| `WithCargoTarget(string target)` | Adds `--target`. Generated Dockerfiles map native Linux x86_64, aarch64, 32-bit ARM, and 32-bit x86 targets to Docker platforms |
| `WithCargoManifestPath(string manifestPath)` | Adds `--manifest-path`. Only needed when the manifest is not the one cargo finds from the app directory. Publishing requires a path relative to the app directory so the manifest can be copied into the image |
| `WithCargoProfile(string profileName)` | Adds `--profile`. Takes precedence over `WithCargoReleaseBuild()`, which cargo rejects alongside `--profile` |

### Debugging

Debugging is enabled automatically by `AddRustApp` — use the normal Aspire "Start Debugging" flow in
VS Code.

### Publishing

`aspire publish` and `aspire deploy` build the app into a container. An app that runs should publish
with no extra configuration: if the app directory contains a `Dockerfile` it is used as-is, otherwise
one is generated that compiles the crate inside the container. The container runs as a non-root `app`
user.

Only the app directory is copied into the image, so it has to hold everything the build needs. For a
crate that inherits from a workspace or depends on a sibling by path, point the app directory at the
workspace root and select the crate with `WithCargoPackage("<name>")`.

#### Base images

| Stage | Default |
| --- | --- |
| Build | `docker.io/library/rust:1.97-alpine3.24` (a `rust-toolchain.toml` pin is installed by rustup inside the image) |
| Runtime | `docker.io/library/alpine:3.24` |

Each `WithDockerfileBaseImage` call replaces the previous image configuration. When a target needs a
custom pair, set both images in a single `WithDockerfileBaseImage` call:

```csharp
builder.AddRustApp("api", "../rust-api")
    .WithCargoTarget("armv7-unknown-linux-gnueabihf")
    .WithDockerfileBaseImage(
        buildImage: "example/rust-armv7-build:latest",
        runtimeImage: "example/armv7-runtime:latest");
```

The generated Dockerfile maps `x86_64` to `linux/amd64`, `aarch64` to `linux/arm64`, and 32-bit x86
to `linux/386`. 32-bit ARM targets map to Docker's `linux/arm` platform, which Docker normalizes to
the ARMv7 variant.

The default Rust build image publishes only AMD64 and ARM64 variants, while the default Alpine
runtime image also publishes ARM and 386 variants. Both defaults use musl. 32-bit musl targets need
a custom build image but can keep the default runtime image. Other ABIs require custom build and
runtime images configured together in one call.

A custom image pair does not bypass target-platform validation: non-Linux targets and architectures
without a `ContainerTargetPlatform` mapping still require an authored Dockerfile.

`WithCargoTarget` installs the Rust standard library but does not install a linker or other
cross-compilation tooling, so the selected build image must already support the target. It is also
on you to keep the build image, target ABI, and runtime image compatible.

### Testing with nextest

The experimental `WithNextestTesting` API (`ASPIRETESTING001`) collects final
[nextest JUnit reports](https://nexte.st/docs/machine-readable/junit/) during `aspire test`.
Install [cargo-nextest 0.9.143 or later](https://nexte.st/docs/installation/pre-built-binaries/)
separately; Aspire does not install it or change global Rust configuration.

**C#**

```csharp
#pragma warning disable ASPIRETESTING001
var tests = builder.AddExecutable("rust-tests", "cargo", "../rust-api",
        "nextest", "run", "--workspace", "--features=integration")
    .WithNextestTesting()
    .WithEnvironment("API_URL", rustApi.GetEndpoint("http"))
    .WaitFor(rustApi);
```

**TypeScript**

```typescript
const tests = await builder.addExecutable("rust-tests", "cargo", "../rust-api",
    ["nextest", "run", "--workspace", "--features=integration"])
  .withNextestTesting()
  .withEnvironment("API_URL", await rustApi.getEndpoint("http"))
  .waitFor(rustApi);
```

Use an explicit Cargo executable, not `AddRustApp` (which runs/debugs an application).
Cargo workspace/package selection, features, manifest, build target, working directory,
configuration, and environment remain intact. Use nextest's `--cargo-profile` for a Cargo
build profile; `--profile` selects a runner profile and is reserved during testing.

**Controlled runner configuration:** during testing, Aspire supplies a private `--config-file`
and `aspire` profile instead of loading repository `.config/nextest.toml`. Repository files
are not edited, but their nextest settings, setup scripts, groups, and per-test overrides
are **not applied**. This prevents repository config from redirecting the assigned report
or enabling hidden retries. The adapter disables retries and fail-fast, rejects stress/debug
execution, and explicitly fails when no tests are selected. Ordinary runs keep the original
arguments and repository configuration, without report collection. The resource is run-only.

Nextest runs unit/integration tests, **not doctests**. Stable Cargo compiler JSON is not test
result JSON; this adapter does not use nightly libtest JSON or experimental nextest JSON.
Final JUnit cases, including ignored tests as skipped, contribute to the common summary.
Filtered-out or interrupted/unexecuted tests are not fabricated as passes or skips.
Nonzero tool/build/test exits remain failures even when valid test results are collected.
Missing, malformed, empty, or inconsistent reports fail collection.

One native invocation per resource is supported, without terminals, replicas, restarts,
retries, or stress loops. The assigned resource output directory retains `nextest.toml`,
`junit.xml` when produced, raw `stdout.log`/`stderr.log`, and `output.complete` after log EOF.
Configuration and captured logs have private permissions; JUnit is written by nextest
inside Aspire's private directory and retains runner-controlled permissions. JUnit collection
is final-report based, not live per-test streaming. CLI foreground/JSON and keep-alive behavior
are owned by the common testing foundation.

## Additional documentation

- https://aspire.dev/integrations/gallery/
- [Aspire documentation](https://aspire.dev/)
- [The Cargo Book](https://doc.rust-lang.org/cargo/)

## Feedback & contributing

https://github.com/microsoft/aspire
