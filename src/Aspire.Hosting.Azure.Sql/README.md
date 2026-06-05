# Aspire.Hosting.Azure.Sql library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure SQL DB.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

Install the Aspire Azure SQL Server Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Sql
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

In the _AppHost.cs_ file of `AppHost`, register a SqlServer database and consume the connection using the following methods:

```csharp
var sql = builder.AddAzureSqlServer("sql")
                 .AddDatabase("sqldata");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(sql);
```

The `WithReference` method configures a connection in the `MyService` project named `sqldata`. In the _Program.cs_ file of `MyService`, the sql connection can be consumed using the client library [Aspire.Microsoft.Data.SqlClient](https://www.nuget.org/packages/Aspire.Microsoft.Data.SqlClient):

```csharp
builder.AddSqlServerClient("sqldata");
```

## Connection Properties

When you reference Azure SQL Server resources using `WithReference`, the following connection properties are made available to the consuming project:

### Azure SQL Server resource

The Azure SQL Server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The fully qualified domain name of the Azure SQL Server |
| `Port` | The SQL Server port (`1433` for Azure) |
| `Uri` | The connection URI, with the format `mssql://{Host}:{Port}` |
| `JdbcConnectionString` | JDBC connection string with the format `jdbc:sqlserver://{Host}:{Port};encrypt=true;trustServerCertificate=false`; |

### Azure SQL database resource

The Azure SQL database resource inherits all properties from its parent Azure SQL Server resource and adds:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The name of the database |
| `Uri` | The connection URI, with the format `mssql://{Host}:{Port}/{DatabaseName}` |
| `JdbcConnectionString` | JDBC connection string with the format `jdbc:sqlserver://{Host}:{Port};database={DatabaseName};encrypt=true;trustServerCertificate=false`; |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.
The client should add a valid authentication property for the JDBC connection string like `authentication=ActiveDirectoryDefault` or `authentication=ActiveDirectoryManagedIdentity`.

## Azure SQL DB defaults

Unless otherwise specified, the Azure SQL DB created will be a 2vCores General Purpose Serverless database (GP_S_Gen5_2) with the free offer enabled.

Read more about the free offer here: [Deploy Azure SQL Database for free](https://learn.microsoft.com/azure/azure-sql/database/free-offer?view=azuresql)

The free offer is configured so that when the maximum usage limit is reached, the database is stopped to avoid incurring in unexpected costs.

If you **don't want to use the free offer** and instead deploy the database use the default sku set by Azure, use the `WithAzureDefaultSku` method:

```csharp
var sql = builder.AddAzureSqlServer("sql")
                 .AddDatabase("db", "my-db-name")
                 .WithAzureDefaultSku();
```

## Setting a specific SKU

If you want to manually define what SKU must be used when deploying the Azure SQL DB resource, use the `ConfigureInfrastructure` method:

```csharp
var sqlSrv = builder.AddAzureSqlServer("sqlsrv")
    .ConfigureInfrastructure(infra => {
        var azureResources = infra.GetProvisionableResources();
        var azureDb = azureResources.OfType<SqlDatabase>().Single();
        azureDb.Sku = new SqlSku() { Name = "HS_Gen5_2" };
    })
    .AddDatabase("sqldb", "DatabaseName");
    .RunAsContainer();
```

## Additional documentation

* https://learn.microsoft.com/dotnet/framework/data/adonet/sql/
* https://learn.microsoft.com/dotnet/api/system.data.sqlclient.sqlconnection
* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire
