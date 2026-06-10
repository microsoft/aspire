# Aspire.Azure.Storage.Blobs

Registers a [BlobServiceClient](https://learn.microsoft.com/dotnet/api/azure.storage.blobs.blobserviceclient) service as a singleton in the DI container for connecting to Azure Storage Blobs. Enables corresponding health checks, logging and telemetry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure Storage account - [create a storage account](https://learn.microsoft.com/azure/storage/common/storage-account-create)

### Install the package

Install the Aspire Azure Storage Blobs library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Storage.Blobs
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureBlobClient` extension method to register a `BlobServiceClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureBlobServiceClient("blobs");
```

You can then retrieve the `BlobServiceClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly BlobServiceClient _client;

public ProductsController(BlobServiceClient client)
{
    _client = client;
}
```

See the [Azure.Storage.Blobs documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/README.md) for examples on using the `BlobServiceClient`.

## Configuration

The Aspire Azure Storage Blobs library provides multiple options to configure the Azure Storage Blob connection based on the requirements and conventions of your project. Note that either a `ServiceUri` or a `ConnectionString` is a required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureBlobServiceClient()`:

```csharp
builder.AddAzureBlobServiceClient("blobsConnectionName");
```

And then the connection information will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Service URI

The recommended approach is to use a ServiceUri, which works with the `AzureStorageBlobsSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "blobsConnectionName": "https://{account_name}.blob.core.windows.net/"
  }
}
```

#### Connection string

Alternatively, an [Azure Storage connection string](https://learn.microsoft.com/azure/storage/common/storage-configure-connection-string) can be used.

```json
{
  "ConnectionStrings": {
    "blobsConnectionName": "AccountName=myaccount;AccountKey=myaccountkey"
  }
}
```

### Use configuration providers

The Aspire Azure Storage Blobs library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureStorageBlobsSettings` and `BlobClientOptions` from configuration by using the `Aspire:Azure:Storage:Blobs` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Azure": {
      "Storage": {
        "Blobs": {
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

You can also pass the `Action<AzureStorageBlobsSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddAzureBlobServiceClient("blobs", settings => settings.HealthChecks = false);
```

You can also setup the [BlobClientOptions](https://learn.microsoft.com/dotnet/api/azure.storage.blobs.blobclientoptions) using the optional `Action<IAzureClientBuilder<BlobServiceClient, BlobClientOptions>> configureClientBuilder` parameter of the `AddAzureBlobClient` method. For example, to set the first part of "User-Agent" headers for all requests issues by this client:

```csharp
builder.AddAzureBlobServiceClient("blobs", configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(options => options.Diagnostics.ApplicationId = "myapp"));
```

## AppHost extensions

In your AppHost project, install the Aspire Azure Storage Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Storage
```

Then, in the _AppHost.cs_ file of `AppHost`, add a Blob Storage connection and consume the connection using the following methods:

```csharp
var blobs = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureStorage("storage").AddBlobs("blobs")
    : builder.AddConnectionString("blobs");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(blobs);
```

The `AddBlobs` method adds an Azure Storage blob service resource to the builder. Or `AddConnectionString` method can be used be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:blobs` config key. The `WithReference` method passes that connection information into a connection string named `blobs` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureBlobServiceClient("blobs");
```

## Additional documentation

* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
