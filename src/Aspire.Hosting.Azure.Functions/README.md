# Azure Functions hosting integration

Use this integration to model, configure, and orchestrate Azure Functions apps in an Aspire solution.

## Getting started

### Prerequisites

* An Aspire project based on the starter template.
* An Azure Functions app. C# isolated-worker apps can be referenced as SDK-style projects. TypeScript and JavaScript apps can be referenced by directory.
* Azure Functions Core Tools for local .NET and JavaScript Functions apps. TypeScript Functions apps normally reference `azure-functions-core-tools` from `devDependencies` and run it through an npm script.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Functions` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Functions
```

Use `AddAzureFunctions(name, projectPath)` for .NET Functions projects. It returns an
`AzureFunctionsResource` based on `DotnetProjectResource`, preserving the Functions host,
binding configuration, and publishing behavior. The old `AddAzureFunctionsProject` overloads
and `AzureFunctionsProjectResource` remain available but are obsolete. See the
[project migration guide](../../docs/migrate-from-addproject.md#azure-functions-projects).

## Usage example

In the AppHost, add an Azure Functions app resource and reference Azure resources with either C# or TypeScript.

**C# - .NET isolated worker project**

```csharp
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Functions;

var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var queue = storage.AddQueues("queue");
var blob = storage.AddBlobs("blob");

builder.AddAzureFunctions("functions", "../AzureFunctionsEndToEnd.Functions/AzureFunctionsEndToEnd.Functions.csproj")
    .WithReference(queue)
    .WithReference(blob);

builder.Build().Run();
```

**C# - TypeScript or JavaScript app directory**

```csharp
#pragma warning disable ASPIREAZUREFUNCTIONS001

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Functions;

var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var blob = storage.AddBlobs("blob");

builder.AddAzureFunctionsApp("functions", "../functions", AzureFunctionsLanguage.TypeScript)
    .WithReference(blob);

builder.Build().Run();

#pragma warning restore ASPIREAZUREFUNCTIONS001
```

**TypeScript**

```typescript
import { AzureFunctionsLanguage, createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const storage = await builder.addAzureStorage("storage").runAsEmulator();
const blob = await storage.addBlobs("blob");

await builder.addAzureFunctionsApp("functions", "../functions", AzureFunctionsLanguage.TypeScript)
    .withReference(blob);

await builder.build().run();
```

## TypeScript and JavaScript Functions apps

The directory-based APIs (`AddAzureFunctionsApp`, `AzureFunctionsAppResource`, `AzureFunctionsLanguage`, and the app-specific extension overloads) are experimental pending API review and may change or be removed. C# callers must opt in by suppressing diagnostic `ASPIREAZUREFUNCTIONS001`, for example with `#pragma warning disable ASPIREAZUREFUNCTIONS001`. The .NET Functions APIs do not require this experimental opt-in.

Directory-based Functions apps run on the Azure Functions Node worker with `FUNCTIONS_WORKER_RUNTIME` set to `node`.

TypeScript apps are started with:

```shell
npm run start -- --port <port>
```

The Functions app should include scripts similar to the standard Azure Functions TypeScript template:

```json
{
  "scripts": {
    "build": "tsc",
    "prestart": "npm run build",
    "start": "func start"
  },
  "devDependencies": {
    "azure-functions-core-tools": "^4.0.0",
    "typescript": "^5.0.0"
  }
}
```

JavaScript apps are started directly with:

```shell
func host start --port <port>
```

Use `WithHostStorage` to choose the Azure Storage resource used by the Functions host. If host storage is not specified, Aspire adds an implicit storage resource for the Functions runtime.

## Publish and deployment

When a TypeScript or JavaScript Functions app does not include a `Dockerfile`, Aspire publish generates a Dockerfile that uses the official Azure Functions Node image, copies the app to `/home/site/wwwroot`, installs npm dependencies, and runs `npm run build` for TypeScript apps. After the build (or installation for JavaScript), it runs `npm prune --omit=dev` so the deployed app retains only production dependencies.

If the Functions app directory already contains a `Dockerfile`, Aspire uses that Dockerfile instead. Use this to select a different Azure Functions Node image, customize the Node version, or add project-specific build steps.

When deployed to Azure Container Apps, Functions resources are published with the container app
`kind` set to `functionapp`. .NET Functions use SDK container publishing and target port `8080`;
TypeScript and JavaScript Functions use port `80`. Functions apps cannot be published as
Container App Jobs.

## Debugging

.NET isolated-worker Functions projects start Core Tools, which hosts and launches the isolated
worker. The SDK selects the process invocation and host working directory; Aspire does not
launch the worker directly.

An IDE advertising `azure-functions-with-external-build.v1` launches the output produced by
Aspire's coordinated build without rebuilding it. Build-only environment variables configured
with `WithBuildEnvironment` are used for that build and IDE output evaluation, not injected into
the host or worker runtime environment.

With an older IDE that supports the legacy Functions launcher, or a capability-less Visual
Studio session with the Functions project loaded in the solution, the IDE builds the project.
Aspire logs one startup warning per affected resource because the IDE may use different build
configuration or build environment settings. This fallback waits for the entire existing
coordinated initial-build batch, including failures, but does not add the Functions project to
that batch. Upgrade the IDE integration or run outside the IDE to use Aspire-owned builds.
Ordinary project external-build support alone is not Functions external-build support.

TypeScript and JavaScript Functions apps use VS Code JavaScript debugging. Aspire does not add a service-discovery endpoint for the Node inspector. During a VS Code debug session, the Aspire extension starts the Functions host with a loopback-only Node inspector port by setting `languageWorkers__node__arguments`, then attaches the JavaScript debugger to that port.

TypeScript debugging assumes the standard Azure Functions TypeScript output under `dist/**/*.js` unless an explicit VS Code debugger configuration overrides `outFiles`.

## Durable Task Scheduler (Durable Functions)

The Azure Functions hosting integration also provides resource APIs for using the Durable Task Scheduler (DTS) with Durable Functions.

In the AppHost, add a Scheduler resource, create one or more Task Hubs, and pass the connection string and hub name to your Functions app resource:

```csharp
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Functions;

var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var scheduler = builder.AddDurableTaskScheduler("scheduler")
    .RunAsEmulator();

var taskHub = scheduler.AddTaskHub("taskhub");

builder.AddAzureFunctions("funcapp", "../AzureFunctionsWithDts.Functions/AzureFunctionsWithDts.Functions.csproj")
    .WithHostStorage(storage)
    .WithReference(taskHub);

builder.Build().Run();
```

### Use the DTS emulator

`RunAsEmulator()` starts a local container running the Durable Task Scheduler emulator.

When a Scheduler runs as an emulator, Aspire automatically provides:

* A "Manage" URL for the scheduler resource.
* A "Task Hub Dashboard" URL for each Task Hub resource.
* A `DTS_TASK_HUB_NAMES` environment variable on the emulator container listing the Task Hub names associated with that scheduler.

### Use an existing Scheduler

If you already have a Scheduler instance, configure the resource using its connection string:

```csharp
var schedulerConnectionString = builder.AddParameter(
    "dts-connection-string",
    "Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure");

var scheduler = builder.AddDurableTaskScheduler("scheduler")
    .RunAsExisting(schedulerConnectionString);

var taskHubName = builder.AddParameter("taskhub-name", "mytaskhub");
var taskHub = scheduler.AddTaskHub("taskhub").WithTaskHubName(taskHubName);
```

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-functions/azure-functions-host/
* https://learn.microsoft.com/azure/azure-functions
* https://learn.microsoft.com/azure/azure-functions/functions-reference-node
* https://learn.microsoft.com/azure/azure-functions/functions-how-to-custom-container
* https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler

## Feedback & contributing

https://github.com/microsoft/aspire
