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

Run `aspire test` from the AppHost directory. `WithReference` supplies connection configuration; `WaitFor` waits for the referenced service before starting tests. Use `WithArgs` for pytest selectors and options, without changing the application's working directory or import mode.

`AddPytest` returns the existing Python resource builder. By default, it launches `.venv/bin/pytest` (`.venv/Scripts/pytest.exe` on Windows), with the usual application/AppHost environment discovery. `WithVirtualEnvironment("custom-venv")` selects a different environment. Requirements-based projects use the existing pip installer. `WithUv` runs an installer child with `uv sync`, then launches pytest directly, not `uv run`; it aligns `UV_PROJECT_ENVIRONMENT` with the selected environment. For a custom dependency group, use `WithUv(args: ["sync", "--group", "test"])`; these arguments belong to the installer, not pytest.

During testing, Aspire appends `--junitxml=<resource-output>/pytest.xml` before any `--` separator, overriding report destinations in pytest configuration or `PYTEST_ADDOPTS`. Explicit report redirection and response files in `WithArgs` are rejected. Keep pytest's built-in JUnit plugin enabled. The collector waits for process completion and harvests the report even after a failing exit. Missing, malformed, unsupported, or inconsistent reports fail collection explicitly. Every nonzero process exit fails the run, including exit 5 (no tests collected); exit 2 (interrupted or aborted collection) is not treated as user cancellation without Aspire cancellation.

The common summary counts **JUnit report outcomes**, not unique pytest items. Parameterized names, failures, skips/xfails, collection/setup/teardown errors, durations, and diagnostic text are preserved. A call failure and teardown error may yield separate outcomes with identical names; a skip and teardown error can share one XML case. A passing call followed by a teardown error may count twice in pytest's suite total but emit only an error; Aspire does not invent the hidden pass. No outcomes are deduplicated. Non-strict XPASS is indistinguishable from a pass in built-in XML; strict XPASS fails. Raw suite counters and captured output remain in `pytest.xml`.

Only a single invocation per resource is supported during testing: replicas and restarts are rejected, and retry/repeat/loop plugins are unsupported. Common retry/loop arguments are rejected; do not enable these modes through plugins or configuration. Reports are finalized at completion, not streamed as live test events. Cancellation interrupts collection and may leave incomplete or absent XML. Aspire owns the private output directory (0700 on Unix); pytest controls report-file permissions. Ordinary `aspire run` launches pytest without Aspire reporting arguments or collectors. The resource is excluded from publishing.

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
