# Archetype: language executable app integration

Use this archetype for non-.NET app workloads such as Python, Go, JavaScript, Node, Vite, Next.js, and similar language runtimes.

Representative examples:

- `src/Aspire.Hosting.Python/PythonAppResourceBuilderExtensions.cs`
- `src/Aspire.Hosting.Go/GoHostingExtensions.cs`
- `src/Aspire.Hosting.JavaScript/JavaScriptHostingExtensions.cs`

## Resource shape

Language apps usually derive from `ExecutableResource` and represent workload code, not infrastructure.

They commonly support:

- Local run with the developer's toolchain.
- Toolchain validation with `WithRequiredCommand`.
- Debugger integration.
- OTLP telemetry configuration.
- Run-mode setup siblings.
- Publish-time Dockerfile generation when no user Dockerfile exists.
- Container-file support for publish output and generated Dockerfile build stages.

## Add methods

DO:

- Name APIs by runtime and app style, for example `AddPythonApp`, `AddPythonModule`, `AddGoApp`, `AddNodeApp`, `AddViteApp`, `AddNextJsApp`.
- Normalize paths relative to `builder.AppHostDirectory`.
- Use `Path.GetFullPath(path, builder.AppHostDirectory)` or the repository path-normalization helper used by the existing integration.
- Validate app directory, script, module, package path, or run script parameters.
- Configure default executable, working directory, args, endpoints, and icons.
- Add `WithRequiredCommand` checks for the executable or package manager the resource actually invokes.
- Use specialized Add APIs for materially different entrypoint shapes. For example, first-party Python uses separate script, module, executable, and Uvicorn APIs instead of one ambiguous catch-all.
- Add language-specific docs that explain run and publish behavior.

DON'T:

- Don't assume the current process working directory is the AppHost directory.
- Don't use `Directory.SetCurrentDirectory`.
- Don't silently ignore missing required toolchains when the app cannot run without them.
- Don't validate `node`, `python`, `go`, or another transitive tool when the configured command is really `npm`, `uv`, `go`, `python`, or a package manager wrapper.

## Run-mode behavior

DO:

- Use local toolchain commands such as `python`, `go run`, `node`, package manager scripts, or framework dev servers.
- Add debugging support when the language ecosystem has a standard debugger.
- Add run-mode setup siblings for dependency restore, virtual environments, `go mod`, static analysis, or install commands.
- Mark setup siblings `.ExcludeFromManifest()`.
- Wire setup siblings with `WaitForCompletion`.
- Create setup siblings idempotently with `TryCreateResourceBuilder` when multiple fluent calls can request the same installer or virtual environment creator.
- Add parent relationships from setup siblings back to the app resource so the dashboard remains understandable.
- Use `OnBeforeStart` or equivalent final-model hooks when setup dependencies depend on which annotations were ultimately applied.
- Add development flags such as reload/watch only in run mode.

DON'T:

- Don't run dev-only setup or reload/watch behavior in publish.
- Don't include setup siblings in generated manifests.

## Publish behavior

DO:

- Use `PublishAsDockerFile` or target-specific publish APIs to produce containerizable workloads.
- Generate a Dockerfile only if the app directory does not already contain one.
- Respect user-authored Dockerfiles.
- Validate publish-only prerequisites in build/publish pipeline steps.
- Use deterministic base image defaults, and allow explicit base image overrides.
- Use BuildKit secrets for private package/module credentials.
- Ensure generated images bind to `0.0.0.0` and use deployment-provided ports.
- Add pipeline dependencies when generated container files come from other resources.
- Keep Dockerfile generation mode-aware. Run-mode package-manager scripts, dev servers, reload flags, and local virtual environment paths should not leak into publish output.

DON'T:

- Don't persist credentials in Dockerfile layers.
- Don't overwrite user Dockerfiles or entrypoints.
- Don't fail `aspire start` because a publish-only Dockerfile prerequisite is missing.
- Don't emit Dockerfiles that depend on host-specific absolute paths.

## Framework and publish variants

First-party Aspire language integrations prefer explicit variants when the runtime behavior materially changes.

DO:

- Use subtype resources or thin wrapper methods for framework variants such as Vite and Uvicorn when they add endpoints, TLS, dev-server flags, or publish behavior.
- Keep framework-specific run and publish behavior close to the Add/Publish method that introduces it.
- Provide publish helpers for distinct output shapes, such as static website, Node server, or package-script runtime.
- For C# callback-based publish options, provide a polyglot-friendly exported adapter with primitive/DTO parameters.

DON'T:

- Don't use one generic language app API when the framework changes endpoint binding, TLS, debugger, or production container semantics.

## Mode-specific arguments and environment

Arguments and environment often differ by mode.

Examples:

- Uvicorn should use target host/reload in run mode and `0.0.0.0` without reload in publish mode.
- Next.js should use dev script in run mode and standalone output in publish mode.
- Windows Python run mode may need `PYTHONUTF8=1`.

Always branch explicitly when behavior differs.
