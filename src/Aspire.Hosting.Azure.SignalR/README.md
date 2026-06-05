# Aspire.Hosting.Azure.SignalR library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure SignalR.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

Install the Aspire Azure SignalR Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.SignalR
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

In the _AppHost.cs_ file of `AppHost`, add a SignalR connection and consume the connection using the following methods:

```csharp
var signalR = builder.AddAzureSignalR("sr");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(signalR);
```

The `WithReference` method configures a connection in the `MyService` project named `sr`. In the _Program.cs_ file of `MyService`, the Azure SignalR connection can be consumed using the client library [Microsoft.Azure.SignalR](https://www.nuget.org/packages/Microsoft.Azure.SignalR):

```csharp
builder.Services.AddSignalR()
    .AddNamedAzureSignalR("sr");
```

## Connection Properties

When you reference an Azure SignalR resource using `WithReference`, the following connection properties are made available to the consuming project:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The connection URI for the SignalR service, with the format `https://{host}` in Azure (typically `https://<resource-name>.service.signalr.net`) or the emulator-provided endpoint when running locally |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `sr` becomes `SR_URI`.

## Additional documentation

* https://github.com/dotnet/aspire/tree/main/src/Components/README.md
* https://learn.microsoft.com/dotnet/aspire/real-time/azure-signalr-scenario
* https://learn.microsoft.com/azure/azure-signalr/signalr-overview

## Feedback & contributing

https://github.com/dotnet/aspire
