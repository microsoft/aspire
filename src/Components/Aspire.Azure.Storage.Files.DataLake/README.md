# Aspire.Azure.Storage.Files.DataLake

Registers a [DataLakeServiceClient](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.files.datalake.datalakeserviceclient?view=azure-dotnet) service or [DataLakeFileSystemClient](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.files.datalake.datalakefilesystemclient?view=azure-dotnet)  as a singleton in the DI container for connecting to Azure Data Lake Storage. Enables corresponding health checks, logging and telemetry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure Storage account - [create a storage account](https://learn.microsoft.com/azure/storage/common/storage-account-create)

### Install the package

Install the .NET Aspire Azure Storage DataLake library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Storage.Files.DataLake
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureDataLakeServiceClient` extension method to register a `DataLakeServiceClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureDataLakeServiceClient("data-lake");
builder.AddAzureDataLakeFileSystemClient("data-lake-file-system");
```

You can then retrieve the `DataLakeServiceClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly DataLakeServiceClient _serviceClient;
private readonly DataLakeFileSystemClient _fileSystemClient;

public ProductsController(DataLakeServiceClient serviceClient, DataLakeFileSystemClient fileSystemClient)
{
    _serviceClient = serviceClient;
    _fileSystemClient = fileSystemClient
}
```

See the [Azure.Storage.Files.DataLake documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Files.DataLake/README.md) for examples on using `DataLakeServiceClient` and `DataLakeFileSystemClient`.

## Configuration

The .NET Aspire Azure DataLake Storage library provides multiple options to configure the Azure Data Lake Storage connection based on the requirements and conventions of your project. Note that either a `ServiceUri` or a `ConnectionString` is a required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureDataLakeServiceClient()`:

```csharp
builder.AddAzureDataLakeClientServiceClient("dataLakeConnectionName");
```

And then the connection information will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Service URI

The recommended approach is to use a ServiceUri, which works with the `AzureDataLakeSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "dataLakeConnectionName": "https://{account_name}.dfs.core.windows.net/",
    "dataLakeFileSystemConnectionName": "https://{account_name}.dfs.core.windows.net/;FileSystemName:{fileSystemName};"
  }
}
```

#### Connection string

Alternatively, an [Azure Storage connection string](https://learn.microsoft.com/azure/storage/common/storage-configure-connection-string) can be used.

```json
{
  "ConnectionStrings": {
    "dataLakeConnectionName": "AccountName=myaccount;AccountKey=myaccountkey",
    "dataLakeFileSystemConnectionName": "AccountName=myaccount;AccountKey=myaccountkey;FileSystemName:{fileSystemName};"
  }
}
```

### Use configuration providers

The .NET Aspire Azure Data Lake Storage library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureDataLakeSettings` and `DataLakeClientOptions` from configuration by using the `Aspire:Azure:Storage:Files:DataLake` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Azure": {
      "Storage": {
        "Files": {
          "DataLake": {
            "ServiceUri": "http://YOUR_URI",
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
}
```

### Use inline delegates

You can also pass the `Action<AzureDataLakeSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddAzureDataLakeServiceClient("data-lake", settings => settings.HealthChecks = false);
```

You can also setup the [DataLakeClientOptions](https://learn.microsoft.com/dotnet/api/azure.storage.files.datalake.datalakeclientoptions?view=azure-dotnet) using the optional `Action<IAzureClientBuilder<DataLakeServiceClient, DataLakeClientOptions>> configureClientBuilder` parameter of the `AddAzureDataLakeServiceClient` method. For example, to set the first part of "User-Agent" headers for all requests issues by this client:

```csharp
builder.AddAzureDataLakeServiceClient("dataLakeConnectionName", configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(options => options.Diagnostics.ApplicationId = "myapp"));
```

## AppHost extensions

In your AppHost project, install the Aspire Azure Storage Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Storage
```

Then, in the _AppHost.cs_ file of `AppHost`, add a DataLake Storage connection and consume the connection using the following methods:

```csharp
var storage = builder.AddAzureStorage("azure-storage");
var dataLake = builder.ExecutionContext.IsPublishMode
    ? storage.AddDataLake("data-lake")
    : builder.AddConnectionString("data-lake");

var fileSystem = builder.ExecutionContext.IsPublishMode
? storage.AddDataLakeFileSystem("data-lake-file-system");
: builder.AddConnectionString("data-lake-file-system");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(dataLake)
                       .WithReference(fileSystem);
```

The `AddDataLake` method adds an Azure DataLake storage service resource to the builder. Or `AddConnectionString` method can be used be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:data-lake` config key. The `WithReference` method passes that connection information into a connection string named `data-lake` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureDataLakeServiceClient("data-lake");
builder.AddAzureDataLakeFileSystemClient("data-lake-file-system");
```

## Additional documentation

* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Files.DataLake/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
