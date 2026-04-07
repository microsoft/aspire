# Aspire.Hosting.Azure.Storage library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Storage.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

Install the Aspire Azure Storage Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Storage
```

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via .NET configuration. Set these values in user secrets in order to allow resources to be configured
automatically.

```json
{
    "Azure": {
      "SubscriptionId": "<your subscription id>",
      "ResourceGroupPrefix": "<prefix for the resource group>",
      "Location": "<azure location>"
    }
}
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

In the _AppHost.cs_ file of `AppHost`, add a Blob (can use tables or queues also) Storage connection and consume the connection using the following methods:

```csharp
var blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(blobs);
```

The `WithReference` method passes that connection information into a connection string named `blobs` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using the client library [Aspire.Azure.Storage.Blobs](https://www.nuget.org/packages/Aspire.Azure.Storage.Blobs):

```csharp
builder.AddAzureBlobServiceClient("blobs");
```

## Creating and using blob containers and queues directly

You can create and use blob containers and queues directly by adding them to your storage resource. This allows you to provision and reference specific containers or queues for your services.

### Adding a blob container

```csharp
var storage = builder.AddAzureStorage("storage");
var container = storage.AddBlobContainer("my-container");
```

You can then pass the container reference to a project:

```csharp
builder.AddProject<Projects.MyService>()
       .WithReference(container);
```

In your service, consume the container using:

```csharp
builder.AddAzureBlobContainerClient("my-container");
```

This will register a singleton of type `BlobContainerClient`.

### Adding a queue

```csharp
var storage = builder.AddAzureStorage("storage");
var queue = storage.AddQueue("my-queue");
```

Pass the queue reference to a project:

```csharp
builder.AddProject<Projects.MyService>()
       .WithReference(queue);
```

In your service, consume the queue using:

```csharp
builder.AddAzureQueue("my-queue");
```

This will register a singleton of type `QueueClient`.

This approach allows you to define and use specific blob containers and queues as first-class resources in your Aspire application model.

## Creating and using data lake
```csharp
var storage = builder.AddAzureStorage("azure-storage");
var dataLake = storage.AddDataLake("data-lake");
var fileSystem = storage.AddDataLakeFileSystem("data-lake-file-system");
```

You can then pass the references to a project:

```csharp
api.WithReference(dataLake).WithReference(fileSystem);
```

In your service, consume the services using:

```csharp
builder.AddAzureDataLakeServiceClient("data-lake");
builder.AddAzureDataLakeFileSystemClient("data-lake-file-system");
```

## Connection Properties

When you reference Azure Storage resources using `WithReference`, the following connection properties are made available to the consuming project:

### Azure Storage

The Azure Storage account resource doesn't expose any connection property, reference sub-resources:

### Blob Storage

The Blob Storage resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The URI of the blob storage service, with the format `https://mystorageaccount.blob.core.windows.net/` |
| `ConnectionString` | **Emulator only.** The connection string for the blob storage service |

### Blob Container

The Blob Container resource inherits all properties from its parent `AzureBlobStorageResource` and adds:

| Property Name | Description |
|---------------|-------------|
| `BlobContainerName` | The name of the blob container |

### Data Lake Storage

The Data Lake Storage resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The URI of the data lake storage service, with the format `https://mystorageaccount.dfs.core.windows.net/` |

Emulator currently does not support data lake storage.

### Data Lake File System

The Data Lake FileSystem resource inherits all properties from its parent `AzureDataLakeStorageResource` and adds:

| Property Name | Description |
|---------------|-------------|
| `DataLakeFileSystemName` | The name of the data lake file system |

Emulator currently does not support data lake storage.

### Queue Storage

The Queue Storage resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The URI of the queue storage service, with the format `https://mystorageaccount.queue.core.windows.net/` |
| `ConnectionString` | **Emulator only.** The connection string for the queue storage service |

### Queue

The Queue resource inherits all properties from its parent `AzureQueueStorageResource` and adds:

| Property Name | Description |
|---------------|-------------|
| `QueueName` | The name of the queue |
| `ConnectionString` | **Emulator only.** The connection string for the queue storage service |

### Table Storage

The Table Storage resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The URI of the table storage service, with the format `https://mystorageaccount.table.core.windows.net/` |
| `ConnectionString` | The connection string for the table storage service |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `queue1` becomes `QUEUE1_URI`.

## Additional documentation

* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
