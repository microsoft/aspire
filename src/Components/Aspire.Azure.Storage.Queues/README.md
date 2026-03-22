# Aspire.Azure.Storage.Queues library

Registers [QueueServiceClient](https://learn.microsoft.com/dotnet/api/azure.storage.queues.queueserviceclient) as a singleton in the DI container for connecting to Azure Queue Storage. Enables corresponding health check, logging and telemetry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure Storage account - [create a storage account](https://learn.microsoft.com/azure/storage/common/storage-account-create)

### Install the package

Install the Aspire Azure Storage Queues library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Storage.Queues
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureQueueClient` extension method to register a `QueueServiceClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureQueueServiceClient("queue");
```

You can then retrieve the `QueueServiceClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly QueueServiceClient _client;

public ProductsController(QueueServiceClient client)
{
    _client = client;
}
```

See the [Azure.Storage.Queues documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Queues/README.md) for examples on using the `QueueServiceClient`.

## Configuration

The Aspire Azure Storage Queues library provides multiple options to configure the Azure Storage Queues connection based on the requirements and conventions of your project. Note that either a `ServiceUri` or a `ConnectionString` is a required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureQueueServiceClient()`:

```csharp
builder.AddAzureQueueServiceClient("queueConnectionName");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Service URI

The recommended approach is to use a ServiceUri, which works with the `AzureStorageQueuesSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "queueConnectionName": "https://{account_name}.queue.core.windows.net/"
  }
}
```

#### Connection string

Alternatively, an [Azure Storage connection string](https://learn.microsoft.com/azure/storage/common/storage-configure-connection-string) can be used.

```json
{
  "ConnectionStrings": {
    "queueConnectionName": "AccountName=myaccount;AccountKey=myaccountkey"
  }
}
```

### Use configuration providers

The Aspire Azure Storage Queues library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureStorageQueuesSettings` and `QueueClientOptions` from configuration by using the `Aspire:Azure:Storage:Queues` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Azure": {
      "Storage": {
        "Queues": {
          "DisableHealthChecks": true,
          "DisableTracing": false,
          "ClientOptions": {
            "Diagnostics": {
              "ApplicationId": "myapp"
            }
          }
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<AzureStorageQueuesSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddAzureQueueServiceClient("queue", settings => settings.DisableHealthChecks = true);
```

You can also setup the [QueueClientOptions](https://learn.microsoft.com/dotnet/api/azure.storage.queues.queueclientoptions) using the optional `Action<IAzureClientBuilder<QueueServiceClient, QueueClientOptions>> configureClientBuilder` parameter of the `AddAzureQueueClient` method. For example, to set the first part of "User-Agent" headers for all requests issues by this client:

```csharp
builder.AddAzureQueueServiceClient("queue", configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(options => options.Diagnostics.ApplicationId = "myapp"));
```

## AppHost extensions

In your AppHost project, install the Aspire Azure Storage Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Storage
```

Then, in the _AppHost.cs_ file of `AppHost`, add a Storage Queue connection and consume the connection using the following methods:

```csharp
var queue = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureStorage("storage").AddQueues("queue")
    : builder.AddConnectionString("queue");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(queue);
```

The `AddQueues` method adds an Azure Storage queue to the builder. Or `AddConnectionString` can be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:queue` config key. The `WithReference` method passes that connection information into a connection string named `queue` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureQueueServiceClient("queue");
```

## Additional documentation

* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Queues/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
