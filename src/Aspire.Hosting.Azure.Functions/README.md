# Aspire.Hosting.Azure.Functions library

Provides methods to the Aspire hosting model for Azure functions.

## Getting started

### Prerequisites

* An Aspire project based on the starter template.
* A .NET-based Azure Functions worker project.

### Install the package

In your AppHost project, install the Aspire Azure Functions Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Functions
```

## Usage example

Add a reference to the .NET-based Azure Functions project in your `AppHost` project.

```dotnetcli
dotnet add reference ..\Company.FunctionApp\Company.FunctionApp.csproj
```

In the _AppHost.cs_ file of `AppHost`, use the `AddAzureFunctionsProject` to configure the Functions project resource.

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

## Durable Task Scheduler (Durable Functions)

The Azure Functions hosting library also provides resource APIs for using the Durable Task Scheduler (DTS) with Durable Functions.

In the _AppHost.cs_ file of `AppHost`, add a Scheduler resource, create one or more Task Hubs, and pass the connection string and hub name to your Functions project:

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
    .WithEnvironment("DURABLE_TASK_SCHEDULER_CONNECTION_STRING", scheduler)
    .WithEnvironment("TASKHUB_NAME", taskHub.Resource.TaskHubName);

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

- https://learn.microsoft.com/azure/azure-functions
- https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler

## Feedback & contributing

https://github.com/microsoft/aspire
