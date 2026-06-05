# Aspire.Hosting.Valkey library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Valkey cache resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Valkey Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Valkey
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Valkey resource and consume the connection using the following methods:

```csharp
var valkey = builder.AddValkey("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(valkey);
```

## Connection Properties

When you reference a Valkey resource using `WithReference`, the following connection properties are made available to the consuming project:

### Valkey

The Valkey resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Valkey server |
| `Port` | The port number the Valkey server is listening on |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `valkey://:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://valkey.io
* https://github.com/valkey-io/valkey/blob/unstable/README.md
* https://stackexchange.github.io/StackExchange.Redis/Basics
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
