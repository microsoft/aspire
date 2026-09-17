# Python hosting integration

Use this integration to model, configure, and orchestrate Python scripts, modules, and web applications in an Aspire solution.

## Getting started

### Prerequisites

The example uses [uv](https://docs.astral.sh/uv/), which must be installed and available on `PATH`. The Python application directory must contain a `pyproject.toml` that declares Uvicorn and the application's dependencies, such as FastAPI.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Python` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Python
```

## Usage example

Then, in the AppHost, add a Python web application and reference it from another resource with either C# or TypeScript. This example assumes `../python-api/main.py` defines an ASGI application named `app`:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var python = builder.AddUvicornApp("python-api", "../python-api", "main:app")
    .WithUv();

builder.AddProject<Projects.WebFrontend>("web")
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

await builder.addProject("web", "../WebFrontend/WebFrontend.csproj")
    .withReference(python)
    .waitFor(python);

await builder.build().run();
```

`AddUvicornApp` configures an HTTP endpoint and enables reload during local development. `WithUv` runs `uv sync` to prepare the virtual environment and install dependencies before the application starts.

For a script instead of an ASGI web application, use `AddPythonApp("python", "../python-app", "main.py")`. Use `AddPythonModule` to run a module with `python -m`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/frameworks/python/
* https://docs.python.org/3/
* https://docs.astral.sh/uv/
* https://uvicorn.dev/

## Feedback & contributing

https://github.com/microsoft/aspire
