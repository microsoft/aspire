# Aspire.Hosting.Azure.Cosmos library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure CosmosDB.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the Aspire Azure Cosmos DB Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.CosmosDB
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

Then, in the _AppHost.cs_ file of `AppHost`, add a Cosmos DB connection and consume the connection using the following methods:

```csharp
var cosmosdb = builder.AddAzureCosmosDB("cdb").AddCosmosDatabase("cosmosdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(cosmosdb);
```

The `WithReference` method passes that connection information into a connection string named `cosmosdb` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using the client library [Aspire.Microsoft.Azure.Cosmos](https://www.nuget.org/packages/Aspire.Microsoft.Azure.Cosmos):

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

## Connection Properties

When you reference Azure Cosmos DB resources using `WithReference`, the following connection properties are made available to the consuming project:

### Cosmos DB account

The Cosmos DB account resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The account endpoint URI for the Cosmos DB account, with the format `https://mycosmosaccount.documents.azure.com:443/` |
| `AccountKey` | The account key for the Cosmos DB account (only available for emulator and access key authentication) |
| `ConnectionString` | **Emulator or access key authentication only.** A full connection string (includes account key for emulator; access key secret when access key auth is enabled). |

### Cosmos DB database

The Cosmos DB database resource inherits all properties from its parent Cosmos DB account and adds:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The name of the database |

### Cosmos DB container

The Cosmos DB container resource inherits all properties from its parent Cosmos DB database and adds:

| Property Name | Description |
|---------------|-------------|
| `ContainerName` | The name of the container |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://learn.microsoft.com/azure/cosmos-db/nosql/sdk-dotnet-v3
* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire
