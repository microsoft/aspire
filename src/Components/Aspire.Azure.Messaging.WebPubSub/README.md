# Aspire.Azure.Messaging.WebPubSub

Registers a [WebPubSubServiceClient](https://learn.microsoft.com/dotnet/api/azure.messaging.webpubsub.webpubsubserviceclient) in the DI container for connecting to Azure Web PubSub.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- An existing Azure Web PubSub service instance, learn more about how to [Create a Web PubSub resource](https://learn.microsoft.com/azure/azure-web-pubsub/howto-develop-create-instance). Alternatively, you can use a connection string, which is not recommended in production environments.

### Install the package

Install the Aspire Azure Web PubSub library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Messaging.WebPubSub
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureWebPubSubHub` extension method to register a `WebPubSubServiceClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureWebPubSubServiceClient("wps1");
```

You can then retrieve the `WebPubSubServiceClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly WebPubSubServiceClient _client;

public ProductsController(WebPubSubServiceClient client)
{
    _client = client;
}
```

See the [Azure.Messaging.WebPubSub documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/webpubsub/Azure.Messaging.WebPubSub/README.md) for examples on using the `WebPubSubServiceClient`.

## Configuration

The Aspire Azure Web PubSub library provides multiple options to configure the Azure Web PubSub connection based on the requirements and conventions of your project. Note that either a `Endpoint` or a `ConnectionString` is a required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureWebPubSubHub()`:

```csharp
builder.AddAzureWebPubSubServiceClient("WebPubSubConnectionName", "your_hub_name");
```

And then the connection information will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Use the service endpoint

The recommended approach is to use the service endpoint, which works with the `AzureMessagingWebPubSubSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "WebPubSubConnectionName": "https://xxx.webpubsub.azure.com"
  }
}
```

#### Connection string

Alternatively, a connection string can be used.

```json
{
  "ConnectionStrings": {
    "WebPubSubConnectionName": "Endpoint=https://xxx.webpubsub.azure.com;AccessKey==xxxxxxx"
  }
}
```

### Use configuration providers

The Aspire Azure Web PubSub library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureMessagingWebPubSubSettings` and `WebPubSubServiceClientOptions` from configuration by using the `Aspire:Azure:Messaging:WebPubSub` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Azure": {
      "Messaging": {
        "WebPubSub": {
          "DisableHealthChecks": true,
          "HubName": "your_hub_name"
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<AzureMessagingWebPubSubSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddAzureWebPubSubServiceClient("wps", settings => settings.DisableHealthChecks = true);
```

You can also setup the [WebPubSubServiceClientOptions](https://learn.microsoft.com/dotnet/api/azure.messaging.WebPubSub.WebPubSubServiceClientoptions) using the optional `Action<IAzureClientBuilder<WebPubSubServiceClient, WebPubSubServiceClientOptions>> configureClientBuilder` parameter of the `AddAzureWebPubSubHub` method. For example, to set the client ID for this client:

```csharp
    builder.AddAzureWebPubSubServiceClient("wps", configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(options => options.Retry.MaxRetries = 5));
```

## AppHost extensions

In your AppHost project, add a Web PubSub connection and consume the connection using the following methods:

```csharp
var webPubSub = builder.AddAzureWebPubSub("wps");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(webPubSub);
```

The `AddAzureWebPubSubHub` method will read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:wps` config key. The `WithReference` method passes that connection information into a connection string named `wps` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureWebPubSubServiceClient("wps");
```

## Additional documentation

* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/webpubsub/Azure.Messaging.WebPubSub/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
