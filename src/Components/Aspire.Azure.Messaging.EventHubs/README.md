# Aspire.Azure.Messaging.EventHubs

Offers options for registering an [EventHubProducerClient](https://learn.microsoft.com/dotnet/api/azure.messaging.eventhubs.producer.eventhubproducerclient), an [EventHubConsumerClient](https://learn.microsoft.com/dotnet/api/azure.messaging.eventhubs.consumer.eventhubconsumerclient), an [EventHubBufferedProducerClient](https://learn.microsoft.com/dotnet/api/azure.messaging.eventhubs.producer.eventhubbufferedproducerclient), an [EventProcessorClient](https://learn.microsoft.com/dotnet/api/azure.messaging.eventhubs.eventprocessorclient) or a [PartitionReceiver](https://learn.microsoft.com/dotnet/api/azure.messaging.eventhubs.primitives.partitionreceiver) in the DI container for connecting to Azure Event Hubs.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure Event Hubs namespace, learn more about how to [add an Event Hubs namespace](https://learn.microsoft.com/azure/event-hubs/event-hubs-create). Alternatively, you can use a connection string, which is not recommended in production environments.

### Install the package

Install the Aspire Azure Event Hubs library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Messaging.EventHubs
```

## Supported clients with Options classes

The following clients are supported by the library, along with their corresponding Options and Settings classes:

| Client Type                    | Options Class                         | Settings Class                                   |
|--------------------------------|---------------------------------------|--------------------------------------------------|
| EventHubProducerClient         | EventHubProducerClientOptions         | AzureMessagingEventHubsProducerSettings          |
| EventHubConsumerClient         | EventHubConsumerClientOptions         | AzureMessagingEventHubsConsumerSettings          |
| EventHubBufferedProducerClient | EventHubBufferedProducerClientOptions | AzureMessagingEventHubsBufferedProducerSettings  |
| EventProcessorClient           | EventProcessorClientOptions           | AzureMessagingEventHubsProcessorSettings         |
| PartitionReceiver              | PartitionReceiverOptions              | AzureMessagingEventHubsPartitionReceiverSettings |

## Usage example

The following example assumes that you have an Azure Event Hubs namespace and an Event Hub created and wish to configure an `EventHubProducerClient` to send events to the Event Hub. The `EventHubConsumerClient`, `EventProcessorClient`, and `PartitionReceiver`are configured in a similar manner.

In the _AppHost.cs_ file of your project, call the `AddAzureEventHubProducerClient` extension method to register
a `EventHubProducerClient` for use via the dependency injection container. The method takes a connection name parameter. This assumes you have included the `EntityPath` in the connection string to specify the Event Hub name.

```csharp
builder.AddAzureEventHubProducerClient("eventHubsConnectionName");
```

Retrieve the `EventHubProducerClient` instance using dependency injection. For example, to retrieve the
client from a Web API controller:

```csharp
private readonly EventHubProducerClient _client;

public ProductsController(EventHubProducerClient client)
{
    _client = client;
}
```

See the [Azure.Messaging.EventHubs documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/eventhub/Azure.Messaging.EventHubs/README.md) for examples on using the `EventHubProducerClient`.

## Configuration

The Aspire Azure Event Hubs library provides multiple options to configure the Azure Event Hubs connection based on the requirements and conventions of your project. Note that either a `FullyQualifiedNamespace` or a `ConnectionString` is a required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, provide the name of the connection string when calling `builder.AddAzureEventHubProducerClient()` and other supported Event Hubs clients. In this example, the connection string does not include the `EntityPath` property, so the `EventHubName` property must be set in the settings callback:

```csharp
builder.AddAzureEventHubProducerClient("eventHubsConnectionName",
    settings =>
    {
        settings.EventHubName = "MyHub";
    });
```

And then the connection information will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Fully Qualified Namespace

The recommended approach is to use a fully qualified namespace, which works with the `AzureMessagingEventHubsSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "eventHubsConnectionName": "{your_namespace}.servicebus.windows.net"
  }
}
```

#### Connection string

Alternatively, use a connection string:

```json
{
  "ConnectionStrings": {
    "eventHubsConnectionName": "Endpoint=sb://mynamespace.servicebus.windows.net/;SharedAccessKeyName=accesskeyname;SharedAccessKey=accesskey;EntityPath=MyHub"
  }
}
```

### Use configuration providers

The Aspire Azure Event Hubs library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureMessagingEventHubsSettings` and the associated Options, e.g. `EventProcessorClientOptions`, from configuration by using the `Aspire:Azure:Messaging:EventHubs:` key prefix, followed by the name of the specific client in use. Example `appsettings.json` that configures some of the options for an `EventProcessorClient`:

```json
{
  "Aspire": {
    "Azure": {
      "Messaging": {
        "EventHubs": {
          "EventProcessorClient": {
            "EventHubName": "MyHub",
            "BlobContainerName": "checkpoints",
            "ClientOptions": {
              "Identifier": "PROCESSOR_ID"
            }
          }
        }
      }
    }
  }
}
```

You can also setup the Options type using the optional `Action<IAzureClientBuilder<EventProcessorClient, EventProcessorClientOptions>> configureClientBuilder` parameter of the `AddAzureEventProcessorClient` method. For example, to set the processor's client ID for this client:

```csharp
builder.AddAzureEventProcessorClient("eventHubsConnectionName",
    configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(
        options => options.Identifier = "PROCESSOR_ID"));
```

## AppHost extensions

In your AppHost project, install the Aspire Azure Event Hubs Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.EventHubs
```

Then, in the _AppHost.cs_ file of `AppHost`, add an Event Hubs connection and an Event Hub resource and consume the connection using the following methods:

```csharp
var eventHubs = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureEventHubs("eventHubsConnectionName").WithHub("MyHub")
    : builder.AddConnectionString("eventHubsConnectionName");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(eventHubs);
```

The `AddAzureEventHubs` method adds an Azure Event Hubs Namespace resource to the builder. Or `AddConnectionString` can be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:eventHubsConnectionName` config key. The `WithReference` method passes that connection information into a connection string named `eventHubsConnectionName` in the `MyService` project.

NOTE: Even though we are creating an Event Hub using the `WithHub` at the same time as the namespace, for this release of Aspire, the connection string will not include the `EntityPath` property, so the `EventHubName` property must be set in the settings callback for the preferred client. Future versions of Aspire will include the `EntityPath` property in the connection string and will not require the `EventHubName` property to be set in this scenario.

In the _Program.cs_ file of `MyService`, the connection can be consumed using by calling of the supported Event Hubs client extension methods:

```csharp
builder.AddAzureEventProcessorClient("eventHubsConnectionName", settings =>
{
    settings.EventHubName = "MyHub";
});
```

## Additional documentation

* https://aspire.dev/integrations/cloud/azure/azure-event-hubs/
* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/eventhub/Microsoft.Azure.EventHubs/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
