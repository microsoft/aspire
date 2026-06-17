# Aspire.Microsoft.Azure.Cosmos library

Registers [CosmosClient](https://learn.microsoft.com/dotnet/api/microsoft.azure.cosmos.cosmosclient) as a singleton in the DI container for connecting to Azure Cosmos DB. Enables corresponding logging and telemetry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure Cosmos DB account - [create a Cosmos DB account](https://learn.microsoft.com/azure/cosmos-db/nosql/how-to-create-account)

### Install the package

Install the Aspire Microsoft Azure Cosmos DB library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Microsoft.Azure.Cosmos
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureCosmosClient` extension method to register a `CosmosClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureCosmosClient("cosmosConnectionName");
```

You can then retrieve the `CosmosClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly CosmosClient _client;

public ProductsController(CosmosClient client)
{
    _client = client;
}
```

See the [Azure Cosmos DB documentation](https://learn.microsoft.com/dotnet/api/microsoft.azure.cosmos.cosmosclient) for examples on using the `CosmosClient`.

## Configuration

The Aspire Azure Cosmos DB library provides multiple options to configure the Azure Cosmos DB connection based on the requirements and conventions of your project. Note that either an `AccountEndpoint` or a `ConnectionString` is a required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureCosmosClient()`:

```csharp
builder.AddAzureCosmosClient("cosmosConnectionName");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Account Endpoint

The recommended approach is to use an AccountEndpoint, which works with the `MicrosoftAzureCosmosSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "cosmosConnectionName": "https://{account_name}.documents.azure.com:443/"
  }
}
```

#### Connection string

Alternatively, an [Azure Cosmos DB connection string](https://learn.microsoft.com/azure/cosmos-db/nosql/how-to-dotnet-get-started#connect-with-a-connection-string) can be used.

```json
{
  "ConnectionStrings": {
    "cosmosConnectionName": "AccountEndpoint=https://{account_name}.documents.azure.com:443/;AccountKey={account_key};"
  }
}
```

### Use configuration providers

The Aspire Microsoft Azure Cosmos DB library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `MicrosoftAzureCosmosSettings` and `QueueClientOptions` from configuration by using the `Aspire:Microsoft:Azure:Cosmos` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Microsoft": {
      "Azure": {
        "Cosmos": {
          "DisableTracing": false,
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<MicrosoftAzureCosmosSettings> configureSettings` delegate to set up some or all the options inline, for example to disable tracing from code:

```csharp
builder.AddAzureCosmosClient("cosmosConnectionName", settings => settings.DisableTracing = true);
```

You can also setup the [CosmosClientOptions](https://learn.microsoft.com/dotnet/api/microsoft.azure.cosmos.cosmosclientoptions) using the optional `Action<CosmosClientOptions> configureClientOptions` parameter of the `AddAzureCosmosClient` method. For example, to set the `ApplicationName` "User-Agent" header suffix for all requests issues by this client:

```csharp
builder.AddAzureCosmosClient("cosmosConnectionName", configureClientOptions: clientOptions => clientOptions.ApplicationName = "myapp");
```

## AppHost extensions

In your AppHost project, install the Aspire Azure CosmosDB Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.CosmosDB
```

Then, in the _AppHost.cs_ file of `AppHost`, add a Cosmos DB connection and consume the connection using the following methods:

```csharp
var cosmosdb = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureCosmosDB("cdb").AddCosmosDatabase("cosmosdb")
    : builder.AddConnectionString("cosmosdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(cosmosdb);
```

The `AddAzureCosmosDB` method will add an Azure Cosmos DB resource to the builder. Or `AddConnectionString` can be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:cosmosdb` config key. The `WithReference` method passes that connection information into a connection string named `cosmosdb` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureCosmosClient("cosmosdb");
```

### Emulator usage

Aspire supports the usage of the Azure Cosmos DB emulator to use the emulator, add the following to your AppHost project:

```csharp
// AppHost
var cosmosdb = builder.AddAzureCosmosDB("cosmos").RunAsEmulator();
```

When the AppHost starts up a local container running the Azure CosmosDB will also be started:

```csharp
// Service code
builder.AddAzureCosmosClient("cosmos");
```

## Additional documentation

* https://learn.microsoft.com/azure/cosmos-db/nosql/sdk-dotnet-v3
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
