# Azure Functions hosting integration

Use this integration to model, configure, and orchestrate Azure Functions projects in an Aspire solution.

## Getting started

### Prerequisites

* An Aspire project based on the starter template.
* An Azure Functions app project.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Functions` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Functions
```

## Usage example

In the AppHost, use `AddAzureFunctionsProject` to configure the Functions app resource. C# AppHosts use the generated project metadata type; TypeScript AppHosts point at the Functions app directory.

**C#**

```csharp
using Aspire.Hosting;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Functions;

var builder = new DistributedApplicationBuilder();

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var queue = storage.AddQueues("queue");
var blob = storage.AddBlobs("blob");

builder.AddAzureFunctionsProject<Projects.Company_FunctionApp>("my-functions-project")
    .WithReference(queue)
    .WithReference(blob);

var app = builder.Build();

app.Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const storage = await builder.addAzureStorage("storage").runAsEmulator();
const queue = await storage.addQueues("queue");
const blob = await storage.addBlobs("blob");

await builder.addAzureFunctionsProject("my-functions-project", "../Company.FunctionApp")
    .withReference(queue)
    .withReference(blob);

await builder.build().run();
```

## Durable Task Scheduler (Durable Functions)

The Azure Functions hosting integration also provides resource APIs for using the Durable Task Scheduler (DTS) with Durable Functions.

In the AppHost, add a Scheduler resource, create one or more Task Hubs, and pass the connection string and hub name to your Functions app resource:

```csharp
using Aspire.Hosting;
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

- A "Scheduler Dashboard" URL for the scheduler resource.
- A "Task Hub Dashboard" URL for each Task Hub resource.
- A `DTS_TASK_HUB_NAMES` environment variable on the emulator container listing the Task Hub names associated with that scheduler.

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

- https://aspire.dev/integrations/gallery/
- https://aspire.dev/integrations/cloud/azure/azure-functions/azure-functions-host/
- https://learn.microsoft.com/azure/azure-functions
- https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler

## Feedback & contributing

https://github.com/microsoft/aspire
