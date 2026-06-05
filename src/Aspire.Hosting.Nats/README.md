# Aspire.Hosting.NATS library

Provides extension methods and resource definitions for an Aspire AppHost to configure a NATS resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire NATS Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Nats
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a NATS resource and consume the connection using the following methods:

```csharp
var nats = builder.AddNats("nats");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(nats);
```

## Connection Properties

When you reference a NATS resource using `WithReference`, the following connection properties are made available to the consuming project:

### NATS server

The NATS server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the NATS server |
| `Port` | The port number the NATS server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI with the format `nats://{Username}:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `nats` becomes `NATS_URI`.

## Additional documentation

* https://learn.microsoft.com/dotnet/aspire/messaging/nats-component

## Feedback & contributing

https://github.com/dotnet/aspire
