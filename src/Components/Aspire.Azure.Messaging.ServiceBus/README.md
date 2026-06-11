# Aspire.Azure.Messaging.ServiceBus

Registers a [ServiceBusClient](https://learn.microsoft.com/dotnet/api/azure.messaging.servicebus.servicebusclient) in the DI container for connecting to Azure Service Bus.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure Service Bus namespace, learn more about how to [add a Service Bus namespace](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-dotnet-get-started-with-queues?#create-a-namespace-in-the-azure-portal). Alternatively, you can use a connection string, which is not recommended in production environments.

### Install the package

Install the Aspire Azure Service Bus library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Messaging.ServiceBus
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureServiceBusClient` extension method to register a `ServiceBusClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureServiceBusClient("sb");
```

You can then retrieve the `ServiceBusClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly ServiceBusClient _client;

public ProductsController(ServiceBusClient client)
{
    _client = client;
}
```

See the [Azure.Messaging.ServiceBus documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/servicebus/Azure.Messaging.ServiceBus/README.md) for examples on using the `ServiceBusClient`.

## Configuration

The Aspire Azure Service Bus library provides multiple options to configure the Azure Service Bus connection based on the requirements and conventions of your project. Note that either a `Namespace` or a `ConnectionString` is a required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureServiceBusClient()`:

```csharp
builder.AddAzureServiceBusClient("serviceBusConnectionName");
```

And then the connection information will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Fully Qualified Namespace

The recommended approach is to use a fully qualified namespace, which works with the `AzureMessagingServiceBusSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "serviceBusConnectionName": "{your_namespace}.servicebus.windows.net"
  }
}
```

#### Connection string

Alternatively, a connection string can be used.

```json
{
  "ConnectionStrings": {
    "serviceBusConnectionName": "Endpoint=sb://mynamespace.servicebus.windows.net/;SharedAccessKeyName=accesskeyname;SharedAccessKey=accesskey"
  }
}
```

### Use configuration providers

The Aspire Azure Service Bus library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureMessagingServiceBusSettings` and `ServiceBusClientOptions` from configuration by using the `Aspire:Azure:Messaging:ServiceBus` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Azure": {
      "Messaging": {
        "ServiceBus": {
          "HealthCheckQueueName": "myQueue",
          "DisableTracing": false,
          "ClientOptions": {
            "Identifier": "CLIENT_ID"
          }
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<AzureMessagingServiceBusSettings> configureSettings` delegate to set up some or all the options inline, for example to configure the health check queue name from code:

```csharp
builder.AddAzureServiceBusClient("sb", settings => settings.HealthCheckQueueName = "myQueue");
```

You can also setup the [ServiceBusClientOptions](https://learn.microsoft.com/dotnet/api/azure.messaging.servicebus.servicebusclientoptions) using the optional `Action<IAzureClientBuilder<ServiceBusClient, ServiceBusClientOptions>> configureClientBuilder` parameter of the `AddAzureServiceBusClient` method. For example, to set the client ID for this client:

```csharp
builder.AddAzureServiceBusClient("sb", configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(options => options.Identifier = "CLIENT_ID"));
```

## AppHost extensions

In your AppHost project, install the Aspire Azure Service Bus Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.ServiceBus
```

Then, in the _AppHost.cs_ file of `AppHost`, add a Service Bus connection and consume the connection using the following methods:

```csharp
var serviceBus = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureServiceBus("sb")
    : builder.AddConnectionString("sb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(serviceBus);
```

The `AddAzureServiceBus` method adds an Azure Service Bus Namespace to the builder. Or `AddConnectionString` can be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:sb` config key. The `WithReference` method passes that connection information into a connection string named `sb` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureServiceBusClient("sb");
```

## Additional documentation

* https://aspire.dev/integrations/cloud/azure/azure-service-bus/
* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/servicebus/Azure.Messaging.ServiceBus/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
