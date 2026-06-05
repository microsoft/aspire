# Aspire.Hosting.Azure.ServiceBus library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Service Bus.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

Install the Aspire Azure Service Bus Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.ServiceBus
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

In the _AppHost.cs_ file of `AppHost`, add a Service Bus connection and consume the connection using the following methods:

```csharp
var serviceBus = builder.AddAzureServiceBus("sb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(serviceBus);
```

The `WithReference` method passes that connection information into a connection string named `sb` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using the client library [Aspire.Azure.Messaging.ServiceBus](https://www.nuget.org/packages/Aspire.Azure.Messaging.ServiceBus):

```csharp
builder.AddAzureServiceBusClient("sb");
```

## Connection Properties

When you reference Azure Service Bus resources using `WithReference`, the following connection properties are made available to the consuming project:

### Service Bus namespace

The Service Bus namespace resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname of the Service Bus namespace |
| `Port` | The port of the Service Bus namespace when the emulator is used |
| `Uri` | The connection URI, with the format `sb://myservicebus.servicebus.windows.net` |
| `ConnectionString` | **Emulator only.** Includes SAS key material for the local emulator connection. |

### Service Bus queue

The Service Bus queue resource inherits all properties from its parent Service Bus namespace and adds:

| Property Name | Description |
|---------------|-------------|
| `QueueName` | The name of the queue |

### Service Bus topic

The Service Bus topic resource inherits all properties from its parent Service Bus namespace and adds:

| Property Name | Description |
|---------------|-------------|
| `TopicName` | The name of the topic |

### Service Bus subscription

The Service Bus subscription resource inherits all properties from its parent Service Bus topic and adds:

| Property Name | Description |
|---------------|-------------|
| `SubscriptionName` | The name of the subscription |
| `ConnectionString` | The connection string for the subscription |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/servicebus/Azure.Messaging.ServiceBus/README.md
* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire
