# Aspire.NATS.Net library

Registers [INatsConnection](https://nats-io.github.io/nats.net.v2/api/NATS.Client.Core.INatsConnection.html) in the DI container for connecting NATS server. Enables corresponding health check and logging.

## Getting started

### Prerequisites

- NATS server and the server URL for accessing the server.

### Install the package

Install the Aspire NATS library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.NATS.Net
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddNatsClient` extension method to register a `INatsConnection` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddNatsClient("nats");
```

You can then retrieve a `INatsConnection` instance using dependency injection. For example, to retrieve a connection from a Web API controller:

```csharp
private readonly INatsConnection _connection;

public ProductsController(INatsConnection connection)
{
    _connection = connection;
}
```

## Configuration

The Aspire NATS component provides multiple options to configure the NATS connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddNatsClient()`:

```csharp
builder.AddNatsClient("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "nats://nats:4222"
  }
}
```

See the [ConnectionString documentation](https://docs.nats.io/using-nats/developer/connecting#nats-url) for more information on how to format this connection string.

### Use configuration providers

The Aspire NATS component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `NatsClientSettings` from configuration by using the `Aspire:Nats:Client` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Nats": {
      "Client": {
        "DisableHealthChecks": true
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<NatsClientSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddNatsClient("nats", settings => settings.DisableHealthChecks = true);
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Nats` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Nats
```

Then, in the _AppHost.cs_ file of `AppHost`, register a NATS server and consume the connection using the following methods:

```csharp
var nats = builder.AddNats("nats");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(nats);
```

The `WithReference` method configures a connection in the `MyService` project named `nats`. In the _Program.cs_ file of `MyService`, the NATS connection can be consumed using:

```csharp
builder.AddNatsClient("nats");
```

## Additional documentation

* https://nats-io.github.io/nats.net.v2/documentation/intro.html
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
