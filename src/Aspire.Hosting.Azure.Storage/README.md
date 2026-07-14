# Azure Storage hosting integration

Use this integration to model, configure, and orchestrate Azure Storage in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Storage` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Storage
```

## Configure Azure Provisioning for local development

Adding Azure resources to the AppHost model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via AppHost configuration. From your AppHost directory, set these values with `aspire secret set`:

```bash
aspire secret set Azure:SubscriptionId "<your subscription id>"
aspire secret set Azure:ResourceGroupPrefix "<prefix for the resource group>"
aspire secret set Azure:Location "<azure location>"
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

In the AppHost, add a Blob (can use tables or queues also) Storage connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(blobs);
```

**TypeScript**

```typescript
const blobs = await builder.addAzureStorage("storage").addBlobs("blobs");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(blobs);
```

## Creating and using blob containers and queues directly

You can create and use blob containers and queues directly by adding them to your storage resource. This allows you to provision and reference specific containers or queues for your services.

### Adding a blob container

```csharp
var storage = builder.AddAzureStorage("storage");
var container = storage.AddBlobContainer("my-container");
```

The container reference can be passed to another resource:

```csharp
builder.AddProject<Projects.MyService>()
       .WithReference(container);
```

### Adding a queue

```csharp
var storage = builder.AddAzureStorage("storage");
var queue = storage.AddQueue("my-queue");
```

The queue reference can be passed to another resource:

```csharp
builder.AddProject<Projects.MyService>()
       .WithReference(queue);
```

This approach allows you to define and reference specific blob containers and queues as first-class resources in your AppHost model.

## Creating and using data lake
```csharp
var storage = builder.AddAzureStorage("azure-storage");
var dataLake = storage.AddDataLake("data-lake");
var fileSystem = storage.AddDataLakeFileSystem("data-lake-file-system");
```

The references can be passed to a project:

```csharp
api.WithReference(dataLake).WithReference(fileSystem);
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

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-storage-blobs/azure-storage-blobs-host/
* https://aspire.dev/integrations/cloud/azure/azure-storage-queues/azure-storage-queues-host/
* https://aspire.dev/integrations/cloud/azure/azure-storage-tables/azure-storage-tables-host/
* https://learn.microsoft.com/azure/storage/common/storage-introduction

## Feedback & contributing

https://github.com/microsoft/aspire
