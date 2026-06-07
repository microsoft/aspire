# Azure Functions hosting integration

Use this integration to model, configure, and orchestrate Azure Functions apps in an Aspire solution.

## Getting started

### Prerequisites

* An Aspire project based on the starter template.
* An Azure Functions app. .NET isolated worker apps can be referenced as projects. TypeScript and JavaScript apps can be referenced by directory.
* Azure Functions Core Tools for local JavaScript Functions apps. TypeScript Functions apps normally reference `azure-functions-core-tools` from `devDependencies` and run it through an npm script.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Functions` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Functions
```

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

builder.AddAzureFunctionsProject<Projects.Company_FunctionApp>("functions")
    .WithReference(queue)
    .WithReference(blob);

builder.Build().Run();
```

**C# - TypeScript or JavaScript app directory**

```csharp
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Functions;

var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var blob = storage.AddBlobs("blob");

builder.AddAzureFunctionsApp("functions", "../functions", AzureFunctionsLanguage.TypeScript)
    .WithReference(blob);

builder.Build().Run();
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

When a TypeScript or JavaScript Functions app does not include a `Dockerfile`, Aspire publish generates a Dockerfile that uses the official Azure Functions Node image, copies the app to `/home/site/wwwroot`, installs npm dependencies, and runs `npm run build` for TypeScript apps.

If the Functions app directory already contains a `Dockerfile`, Aspire uses that Dockerfile instead. Use this to select a different Azure Functions Node image, customize the Node version, or add project-specific build steps.

When deployed to Azure Container Apps, Functions resources are published with the container app `kind` set to `functionapp` and the Functions container port set to `80`.

## Debugging

.NET isolated worker Functions projects use the Azure Functions extension integration for debugging.

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

builder.AddAzureFunctionsProject<Projects.Company_FunctionApp>("funcapp")
    .WithHostStorage(storage)
    .WithReference(taskHub);

builder.Build().Run();
```

### Use the DTS emulator

`RunAsEmulator()` starts a local container running the Durable Task Scheduler emulator.

When a Scheduler runs as an emulator, Aspire automatically provides:

* A "Scheduler Dashboard" URL for the scheduler resource.
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
