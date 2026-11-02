# Python hosting integration

Use this integration to model, configure, and orchestrate Python scripts, modules, and web applications in an Aspire solution.

## Getting started

### Prerequisites

The example uses [uv](https://docs.astral.sh/uv/), which must be installed and available on `PATH`. The Python application directory must contain a `pyproject.toml` that declares Uvicorn and the application's dependencies, such as FastAPI.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Python` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Python
aspire add Aspire.Hosting.Dotnet
```

## Usage example

Then, in the AppHost, add a Python web application and reference it from another resource with either C# or TypeScript. This example assumes `../python-api/main.py` defines an ASGI application named `app`:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var python = builder.AddUvicornApp("python-api", "../python-api", "main:app")
    .WithUv();

builder.AddDotnetProject("web", "../WebFrontend/WebFrontend.csproj")
    .WithReference(python)
    .WaitFor(python);

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const python = await builder.addUvicornApp("python-api", "../python-api", "main:app")
    .withUv();

await builder.addDotnetProject("web", "../WebFrontend/WebFrontend.csproj")
    .withReference(python)
    .waitFor(python);

await builder.build().run();
```

`AddUvicornApp` configures an HTTP endpoint and enables reload during local development. `WithUv` runs `uv sync` to prepare the virtual environment and install dependencies before the application starts.

For a script instead of an ASGI web application, use `AddPythonApp("python", "../python-app", "main.py")`. Use `AddPythonModule` to run a module with `python -m`.

## Experimental pytest testing

`AddPytest` participates in the experimental `aspire test` command (`ASPIRETESTING001`). These APIs are in development; see the [testing foundation](https://github.com/microsoft/aspire/pull/21527) and [MSTest layer](https://github.com/microsoft/aspire/pull/21559) for the shared testing contract, rather than assuming released packages expose them.

Declare pytest in your Python application's `requirements.txt` or `pyproject.toml`. The helper uses the existing Python virtual-environment and dependency-installation conventions; it does not install pytest globally or require a reporting plugin.

**C#**

```csharp
#pragma warning disable ASPIRETESTING001

builder.AddPytest("api-tests", "../python-api")
    .WithUv()
    .WithArgs("tests", "-q")
    .WithReference(python)
    .WaitFor(python);
```

**TypeScript**

```typescript
await builder.addPytest("api-tests", "../python-api")
    .withUv()
    .withArgs(["tests", "-q"])
    .withReference(python)
    .waitFor(python);
```

The preview CLI command is disabled by default. From the AppHost directory, opt in only for that invocation without changing saved configuration:

```bash
features__testCommandEnabled=true aspire test
```

`WithReference` supplies connection configuration; `WaitFor` waits for the referenced service before starting tests. Use `WithArgs` for pytest selectors and options, without changing the application's working directory or import mode. The feature flag gates CLI registration, not the hosting collector API.

`AddPytest` returns the existing Python resource builder. By default, it launches `.venv/bin/pytest` (`.venv/Scripts/pytest.exe` on Windows), with the usual application/AppHost environment discovery. `WithVirtualEnvironment("custom-venv")` selects a different environment. Requirements-based projects use the existing pip installer. `WithUv` runs an installer child with `uv sync`, then launches pytest directly, not `uv run`; it aligns `UV_PROJECT_ENVIRONMENT` with the selected environment. For a custom dependency group, use `WithUv(args: ["sync", "--group", "test"])`; these arguments belong to the installer, not pytest.

During testing, Aspire configures `--junitxml=<fresh-runner-directory>/pytest.xml` before launch and before any `--` separator, overriding report destinations in pytest configuration or `PYTEST_ADDOPTS`. Explicit report redirection and response files in `WithArgs` are rejected. Keep pytest's built-in JUnit plugin enabled. The runtime-only `WithTesting(async context => { ... })` collector waits for process completion and stages the canonical report into `context.OutputPath`, even after a failing exit. Collection never runs during model construction or ordinary runs; runner configuration is independent of callback execution or replacement. The AppHost file-system service cleans up runner temporary files on disposal. Missing, malformed, unsupported, inconsistent, linked, or already-existing destination reports fail collection explicitly. Every nonzero process exit fails the run, including exit 5 (no tests collected); exit 2 (interrupted or aborted collection) is not treated as user cancellation without Aspire cancellation.

The common summary counts **JUnit report outcomes**, not unique pytest items. JUnit `<failure>` maps to `Failed`, `<error>` to `Error` (including collection/setup/teardown errors), `<skipped>` to `Skipped` (including xfail), and an outcome-free case to `Passed`. Parameterized names, durations, and diagnostics are preserved; no timeout or cancellation category is guessed from text or process exit. Only `Passed` and `Skipped` are non-failing outcomes. A call failure and teardown error may yield separate outcomes with identical names; a skip and teardown error can share one XML case. A passing call followed by a teardown error may count twice in pytest's suite total but emit only an error; Aspire does not invent the hidden pass. No outcomes are deduplicated. Non-strict XPASS is indistinguishable from a pass in built-in XML; strict XPASS fails. Raw suite counters and captured output remain in `pytest.xml`.

Only a single invocation per resource is supported during testing: replicas and restarts are rejected, and retry/repeat/loop plugins are unsupported. Common retry/loop arguments are rejected; do not enable these modes through plugins or configuration. Reports are finalized at completion, not streamed as live test events. Cancellation interrupts collection and may leave incomplete or absent XML. Artifacts use the flat layout `<run>/<logical-resource-name>/pytest.xml`, with no GUID suffix or `resources` intermediary. The runtime callback receives `ResourceTestingContext`: `context.TestingContext` is the containing `TestingContext`, whose `OutputPath` is the run root; `context.OutputPath` is the resource folder. TypeScript exposes `context.testingContext()` and `outputPath()` getters. Aspire owns the private output directory (0700 on Unix); staged files use the copy writer's permissions, not a per-file 0600 guarantee. Ordinary `aspire run` launches pytest without Aspire reporting arguments or collectors. The resource is excluded from publishing.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/frameworks/python/
* https://docs.python.org/3/
* https://docs.astral.sh/uv/
* https://uvicorn.dev/
* https://docs.pytest.org/en/stable/how-to/output.html#creating-junitxml-format-files
* https://docs.pytest.org/en/stable/reference/exit-codes.html

## Feedback & contributing

https://github.com/microsoft/aspire
