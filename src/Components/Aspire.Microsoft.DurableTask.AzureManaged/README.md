# Aspire.Microsoft.DurableTask.AzureManaged library

Registers a `DurableTaskClient` and a Durable Task worker in the DI container for connecting to a [Durable Task Scheduler](https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler). Enables corresponding health check, logging, and telemetry.

## Getting started

### Prerequisites

- A Durable Task Scheduler instance (or the DTS emulator) and a connection string for connecting to the scheduler.

### Install the package

Install the Aspire Durable Task Scheduler library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Microsoft.DurableTask.AzureManaged
```

## Usage example

In the _Program.cs_ file of your project, call the `AddDurableTaskSchedulerWorker` extension method to register a Durable Task worker for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddDurableTaskSchedulerWorker("scheduler", worker =>
{
    worker.AddTasks(tasks =>
    {
        tasks.AddOrchestrator<MyOrchestrator>();
        tasks.AddActivity<MyActivity>();
    });
});
```

By default, this also registers a `DurableTaskClient` for starting and managing orchestrations. You can retrieve it using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly DurableTaskClient _client;

public ProductsController(DurableTaskClient client)
{
    _client = client;
}
```

If you only need a client (without a worker), use the `AddDurableTaskSchedulerClient` method instead:

```csharp
builder.AddDurableTaskSchedulerClient("scheduler");
```

See the [Durable Task Scheduler documentation](https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler) for more information.

## Configuration

The Aspire Durable Task Scheduler library provides multiple options to configure the connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddDurableTaskSchedulerWorker()`:

```csharp
builder.AddDurableTaskSchedulerWorker("scheduler", worker => { /* register tasks */ });
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "scheduler": "Endpoint=https://my-scheduler.durabletask.io;Authentication=DefaultAzure;TaskHub=MyHub"
  }
}
```

### Use configuration providers

The Aspire Durable Task Scheduler library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `DurableTaskSchedulerSettings` from configuration by using the `Aspire:Microsoft:DurableTask:AzureManaged` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Microsoft": {
      "DurableTask": {
        "AzureManaged": {
          "ConnectionString": "Endpoint=https://my-scheduler.durabletask.io;Authentication=DefaultAzure;TaskHub=MyHub",
          "DisableHealthChecks": false,
          "DisableTracing": false
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<DurableTaskSchedulerSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddDurableTaskSchedulerWorker("scheduler", worker => { /* register tasks */ }, configureSettings: settings => settings.DisableHealthChecks = true);
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Azure.Functions` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Functions
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Durable Task Scheduler and consume the connection using the following methods:

```csharp
var scheduler = builder.AddDurableTaskScheduler("scheduler")
    .RunAsEmulator();

var taskHub = scheduler.AddTaskHub("taskhub");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(taskHub);
```

The `WithReference` method configures a connection in the `MyService` project named `taskhub`. In the _Program.cs_ file of `MyService`, the worker can be consumed using:

```csharp
builder.AddDurableTaskSchedulerWorker("taskhub", worker =>
{
    worker.AddTasks(tasks =>
    {
        tasks.AddOrchestrator<MyOrchestrator>();
        tasks.AddActivity<MyActivity>();
    });
});
```

## Additional documentation

* https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
