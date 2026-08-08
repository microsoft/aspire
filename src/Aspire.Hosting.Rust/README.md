# Rust hosting integration

Use this integration to model, configure, and orchestrate a Rust application resource in an Aspire
solution.

## Getting started

### Prerequisites

The **Rust toolchain** (`cargo`) must be available on the PATH of the machine running the AppHost.
Install it with [rustup](https://www.rust-lang.org/tools/install).

For VS Code debugging, install the platform's native debugger extension:
[C/C++](https://marketplace.visualstudio.com/items?itemName=ms-vscode.cpptools) on Windows, or
[CodeLLDB](https://marketplace.visualstudio.com/items?itemName=vadimcn.vscode-lldb) on Linux and macOS.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Rust` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Rust
```

## Usage example

In the AppHost, add a Rust application resource with either C# or TypeScript:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddRustApp("api", "../rust-api")
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

await builder.addRustApp("api", "../rust-api")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

await builder.build().run();
```

`appDirectory` is the directory containing `Cargo.toml`. Arguments for your program are passed with
`.WithArgs(...)`; arguments for cargo itself are passed with `.WithCargoArgs(...)`.

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
| `WithCargoArgs(Action<RustCargoArgsCallbackContext> callback)` | Computes cargo arguments when the resource starts. An async `Func<RustCargoArgsCallbackContext, Task>` overload is also available |
| `WithCargoReleaseBuild(bool releaseBuild = true)` | Adds `--release`. Publishing adds it by default, so pass `false` to publish an unoptimized image |
| `WithCargoLocked(bool locked = true)` | Adds `--locked`, which fails rather than updating `Cargo.lock`. Publishing adds it by default whenever the crate has a lock file, so pass `false` to opt out |
| `WithCargoFeatures(params string[] features)` | Adds `--features` with the supplied features |
| `WithCargoBinTarget(string binName)` | Adds `--bin` to select one of several `[[bin]]` targets |
| `WithCargoExample(string exampleName)` | Adds `--example` to run an example instead of a binary |
| `WithCargoPackage(string packageName)` | Adds `--package` to select a workspace member |
| `WithCargoTarget(string target)` | Adds `--target` to cross-compile for a specific triple |
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
| Build | `rust:alpine` (current stable; a `rust-toolchain.toml` pin is installed by rustup inside the image) |
| Runtime | `alpine:3.24` |

If you change either image with `WithDockerfileBaseImage`, or name an explicit target with
`WithCargoTarget`, it is on you to keep the libc compatible across the three — the defaults are all
musl.

## Additional documentation

- https://aspire.dev/integrations/gallery/
- https://aspire.dev/integrations/frameworks/rust/rust-host/
- [Aspire documentation](https://aspire.dev/)
- [The Cargo Book](https://doc.rust-lang.org/cargo/)

## Feedback & contributing

https://github.com/microsoft/aspire
